using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.Automation.Domain;
using Aictiq.Modules.Automation.Endpoints;
using Aictiq.Modules.Tenancy.Domain;
using Aictiq.Modules.Tenancy.Endpoints;
using Aictiq.Modules.WorkItems.Domain;
using Aictiq.Modules.WorkItems.Endpoints;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Contracts;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Aictiq.IntegrationTests.Automation;

/// <summary>
/// Refining a ticket: a refine run is an ordinary factory run that works on the ticket rather
/// than the code. It claims the item where it stands, answers through <c>submit_refinement</c>,
/// and leaves the item's state alone when it ends; the person confirms the result.
/// </summary>
[Trait("Category", "Automation")]
public sealed class RefinementTests(PostgresFixture postgres, GarageFixture garage) : RunTestBase(postgres, garage)
{
    protected override Action<IDictionary<string, string?>>? Configure => settings =>
        settings["Automation:PollTimeoutSeconds"] = "2";

    private string ItemRefinement(string itemKey) => $"/api/v1/orgs/{Slug}/items/{itemKey}/refinement";

    [Fact]
    public async Task settings_start_off_and_the_starter_playbook_turns_refinement_on()
    {
        var initial = (await Owner.GetFromJsonAsync<RefinementSettingsView>($"{ProjectBase}/refinement-settings/", ApiTestContext.Json, Ct))!;
        Assert.False(initial.Enabled);

        var refused = await Owner.PostAsJsonAsync($"{ItemRefinement(ItemKey)}/", new RefineItemRequest(), ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("not set up", await refused.Content.ReadAsStringAsync(Ct));

        var enabled = await EnableAsync();
        Assert.True(enabled.Enabled);
        var playbooks = (await Owner.GetFromJsonAsync<List<PlaybookView>>($"{ProjectBase}/playbooks/", ApiTestContext.Json, Ct))!;
        var refine = Assert.Single(playbooks, playbook => playbook.Name == RefinementEndpoints.StarterName);
        Assert.Equal(refine.Id, enabled.PlaybookId);
        // A refine playbook is never the default an implement run would fall back to.
        Assert.False(refine.IsDefault);

        var again = await Owner.PostAsync($"{ProjectBase}/refinement-settings/starter-playbook", null, Ct);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Fact]
    public async Task settings_validate_what_they_name()
    {
        var response = await Owner.PutAsJsonAsync($"{ProjectBase}/refinement-settings/",
            new UpdateRefinementSettingsRequest(Guid.NewGuid(), "nobody", null, null, null, new string('x', 8001), Guid.NewGuid(), 0),
            ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.Contains("playbookId", body);
        Assert.Contains("agentId", body);
        Assert.Contains("refinedStateId", body);
        Assert.Contains("platforms", body);
    }

    [Fact]
    public async Task a_refine_run_claims_in_place_and_carries_the_project_context()
    {
        await EnableAsync(product: "A pet-sitting marketplace.", naming: "[APP - ...] for the mobile app");
        var before = await ItemAsync(ItemKey);

        var refinement = await RefineAsync(ItemKey);
        Assert.Equal(RefinementStatus.Refining, refinement.Status);

        // Claimed for the agent, but neither moved to Active nor assigned.
        var claimedItem = await ItemAsync(ItemKey);
        Assert.Equal(AgentId, claimedItem.ClaimedBy);
        Assert.Equal(before.StateId, claimedItem.StateId);
        Assert.Equal(before.AssigneeId, claimedItem.AssigneeId);

        var run = await RunAsync(refinement.LastRunId!.Value);
        Assert.Equal("refine", run.Kind);

        using var runner = RunnerClient(Runner.Secret);
        var claimed = await ClaimAsync(runner);
        Assert.Equal(run.Id, claimed.RunId);
        // An isolated clone of the default branch: no item branch is left for the implement run.
        Assert.True(claimed.WorkOnDefaultBranch);
        Assert.Equal("main", claimed.BranchName);
        Assert.Contains("refinement run", claimed.Prompt);
        Assert.Contains("submit_refinement", claimed.Prompt);
        Assert.Contains("A pet-sitting marketplace.", claimed.Prompt);
        Assert.Contains("[APP - ...]", claimed.Prompt);
        Assert.Contains("# Playbook: Refine", claimed.Prompt);
        // The budget that keeps a refine run from reading the codebase while a person waits.
        Assert.Contains("Work quickly", claimed.Prompt);
        Assert.Contains("Stop as soon as you can write the ticket", claimed.Prompt);
        Assert.DoesNotContain("open a pull request when the work is reviewable", claimed.Prompt);

        // A second request while it refines is refused, not queued behind it.
        var twice = await Owner.PostAsJsonAsync($"{ItemRefinement(ItemKey)}/", new RefineItemRequest(), ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.Conflict, twice.StatusCode);
    }

    [Fact]
    public async Task a_refine_run_takes_the_chosen_runner_and_harness()
    {
        await EnableAsync();

        var unknown = await Owner.PostAsJsonAsync($"{ItemRefinement(ItemKey)}/",
            new RefineItemRequest(Harness: "notepad"), ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Contains("harness", await unknown.Content.ReadAsStringAsync(Ct));

        var codexOnly = await RegisterRunnerAsync("codex-box");
        using var codexClient = RunnerClient(codexOnly.Secret);
        var capabilities = new RunnerCapabilities(1, [new RunnerHarness("codex", "1.0")], "linux", "x64", "0.3.0", 1);
        (await codexClient.PostAsJsonAsync("/api/v1/runner/hello",
            new RunnerHelloRequest(capabilities), ApiTestContext.Json, Ct)).EnsureSuccessStatusCode();

        // The refine playbook's harness is claude, which the codex runner does not report.
        var mismatch = await Owner.PostAsJsonAsync($"{ItemRefinement(ItemKey)}/",
            new RefineItemRequest(RunnerId: codexOnly.Runner.Id), ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);
        Assert.Null((await ItemAsync(ItemKey)).ClaimedBy);

        var response = await Owner.PostAsJsonAsync($"{ItemRefinement(ItemKey)}/",
            new RefineItemRequest(RunnerId: codexOnly.Runner.Id, Harness: "codex"), ApiTestContext.Json, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        var refinement = (await response.Content.ReadFromJsonAsync<RefinementView>(ApiTestContext.Json, Ct))!;
        var run = await RunAsync(refinement.LastRunId!.Value);
        Assert.Equal("codex", run.Harness);
        Assert.Equal(codexOnly.Runner.Id, run.RequestedRunnerId);

        var claim = await codexClient.PostAsJsonAsync("/api/v1/runner/runs/claim",
            new RunnerClaimRequest(["codex"], 1), ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
        var claimed = (await claim.Content.ReadFromJsonAsync<RunnerRunClaimed>(ApiTestContext.Json, Ct))!;
        Assert.Equal(run.Id, claimed.RunId);
        Assert.Equal("codex", claimed.Harness);
    }

    [Fact]
    public async Task questions_round_trip_and_the_answers_reach_the_next_run()
    {
        await EnableAsync();
        var first = await RefineAsync(ItemKey);
        using var runner = RunnerClient(Runner.Secret);
        var claimed = await ClaimAsync(runner);
        (await StartedAsync(runner, claimed.RunId)).EnsureSuccessStatusCode();

        await using (var agent = await ConnectAsync(claimed.AgentToken))
        {
            var empty = await agent.CallToolAsync("submit_refinement",
                new Dictionary<string, object?> { ["key"] = ItemKey, ["outcome"] = "needs_input" }, cancellationToken: Ct);
            Assert.True(empty.IsError);

            var asked = await agent.CallToolAsync("submit_refinement", new Dictionary<string, object?>
            {
                ["key"] = ItemKey, ["outcome"] = "needs_input",
                ["questions"] = new[] { "Which platform shows the bug?", "Is it every login or only after a timeout?" },
                ["summary"] = "The description does not say where it happens.",
            }, cancellationToken: Ct);
            Assert.NotEqual(true, asked.IsError);
        }

        var waiting = await GetRefinementAsync(ItemKey);
        Assert.Equal(RefinementStatus.NeedsInput, waiting.Status);
        Assert.Equal(2, waiting.Questions.Count);
        Assert.Equal("The description does not say where it happens.", waiting.Summary);

        (await FinishAsync(runner, claimed.RunId, RunOutcomes.Succeeded, "Asked two questions.")).EnsureSuccessStatusCode();
        // The run ending does not undo the answer it gave.
        Assert.Equal(RefinementStatus.NeedsInput, (await GetRefinementAsync(ItemKey)).Status);

        var finished = await StagedRunFinishedAsync(claimed.RunId);
        Assert.True(finished.Refinement);
        Assert.Null(finished.OnSuccessStateId);
        Assert.Null(finished.OnFailureStateId);

        // The WorkItems side leaves the item where it is - no fallback to Resolved - and lets it go.
        var before = await ItemAsync(ItemKey);
        await InvokeRunFinishedAsync(finished);
        var after = await ItemAsync(ItemKey);
        Assert.Equal(before.StateId, after.StateId);
        Assert.Null(after.ClaimedBy);
        Assert.Contains((await CommentsAsync(ItemKey)).Items, comment => comment.BodyMarkdown.StartsWith("Refinement run"));

        var second = await Owner.PostAsJsonAsync($"{ItemRefinement(ItemKey)}/", new RefineItemRequest(
            [new RefinementAnswer("Which platform shows the bug?", "iOS 17 on iPhone 15"),
             new RefinementAnswer("Is it every login or only after a timeout?", "")]),
            ApiTestContext.Json, Ct);
        Assert.True(second.IsSuccessStatusCode, await second.Content.ReadAsStringAsync(Ct));
        var refining = (await second.Content.ReadFromJsonAsync<RefinementView>(ApiTestContext.Json, Ct))!;
        Assert.Equal(first.Id, refining.Id);
        // A question left blank is one the person chose not to answer.
        var answered = Assert.Single(refining.Answered);
        Assert.Equal("iOS 17 on iPhone 15", answered.Answer);

        var next = await ClaimAsync(runner);
        Assert.Contains("Which platform shows the bug?", next.Prompt);
        Assert.Contains("iOS 17 on iPhone 15", next.Prompt);
    }

    [Fact]
    public async Task only_the_live_refine_runs_agent_may_submit()
    {
        await EnableAsync();
        var mcpToken = await CreateOwnerTokenAsync();
        await using (var person = await ConnectAsync(mcpToken))
        {
            var refused = await person.CallToolAsync("submit_refinement",
                new Dictionary<string, object?> { ["key"] = ItemKey, ["outcome"] = "ready" }, cancellationToken: Ct);
            Assert.Contains("no refinement in progress", Assert.Single(refused.Content.OfType<TextContentBlock>()).Text);
        }

        await RefineAsync(ItemKey);
        await using (var person = await ConnectAsync(mcpToken))
        {
            var refused = await person.CallToolAsync("submit_refinement",
                new Dictionary<string, object?> { ["key"] = ItemKey, ["outcome"] = "ready" }, cancellationToken: Ct);
            Assert.Contains("no refinement in progress", Assert.Single(refused.Content.OfType<TextContentBlock>()).Text);
        }
        Assert.Equal(RefinementStatus.Refining, (await GetRefinementAsync(ItemKey)).Status);
    }

    [Fact]
    public async Task a_run_that_never_answers_fails_the_refinement_and_ready_ones_confirm()
    {
        await EnableAsync();
        await RefineAsync(ItemKey);
        using var runner = RunnerClient(Runner.Secret);
        var claimed = await ClaimAsync(runner);
        (await StartedAsync(runner, claimed.RunId)).EnsureSuccessStatusCode();

        var tooEarly = await Owner.PostAsJsonAsync($"{ItemRefinement(ItemKey)}/confirm",
            new ConfirmRefinementRequest((await GetRefinementAsync(ItemKey)).Version), ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.Conflict, tooEarly.StatusCode);

        (await FinishAsync(runner, claimed.RunId, RunOutcomes.Succeeded, "Done.")).EnsureSuccessStatusCode();
        var failed = await GetRefinementAsync(ItemKey);
        Assert.Equal(RefinementStatus.Failed, failed.Status);
        Assert.Contains("without submitting", failed.Summary);

        // Try again; this time the agent rewrites the ticket and says it is ready.
        await InvokeRunFinishedAsync(await StagedRunFinishedAsync(claimed.RunId));
        await RefineAsync(ItemKey);
        var retry = await ClaimAsync(runner);
        await using (var agent = await ConnectAsync(retry.AgentToken))
        {
            var ready = await agent.CallToolAsync("submit_refinement", new Dictionary<string, object?>
            {
                ["key"] = ItemKey, ["outcome"] = "ready", ["summary"] = "Wrote steps, expected result and criteria.",
            }, cancellationToken: Ct);
            Assert.NotEqual(true, ready.IsError);
        }
        var view = await GetRefinementAsync(ItemKey);
        Assert.Equal(RefinementStatus.Ready, view.Status);

        var stale = await Owner.PostAsJsonAsync($"{ItemRefinement(ItemKey)}/confirm",
            new ConfirmRefinementRequest(view.Version + 1), ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var confirm = await Owner.PostAsJsonAsync($"{ItemRefinement(ItemKey)}/confirm",
            new ConfirmRefinementRequest(view.Version), ApiTestContext.Json, Ct);
        Assert.True(confirm.IsSuccessStatusCode, await confirm.Content.ReadAsStringAsync(Ct));
        var confirmed = (await confirm.Content.ReadFromJsonAsync<RefinementView>(ApiTestContext.Json, Ct))!;
        Assert.Equal(RefinementStatus.Confirmed, confirmed.Status);
        Assert.Equal(OwnerId, confirmed.ConfirmedBy);
    }

    [Fact]
    public async Task the_refined_state_is_reported_for_the_client_to_move_to()
    {
        var resolved = await StateIdAsync("In Review");
        var settings = await EnableAsync();
        var saved = await Owner.PutAsJsonAsync($"{ProjectBase}/refinement-settings/",
            new UpdateRefinementSettingsRequest(settings.PlaybookId, null, "Product", "Write crisply", "[X - ...]", "Web", resolved, settings.Version),
            ApiTestContext.Json, Ct);
        Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync(Ct));

        var refinement = await RefineAsync(ItemKey);
        Assert.Equal(resolved, refinement.RefinedStateId);
    }

    [Fact]
    public async Task stakeholders_cannot_read_or_change_refinement_even_as_project_admins()
    {
        await EnableAsync();
        var settings = (await Owner.GetFromJsonAsync<RefinementSettingsView>(
            $"{ProjectBase}/refinement-settings/", ApiTestContext.Json, Ct))!;
        await RefineAsync(ItemKey);
        using var runner = RunnerClient(Runner.Secret);
        var claimed = await ClaimAsync(runner);
        await using (var agent = await ConnectAsync(claimed.AgentToken))
        {
            await agent.CallToolAsync("submit_refinement",
                new Dictionary<string, object?> { ["key"] = ItemKey, ["outcome"] = "ready" }, cancellationToken: Ct);
        }
        var ready = await GetRefinementAsync(ItemKey);
        Assert.Equal(RefinementStatus.Ready, ready.Status);

        var stakeholder = await Context.RegisterAsync($"refine-stakeholder-{Guid.NewGuid():N}@test.local", "Sam", "Stake");
        await AddMemberAsync(stakeholder.User.Id, OrgRole.Member, canOperateFactory: false);
        (await Owner.PutAsJsonAsync($"{ProjectBase}/members/{stakeholder.User.Id}",
            new UpdateProjectMemberRequest(ProjectRole.Admin), ApiTestContext.Json, Ct)).EnsureSuccessStatusCode();
        using var client = Context.ClientFor(stakeholder);

        var read = await client.GetAsync($"{ItemRefinement(ItemKey)}/", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
        Assert.Equal(ProblemTypes.FactoryNotPermitted, await ProblemTypeAsync(read));

        var confirm = await client.PostAsJsonAsync($"{ItemRefinement(ItemKey)}/confirm",
            new ConfirmRefinementRequest(ready.Version), ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, confirm.StatusCode);
        Assert.Equal(ProblemTypes.FactoryNotPermitted, await ProblemTypeAsync(confirm));
        Assert.Equivalent(ready, await GetRefinementAsync(ItemKey));

        var readSettings = await client.GetAsync($"{ProjectBase}/refinement-settings/", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, readSettings.StatusCode);
        Assert.Equal(ProblemTypes.FactoryNotPermitted, await ProblemTypeAsync(readSettings));
        var saveSettings = await client.PutAsJsonAsync($"{ProjectBase}/refinement-settings/",
            new UpdateRefinementSettingsRequest(null, null, null, null, null, null, null, settings.Version),
            ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, saveSettings.StatusCode);
        Assert.Equal(ProblemTypes.FactoryNotPermitted, await ProblemTypeAsync(saveSettings));
        var starter = await client.PostAsync($"{ProjectBase}/refinement-settings/starter-playbook", null, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, starter.StatusCode);
        Assert.Equal(ProblemTypes.FactoryNotPermitted, await ProblemTypeAsync(starter));
        Assert.Equal(settings, await Owner.GetFromJsonAsync<RefinementSettingsView>(
            $"{ProjectBase}/refinement-settings/", ApiTestContext.Json, Ct));

        var other = await CreateItemAsync("Another thing");
        var refused = await client.PostAsJsonAsync($"{ItemRefinement(other.Key)}/", new RefineItemRequest(), ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(ProblemTypes.FactoryNotPermitted, await ProblemTypeAsync(refused));

        // Operators still get no content for a never-refined item; stakeholders cannot read it.
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"{ItemRefinement(other.Key)}/", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Owner.GetAsync($"{ItemRefinement(other.Key)}/", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/v1/orgs/{Slug}/items/{ItemKey}", Ct)).StatusCode);
    }

    [Fact]
    public async Task refinement_does_not_reveal_a_project_to_an_outsider()
    {
        using var stranger = Context.ClientFor(await Context.RegisterAsync($"refine-stranger-{Guid.NewGuid():N}@test.local"));
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"{ItemRefinement(ItemKey)}/", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"{ProjectBase}/refinement-settings/", Ct)).StatusCode);
    }

    [Fact]
    public async Task deleting_the_item_takes_its_refinement()
    {
        await EnableAsync();
        await RefineAsync(ItemKey);
        Assert.Equal(1, await ScalarAsync($"SELECT count(*) FROM automation.item_refinements WHERE item_id = '{Item.Id}'"));

        using var scope = Context.Factory.Services.CreateScope();
        var handler = ActivatorUtilities.CreateInstance<Aictiq.Modules.Automation.Events.AutomationWorkItemsDeletedHandler>(scope.ServiceProvider);
        await handler.HandleAsync(new WorkItemsDeleted(OrganizationId, Project.Id, [Item.Id], [], OwnerId), Ct);

        Assert.Equal(0, await ScalarAsync($"SELECT count(*) FROM automation.item_refinements WHERE item_id = '{Item.Id}'"));
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────

    private async Task<RefinementSettingsView> EnableAsync(string? product = null, string? naming = null)
    {
        var starter = await Owner.PostAsync($"{ProjectBase}/refinement-settings/starter-playbook", null, Ct);
        Assert.True(starter.IsSuccessStatusCode, await starter.Content.ReadAsStringAsync(Ct));
        var settings = (await starter.Content.ReadFromJsonAsync<RefinementSettingsView>(ApiTestContext.Json, Ct))!;
        if (product is null && naming is null) return settings;

        var saved = await Owner.PutAsJsonAsync($"{ProjectBase}/refinement-settings/",
            new UpdateRefinementSettingsRequest(settings.PlaybookId, null, product, null, naming, null, null, settings.Version),
            ApiTestContext.Json, Ct);
        Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync(Ct));
        return (await saved.Content.ReadFromJsonAsync<RefinementSettingsView>(ApiTestContext.Json, Ct))!;
    }

    private async Task<RefinementView> RefineAsync(string itemKey)
    {
        var response = await Owner.PostAsJsonAsync($"{ItemRefinement(itemKey)}/", new RefineItemRequest(), ApiTestContext.Json, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<RefinementView>(ApiTestContext.Json, Ct))!;
    }

    private async Task<RefinementView> GetRefinementAsync(string itemKey) =>
        (await Owner.GetFromJsonAsync<RefinementView>($"{ItemRefinement(itemKey)}/", ApiTestContext.Json, Ct))!;

    private async Task<string> CreateOwnerTokenAsync()
    {
        var response = await Owner.PostAsJsonAsync("/api/v1/me/tokens",
            new Aictiq.Modules.Identity.Endpoints.CreateAccessTokenRequest("refine", [Scopes.Mcp, Scopes.Read, Scopes.Write], OrganizationId, null),
            ApiTestContext.Json, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<Aictiq.Modules.Identity.Endpoints.AccessTokenCreated>(ApiTestContext.Json, Ct))!.Secret;
    }

    private async Task<McpClient> ConnectAsync(string token)
    {
        var http = Context.Factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, "/mcp") },
            http, loggerFactory: null, ownsHttpClient: true);
        return await McpClient.CreateAsync(transport, cancellationToken: Ct);
    }
}

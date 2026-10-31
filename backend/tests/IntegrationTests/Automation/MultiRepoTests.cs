using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.Automation;
using Aictiq.Modules.Automation.Domain;
using Aictiq.Modules.Automation.Endpoints;
using Aictiq.Modules.Automation.Workers;
using Aictiq.Modules.WorkItems.Endpoints;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Aictiq.IntegrationTests.Automation;

/// <summary>
/// Multiple repositories and monorepo sub-packages on a hosted instance: only a paid organization
/// may turn them on; a ticket's <c>repo:</c> labels choose the checkouts, which go only to a runner
/// that has every one of them; a playbook's graph runs a later repository after an earlier one's
/// pull request merges; and a project that downgrades falls back to its primary repository.
/// </summary>
[Trait("Category", "Automation")]
public sealed class MultiRepoTests(PostgresFixture postgres, GarageFixture garage) : RunTestBase(postgres, garage)
{
    protected override Action<IDictionary<string, string?>>? Configure => settings =>
    {
        settings["Billing:Mode"] = "saas";
        settings["Automation:PollTimeoutSeconds"] = "1";
    };

    private string SettingsUrl => $"{ProjectBase}/factory-settings";

    private async Task<FactorySettingsView> SettingsAsync() =>
        (await Owner.GetFromJsonAsync<FactorySettingsView>(SettingsUrl, ApiTestContext.Json, Ct))!;

    private async Task<HttpResponseMessage> PutAsync(
        bool? enabled = null, IReadOnlyList<ProjectRepositoryView>? additional = null, string? primaryWorkingDirectory = null)
    {
        var current = await SettingsAsync();
        return await Owner.PutAsJsonAsync(SettingsUrl,
            new UpdateFactorySettingsRequest(current.RepoSource, current.RepoFullName, current.DefaultBranch,
                current.LocalPathHint, current.DefaultAgentId, current.Version, current.DefaultRunnerId,
                enabled, null, primaryWorkingDirectory, additional),
            ApiTestContext.Json, Ct);
    }

    private Task SeedPaidSubscriptionAsync() => ExecuteSqlAsync($"""
        INSERT INTO billing.subscriptions (id, organization_id, plan, stripe_customer_id, stripe_subscription_id, status,
            cancel_at_period_end, seats_human, seats_agent, created_at, updated_at)
        VALUES (gen_random_uuid(), '{OrganizationId}', 'hosted', 'cus_multi', 'sub_multi', 4, false, 1, 0, now(), now())
        """);

    /// <summary>A paid organization whose project has the primary <c>acme</c> (/srv/acme) and a local <c>api</c>.</summary>
    private async Task EnableAsync()
    {
        await SeedPaidSubscriptionAsync();
        var saved = await PutAsync(true, [new ProjectRepositoryView("api", 1, null, "/srv/api", "develop", "packages/api")]);
        Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync(Ct));
    }

    private async Task HelloAsync(string secret, bool multiRepo = true, IReadOnlyList<string>? workspaces = null)
    {
        using var client = RunnerClient(secret);
        var capabilities = new RunnerCapabilities(1, [new RunnerHarness("claude", "2.1.0")], "linux", "x64", "0.18.0",
            1, Workspaces: workspaces ?? ["WEB", "WEB/api"], MultiRepo: multiRepo ? true : null);
        var response = await client.PostAsJsonAsync("/api/v1/runner/hello", new RunnerHelloRequest(capabilities), ApiTestContext.Json, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    private async Task LabelAsync(string itemKey, string name)
    {
        var created = await Owner.PostAsJsonAsync($"{ProjectBase}/labels", new CreateLabelRequest(name, null, null, null), ApiTestContext.Json, Ct);
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync(Ct));
        var label = (await created.Content.ReadFromJsonAsync<LabelView>(ApiTestContext.Json, Ct))!;
        (await Owner.PutAsync($"/api/v1/orgs/{Slug}/items/{itemKey}/labels/{label.Id}", null, Ct)).EnsureSuccessStatusCode();
    }

    private async Task SetStepsAsync(params RepositoryStep[] steps)
    {
        var playbook = (await Owner.GetFromJsonAsync<PlaybookView>($"{ProjectBase}/playbooks/{PlaybookId}", ApiTestContext.Json, Ct))!;
        var saved = await Owner.PatchAsJsonAsync($"{ProjectBase}/playbooks/{PlaybookId}",
            new UpdatePlaybookRequest { RepositorySteps = steps, Version = playbook.Version }, ApiTestContext.Json, Ct);
        Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync(Ct));
    }

    private async Task SettleAsync(Guid runId)
    {
        await InvokeRunFinishedAsync(await StagedRunFinishedAsync(runId));
        await ExecuteSqlAsync($"""
            UPDATE shared.outbox_messages SET processed_at = now()
            WHERE type = '{typeof(RunFinished).FullName}' AND payload::jsonb ->> 'RunId' = '{runId}'
            """);
    }

    private async Task SweepAsync() =>
        await ActivatorUtilities.CreateInstance<RunSweeper>(Context.Factory.Services).RunOnceAsync(Ct);

    private Task<HttpResponseMessage> FinishWithAsync(HttpClient runner, Guid runId, params string[] pullRequests) =>
        runner.PostAsJsonAsync($"/api/v1/runner/runs/{runId}/finish",
            new RunnerFinishRequest(RunOutcomes.Succeeded, 0, "Done.", pullRequests.FirstOrDefault(), null, null, null, null,
                PullRequestUrls: pullRequests),
            ApiTestContext.Json, Ct);

    [Fact]
    public async Task a_free_organization_is_offered_the_upgrade_and_refused_and_its_single_repository_saves_as_before()
    {
        var settings = await SettingsAsync();
        Assert.Equal("upgrade", settings.MultiRepoAvailability);
        Assert.False(settings.MultiRepoEnabled);
        Assert.Equal("acme", settings.PrimaryAlias);
        Assert.Empty(settings.AdditionalRepositories!);

        var refused = await PutAsync(true);
        Assert.Equal(HttpStatusCode.PaymentRequired, refused.StatusCode);
        Assert.Equal(ProblemTypes.PlanLimit, await ProblemTypeAsync(refused));
        Assert.Equal(HttpStatusCode.PaymentRequired,
            (await PutAsync(additional: [new ProjectRepositoryView("api", 1, null, "/srv/api", null, null)])).StatusCode);

        // The single-repository save, as every client sent it before, is untouched.
        var plain = await PutAsync();
        Assert.True(plain.IsSuccessStatusCode, await plain.Content.ReadAsStringAsync(Ct));
        Assert.False((await SettingsAsync()).MultiRepoEnabled);
    }

    [Fact]
    public async Task repositories_and_working_directories_are_validated_saved_and_listed()
    {
        await SeedPaidSubscriptionAsync();
        Assert.Equal("available", (await SettingsAsync()).MultiRepoAvailability);

        Assert.Equal(HttpStatusCode.BadRequest,
            (await PutAsync(true, [new ProjectRepositoryView("Bad Alias!", 1, null, "/srv/x", null, null)])).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await PutAsync(true, [new ProjectRepositoryView("acme", 1, null, "/srv/x", null, null)])).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await PutAsync(true, [new ProjectRepositoryView("app", 0, null, null, null, null)])).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await PutAsync(true, [new ProjectRepositoryView("app", 1, null, "/srv/app", null, "../outside")])).StatusCode);

        var saved = await PutAsync(true,
            [
                new ProjectRepositoryView("frontend", 0, "acme/frontend", null, null, "./apps/web/"),
                new ProjectRepositoryView("api", 1, null, "/srv/api", "develop", null),
            ],
            primaryWorkingDirectory: "packages/core");
        Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync(Ct));
        var settings = await SettingsAsync();
        Assert.True(settings.MultiRepoEnabled);
        Assert.Equal("acme", settings.PrimaryAlias);
        Assert.Equal("packages/core", settings.PrimaryWorkingDirectory);
        Assert.Equal(["frontend", "api"], settings.AdditionalRepositories!.Select(repo => repo.Alias));
        Assert.Equal("apps/web", settings.AdditionalRepositories![0].WorkingDirectory);
        Assert.Equal("main", settings.AdditionalRepositories[0].DefaultBranch);
        Assert.Equal("develop", settings.AdditionalRepositories[1].DefaultBranch);

        // Null leaves the list; an empty one clears it.
        (await PutAsync()).EnsureSuccessStatusCode();
        Assert.Equal(2, (await SettingsAsync()).AdditionalRepositories!.Count);
        (await PutAsync(additional: [])).EnsureSuccessStatusCode();
        Assert.Empty((await SettingsAsync()).AdditionalRepositories!);
    }

    [Fact]
    public async Task playbook_repository_steps_refuse_unknown_waits_and_cycles()
    {
        var playbook = (await Owner.GetFromJsonAsync<PlaybookView>($"{ProjectBase}/playbooks/{PlaybookId}", ApiTestContext.Json, Ct))!;
        Assert.Empty(playbook.RepositorySteps!);
        async Task<HttpStatusCode> PatchAsync(params RepositoryStep[] steps)
        {
            var current = (await Owner.GetFromJsonAsync<PlaybookView>($"{ProjectBase}/playbooks/{PlaybookId}", ApiTestContext.Json, Ct))!;
            return (await Owner.PatchAsJsonAsync($"{ProjectBase}/playbooks/{PlaybookId}",
                new UpdatePlaybookRequest { RepositorySteps = steps, Version = current.Version }, ApiTestContext.Json, Ct)).StatusCode;
        }

        Assert.Equal(HttpStatusCode.BadRequest, await PatchAsync(new RepositoryStep("api", null, ["web"])));
        Assert.Equal(HttpStatusCode.BadRequest,
            await PatchAsync(new RepositoryStep("api", null, ["acme"]), new RepositoryStep("acme", null, ["api"])));
        Assert.Equal(HttpStatusCode.OK,
            await PatchAsync(new RepositoryStep("acme"), new RepositoryStep("api", "packages/api/", ["acme"], true)));
        var saved = (await Owner.GetFromJsonAsync<PlaybookView>($"{ProjectBase}/playbooks/{PlaybookId}", ApiTestContext.Json, Ct))!;
        Assert.Equal("packages/api", saved.RepositorySteps![1].WorkingDirectory);
        Assert.True(saved.RepositorySteps[1].WaitForMerge);
    }

    [Fact]
    public async Task a_ticket_tagged_with_two_repositories_checks_out_both_and_links_a_pull_request_in_each()
    {
        await EnableAsync();
        await LabelAsync(ItemKey, "repo:acme");
        await LabelAsync(ItemKey, "repo:api");

        var run = await DispatchAsync(ItemKey);
        Assert.Equal(["acme", "api"], run.Repositories);
        var prompt = (await RunAsync(run.Id)).PromptSnapshot!;
        Assert.Contains("`repo/api/`", prompt);
        Assert.Contains("one pull request per changed repository", prompt);
        Assert.Contains("several checkouts under `repo/`", prompt);

        // An older CLI checks out one repository, and a runner without api's clone cannot take it.
        var older = await RegisterRunnerAsync("old-box");
        await HelloAsync(older.Secret, multiRepo: false);
        var partial = await RegisterRunnerAsync("half-box");
        await HelloAsync(partial.Secret, workspaces: ["WEB"]);
        using (var olderClient = RunnerClient(older.Secret))
        {
            Assert.Equal(HttpStatusCode.NoContent, (await ClaimMaybeAsync(olderClient))!.StatusCode);
        }
        using (var partialClient = RunnerClient(partial.Secret))
        {
            Assert.Equal(HttpStatusCode.NoContent, (await ClaimMaybeAsync(partialClient))!.StatusCode);
        }
        await HelloAsync(Runner.Secret);
        var waiting = (await RunAsync(run.Id)).Waiting;
        Assert.NotNull(waiting);

        using var runner = RunnerClient(Runner.Secret);
        var claimed = await ClaimAsync(runner);
        Assert.Equal(run.Id, claimed.RunId);
        Assert.Equal(["acme", "api"], claimed.Repos!.Select(repo => repo.Alias));
        var api = claimed.Repos!.Single(repo => repo.Alias == "api");
        Assert.Equal(("local", "/srv/api", "develop", "packages/api", false),
            (api.Source, api.LocalPathHint, api.DefaultBranch, api.WorkingDirectory, api.Primary));
        Assert.True(claimed.Repos!.Single(repo => repo.Alias == "acme").Primary);

        Assert.Equal(HttpStatusCode.NoContent, (await StartedAsync(runner, run.Id)).StatusCode);
        var finished = await FinishWithAsync(runner, run.Id, "https://github.com/acme/acme/pull/1", "https://github.com/acme/api/pull/2");
        Assert.True(finished.IsSuccessStatusCode, await finished.Content.ReadAsStringAsync(Ct));
        var done = await RunAsync(run.Id);
        Assert.Equal(["https://github.com/acme/acme/pull/1", "https://github.com/acme/api/pull/2"], done.PullRequestUrls);
        var steps = Assert.Single(done.RepositorySteps!);
        Assert.Equal("succeeded", steps.State);

        await SettleAsync(run.Id);
        Assert.Equal(2, await ScalarAsync($"SELECT count(*) FROM work.item_links WHERE item_id = '{Item.Id}' AND kind = 2"));
    }

    [Fact]
    public async Task a_runner_that_cannot_find_a_checkout_hands_the_run_back_naming_it()
    {
        await EnableAsync();
        await LabelAsync(ItemKey, "repo:api");
        var run = await DispatchAsync(ItemKey);
        Assert.Equal(["api"], run.Repositories);
        await HelloAsync(Runner.Secret);
        using var runner = RunnerClient(Runner.Secret);
        Assert.Equal(run.Id, (await ClaimAsync(runner)).RunId);

        var wrong = await runner.PostAsJsonAsync($"/api/v1/runner/runs/{run.Id}/release",
            new RunnerReleaseRequest("no-local-repository", "frontend"), ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        var released = await runner.PostAsJsonAsync($"/api/v1/runner/runs/{run.Id}/release",
            new RunnerReleaseRequest("no-local-repository", "api"), ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.NoContent, released.StatusCode);
        Assert.Equal(1, await ScalarAsync($"SELECT cardinality(declined_runner_ids) FROM automation.runs WHERE id = '{run.Id}'"));
    }

    [Fact]
    public async Task a_backend_then_frontend_graph_starts_the_second_repository_only_after_the_first_pull_request_merges()
    {
        await EnableAsync();
        await SetStepsAsync(new RepositoryStep("acme"), new RepositoryStep("api", null, ["acme"], true));
        await HelloAsync(Runner.Secret);
        using var runner = RunnerClient(Runner.Secret);

        var first = await DispatchAsync(ItemKey);
        Assert.Equal(["acme"], first.Repositories);
        Assert.Contains("step 1 of 2", (await RunAsync(first.Id)).PromptSnapshot);
        var claimed = await ClaimAsync(runner);
        Assert.Equal(["acme"], claimed.Repos!.Select(repo => repo.Alias));
        Assert.Equal(HttpStatusCode.NoContent, (await StartedAsync(runner, first.Id)).StatusCode);
        const string backendPullRequest = "https://github.com/acme/acme/pull/9";
        (await FinishWithAsync(runner, first.Id, backendPullRequest)).EnsureSuccessStatusCode();
        Assert.True((await StagedRunFinishedAsync(first.Id)).Chained);
        await SettleAsync(first.Id);

        var view = await RunAsync(first.Id);
        Assert.Equal(["succeeded", "waiting-for-merge"], view.RepositorySteps!.Select(step => step.State));

        // Not merged yet: the sweeper leaves it waiting.
        await SweepAsync();
        Assert.Equal(1, await ScalarAsync("SELECT count(*) FROM automation.runs"));

        await ExecuteSqlAsync($"UPDATE work.item_links SET state = 'merged' WHERE url = '{backendPullRequest}'");
        await SweepAsync();
        var second = await ScalarAsync("SELECT count(*) FROM automation.runs WHERE 'api' = ANY(repository_aliases)");
        Assert.Equal(1, second);
        var next = await ClaimAsync(runner);
        Assert.Equal(["api"], next.Repos!.Select(repo => repo.Alias));
        Assert.Equal(first.BranchName, next.BranchName);
        var nextView = await RunAsync(next.RunId);
        Assert.Contains("step 2 of 2", nextView.PromptSnapshot);
        Assert.Equal(["succeeded", "running"], nextView.RepositorySteps!.Select(step => step.State));
    }

    [Fact]
    public async Task a_sub_package_run_keeps_the_single_checkout_and_starts_in_its_working_directory()
    {
        await SeedPaidSubscriptionAsync();
        (await PutAsync(true, primaryWorkingDirectory: "packages/web")).EnsureSuccessStatusCode();
        await HelloAsync(Runner.Secret, workspaces: ["WEB"]);

        var run = await DispatchAsync(ItemKey);
        Assert.Empty(run.Repositories!);
        Assert.Equal("packages/web", run.WorkingDirectory);
        Assert.Contains("You start in `packages/web`", (await RunAsync(run.Id)).PromptSnapshot);
        using var runner = RunnerClient(Runner.Secret);
        var claimed = await ClaimAsync(runner);
        Assert.Null(claimed.Repos);
        Assert.Equal("packages/web", claimed.WorkingDirectory);
    }

    [Fact]
    public async Task a_downgraded_project_keeps_its_repositories_but_runs_in_the_primary_one()
    {
        await EnableAsync();
        await LabelAsync(ItemKey, "repo:api");
        await ExecuteSqlAsync($"UPDATE billing.subscriptions SET plan = 'free' WHERE organization_id = '{OrganizationId}'");

        var settings = await SettingsAsync();
        Assert.Equal("upgrade", settings.MultiRepoAvailability);
        Assert.True(settings.MultiRepoEnabled);
        Assert.Single(settings.AdditionalRepositories!);
        // Saving the other settings still works, and keeps the configuration.
        (await PutAsync()).EnsureSuccessStatusCode();
        Assert.Single((await SettingsAsync()).AdditionalRepositories!);

        var run = await DispatchAsync(ItemKey);
        Assert.Empty(run.Repositories!);
        Assert.Null(run.WorkingDirectory);
        using var runner = RunnerClient(Runner.Secret);
        var claimed = await ClaimAsync(runner);
        Assert.Null(claimed.Repos);
        Assert.DoesNotContain("# Repositories", claimed.Prompt);
    }

    [Fact]
    public async Task project_chat_and_scheduled_runs_use_only_the_primary_repository()
    {
        await EnableAsync();
        await LabelAsync(ItemKey, "repo:api");
        var scheduled = await Owner.PostAsJsonAsync($"/api/v1/orgs/{Slug}/items/{ItemKey}/runs",
            new DispatchRunRequest(null, null, ScheduledFor: DateTimeOffset.UtcNow.AddHours(1)), ApiTestContext.Json, Ct);
        Assert.True(scheduled.StatusCode == HttpStatusCode.Created, await scheduled.Content.ReadAsStringAsync(Ct));
        var run = (await scheduled.Content.ReadFromJsonAsync<RunView>(ApiTestContext.Json, Ct))!;
        Assert.Empty(run.Repositories!);

        var chat = await Owner.PostAsJsonAsync($"{ProjectBase}/chat", new { question = "Where is the API?" }, ApiTestContext.Json, Ct);
        Assert.True(chat.StatusCode == HttpStatusCode.Created, await chat.Content.ReadAsStringAsync(Ct));
        Assert.Equal(0, await ScalarAsync("SELECT count(*) FROM automation.runs WHERE kind = 4 AND cardinality(repository_aliases) > 0"));
    }
}

/// <summary>A self-hosted install does not offer multiple repositories, and refuses to turn them on.</summary>
[Trait("Category", "Automation")]
public sealed class MultiRepoSelfHostedTests(PostgresFixture postgres, GarageFixture garage) : RunTestBase(postgres, garage)
{
    [Fact]
    public async Task the_option_is_hidden_and_the_api_refuses_it()
    {
        var url = $"{ProjectBase}/factory-settings";
        var settings = (await Owner.GetFromJsonAsync<FactorySettingsView>(url, ApiTestContext.Json, Ct))!;
        Assert.Equal("hidden", settings.MultiRepoAvailability);

        var refused = await Owner.PutAsJsonAsync(url,
            new UpdateFactorySettingsRequest(settings.RepoSource, settings.RepoFullName, settings.DefaultBranch,
                settings.LocalPathHint, settings.DefaultAgentId, settings.Version, MultiRepoEnabled: true),
            ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        using var problem = JsonDocument.Parse(await refused.Content.ReadAsStringAsync(Ct));
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty("multiRepoEnabled", out _));
    }
}

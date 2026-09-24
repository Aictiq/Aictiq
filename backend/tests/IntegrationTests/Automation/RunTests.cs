using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
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
using Aictiq.SharedKernel.Paging;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Aictiq.IntegrationTests.Automation;

public abstract class RunTestBase(PostgresFixture postgres, GarageFixture garage) : IAsyncLifetime
{
    protected const string Slug = "acme";

    private readonly PostgresFixture _postgres = postgres;
    private readonly GarageFixture _garage = garage;

    protected ApiTestContext Context { get; private set; } = null!;
    protected HttpClient Owner { get; private set; } = null!;
    protected string OwnerId { get; private set; } = null!;
    protected Guid OrganizationId { get; private set; }
    protected ProjectView Project { get; private set; } = null!;
    protected string AgentId { get; private set; } = null!;
    protected Guid PlaybookId { get; private set; }
    protected RunnerIssuedView Runner { get; private set; } = null!;
    protected WorkItemView Item { get; private set; } = null!;
    protected string ItemKey => Item.Key;
    protected string ProjectBase => $"/api/v1/orgs/{Slug}/projects/{Project.Key}";
    protected CancellationToken Ct => TestContext.Current.CancellationToken;

    protected virtual Action<IDictionary<string, string?>>? Configure => null;

    public async ValueTask InitializeAsync()
    {
        Context = await ApiTestContext.CreateAsync(_postgres, _garage, "runs", configure: Configure);

        var alice = await Context.RegisterAsync($"run-owner-{Guid.NewGuid():N}@test.local", "Alice", "Anderson");
        OwnerId = alice.User.Id;
        Owner = Context.ClientFor(alice);

        var organization = await Owner.PostAsJsonAsync("/api/v1/orgs",
            new CreateOrganizationRequest("Acme", Slug, null, null), ApiTestContext.Json, Ct);
        organization.EnsureSuccessStatusCode();
        OrganizationId = (await organization.Content.ReadFromJsonAsync<OrganizationView>(ApiTestContext.Json, Ct))!.Id;

        Project = await CreateProjectAsync("Web", "web", ProjectVisibility.Organization);
        AgentId = (await CreateAgentAsync("builder", [Project.Id])).UserId;

        var starter = await Owner.PostAsync($"{ProjectBase}/playbooks/starter", null, Ct);
        Assert.True(starter.StatusCode == HttpStatusCode.Created, await starter.Content.ReadAsStringAsync(Ct));
        PlaybookId = (await starter.Content.ReadFromJsonAsync<PlaybookView>(ApiTestContext.Json, Ct))!.Id;

        (await Owner.PutAsJsonAsync($"{ProjectBase}/factory-settings",
            new UpdateFactorySettingsRequest((short)ProjectRepositorySource.RunnerLocal, null, "main", "/srv/acme", AgentId, 0),
            ApiTestContext.Json, Ct)).EnsureSuccessStatusCode();

        Runner = await RegisterRunnerAsync("box-1");
        Item = await CreateItemAsync("Fix the login redirect");
    }

    public async ValueTask DisposeAsync()
    {
        Owner.Dispose();
        await Context.DisposeAsync();
    }

    protected async Task<ProjectView> CreateProjectAsync(string name, string key, ProjectVisibility visibility)
    {
        var response = await Owner.PostAsJsonAsync($"/api/v1/orgs/{Slug}/projects",
            new CreateProjectRequest(name, key, null, visibility, null, null), ApiTestContext.Json, Ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProjectView>(ApiTestContext.Json, Ct))!;
    }

    protected async Task<AgentView> CreateAgentAsync(string displayName, IReadOnlyList<Guid>? projectIds = null)
    {
        var response = await Owner.PostAsJsonAsync($"/api/v1/orgs/{Slug}/agents",
            new CreateAgentRequest(displayName, projectIds), ApiTestContext.Json, Ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AgentView>(ApiTestContext.Json, Ct))!;
    }

    protected async Task<RunnerIssuedView> RegisterRunnerAsync(string name)
    {
        var response = await Owner.PostAsJsonAsync($"/api/v1/orgs/{Slug}/runners",
            new CreateRunnerRequest(name), ApiTestContext.Json, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<RunnerIssuedView>(ApiTestContext.Json, Ct))!;
    }

    protected async Task<WorkItemView> CreateItemAsync(string title, string projectKey = "web")
    {
        var response = await Owner.PostAsJsonAsync($"/api/v1/orgs/{Slug}/projects/{projectKey}/items/",
            new CreateWorkItemRequest(WorkItemType.Bug, title, null, null, null, null, null, null, null, null, null, null, null, null),
            ApiTestContext.Json, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<WorkItemView>(ApiTestContext.Json, Ct))!;
    }

    protected async Task<RunView> DispatchAsync(string itemKey, Guid? playbookId = null, string? agentId = null)
    {
        var response = await Owner.PostAsJsonAsync($"/api/v1/orgs/{Slug}/items/{itemKey}/runs",
            new DispatchRunRequest(playbookId, agentId), ApiTestContext.Json, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<RunView>(ApiTestContext.Json, Ct))!;
    }

    protected HttpClient RunnerClient(string secret)
    {
        var client = Context.Factory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(40);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        return client;
    }

    protected HttpClient TokenClient(string secret)
    {
        var client = Context.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        return client;
    }

    protected async Task<RunnerRunClaimed> ClaimAsync(HttpClient runner)
    {
        var response = await runner.PostAsJsonAsync("/api/v1/runner/runs/claim",
            new RunnerClaimRequest(["claude"], 1), ApiTestContext.Json, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<RunnerRunClaimed>(ApiTestContext.Json, Ct))!;
    }

    protected async Task<HttpResponseMessage?> ClaimMaybeAsync(HttpClient runner)
    {
        try
        {
            return await runner.PostAsJsonAsync("/api/v1/runner/runs/claim",
                new RunnerClaimRequest(["claude"], 1), ApiTestContext.Json, Ct);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    protected Task<HttpResponseMessage> StartedAsync(HttpClient runner, Guid runId) =>
        runner.PostAsync($"/api/v1/runner/runs/{runId}/started", null, Ct);

    protected Task<HttpResponseMessage> BeatAsync(HttpClient runner, Guid runId) =>
        runner.PostAsync($"/api/v1/runner/runs/{runId}/heartbeat", null, Ct);

    protected Task<HttpResponseMessage> PostLogAsync(HttpClient runner, Guid runId, params (long Seq, string Text)[] chunks) =>
        runner.PostAsJsonAsync($"/api/v1/runner/runs/{runId}/log",
            new RunnerLogRequest(chunks.Select(chunk => new RunnerLogChunk(chunk.Seq, "stdout", chunk.Text, null)).ToList()),
            ApiTestContext.Json, Ct);

    protected Task<HttpResponseMessage> FinishAsync(HttpClient runner, Guid runId, string outcome,
        string? summary = null, string? pullRequestUrl = null, string? failureReason = null) =>
        runner.PostAsJsonAsync($"/api/v1/runner/runs/{runId}/finish",
            new RunnerFinishRequest(outcome, null, summary, pullRequestUrl, null, null, null, failureReason),
            ApiTestContext.Json, Ct);

    protected async Task<RunView> RunAsync(Guid runId) =>
        (await Owner.GetFromJsonAsync<RunView>($"/api/v1/orgs/{Slug}/runs/{runId}", ApiTestContext.Json, Ct))!;

    protected async Task<WorkItemView> ItemAsync(string itemKey) =>
        (await Owner.GetFromJsonAsync<WorkItemView>($"/api/v1/orgs/{Slug}/items/{itemKey}", ApiTestContext.Json, Ct))!;

    protected async Task<RunLogPage> LogAsync(Guid runId) =>
        (await Owner.GetFromJsonAsync<RunLogPage>($"/api/v1/orgs/{Slug}/runs/{runId}/log", ApiTestContext.Json, Ct))!;

    protected async Task<PagedResult<CommentView>> CommentsAsync(string itemKey) =>
        (await Owner.GetFromJsonAsync<PagedResult<CommentView>>(
            $"/api/v1/orgs/{Slug}/items/{itemKey}/comments/", ApiTestContext.Json, Ct))!;

    protected async Task<Guid> StateIdAsync(string name)
    {
        var workflows = (await Owner.GetFromJsonAsync<List<WorkflowView>>(
            $"{ProjectBase}/workflows/", ApiTestContext.Json, Ct))!;
        return workflows.Single(workflow => workflow.IsDefault).States.Single(state => state.Name == name).Id;
    }

    /// <summary>The RunFinished Automation actually committed for a run, as the outbox holds it.</summary>
    protected async Task<RunFinished> StagedRunFinishedAsync(Guid runId)
    {
        await using var connection = new NpgsqlConnection(Context.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "SELECT payload FROM shared.outbox_messages WHERE type = @type AND payload::jsonb ->> 'RunId' = @runId",
            connection);
        command.Parameters.AddWithValue("type", typeof(RunFinished).FullName!);
        command.Parameters.AddWithValue("runId", runId.ToString());
        var payload = Assert.IsType<string>(await command.ExecuteScalarAsync(Ct));
        return JsonSerializer.Deserialize<RunFinished>(payload)!;
    }

    protected async Task ExecuteSqlAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(Context.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }

    protected async Task<long> ScalarAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(Context.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync(Ct));
    }

    /// <summary>
    /// The evaluation a hosted organization is born with, written straight in:
    /// tests of what expiry <em>does</em> should not also depend on the handler that mints
    /// the window. A negative <paramref name="daysRemaining"/> is an expired one.
    /// </summary>
    protected Task SeedEvaluationAsync(int daysRemaining) => ExecuteSqlAsync($"""
        INSERT INTO billing.evaluations (id, organization_id, started_at, ends_at)
        VALUES (gen_random_uuid(), '{OrganizationId}',
                now() - interval '30 days', now() + interval '{daysRemaining} days')
        """);

    protected async Task InvokeRunFinishedAsync(RunFinished @event)
    {
        using var scope = Context.Factory.Services.CreateScope();
        var handler = ActivatorUtilities.CreateInstance<Aictiq.Modules.WorkItems.Events.RunFinishedHandler>(scope.ServiceProvider);
        await handler.HandleAsync(@event, Ct);
    }

    protected async Task<string> ProblemTypeAsync(HttpResponseMessage response)
    {
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return problem.RootElement.GetProperty("type").GetString()!;
    }

    protected async Task AddMemberAsync(string userId, OrgRole role, bool canOperateFactory)
    {
        await using var connection = new NpgsqlConnection(Context.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO tenancy.organization_members (organization_id, user_id, role, joined_at, can_operate_factory)
            VALUES (@org, @user, @role, now(), @operator)
            """, connection);
        command.Parameters.AddWithValue("org", OrganizationId);
        command.Parameters.AddWithValue("user", userId);
        command.Parameters.AddWithValue("role", (short)role);
        command.Parameters.AddWithValue("operator", canOperateFactory);
        await command.ExecuteNonQueryAsync(Ct);
    }
}

[Trait("Category", "Automation")]
public sealed class RunTests(PostgresFixture postgres, GarageFixture garage) : RunTestBase(postgres, garage)
{
    // An idle claim long-polls; a short poll keeps "nothing to claim" from costing 25 s a test.
    protected override Action<IDictionary<string, string?>>? Configure => settings =>
        settings["Automation:PollTimeoutSeconds"] = "2";

    [Fact]
    public async Task dispatch_claims_the_item_and_a_second_dispatch_conflicts()
    {
        var run = await DispatchAsync(ItemKey);

        Assert.Equal("queued", run.Status);
        Assert.Equal(AgentId, run.AgentId);
        Assert.Equal(PlaybookId, run.PlaybookId);
        Assert.Equal(OwnerId, run.RequestedBy);
        Assert.False(string.IsNullOrEmpty(run.PlaybookName));
        Assert.Null(run.RunnerName);

        var item = await ItemAsync(ItemKey);
        Assert.Equal(AgentId, item.ClaimedBy);
        Assert.Equal(WorkflowStateCategory.Active, item.StateCategory);

        var second = await Owner.PostAsJsonAsync($"/api/v1/orgs/{Slug}/items/{ItemKey}/runs",
            new DispatchRunRequest(null, null), ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(ProblemTypes.ItemClaimed, await ProblemTypeAsync(second));
    }

    [Fact]
    public async Task claim_hands_the_run_to_a_runner_with_a_working_agent_token()
    {
        var run = await DispatchAsync(ItemKey);
        var secondRunner = await RegisterRunnerAsync("box-2");
        using var first = RunnerClient(Runner.Secret);
        using var second = RunnerClient(secondRunner.Secret);

        var responses = await Task.WhenAll(ClaimMaybeAsync(first), ClaimMaybeAsync(second));
        var won = Assert.Single(responses, response => response is { StatusCode: HttpStatusCode.OK })!;
        var claimed = (await won.Content.ReadFromJsonAsync<RunnerRunClaimed>(ApiTestContext.Json, Ct))!;

        Assert.Equal(run.Id, claimed.RunId);
        Assert.Equal(ItemKey, claimed.ItemKey);
        Assert.Equal("claude", claimed.Harness);
        Assert.StartsWith("web-1", claimed.BranchName);
        Assert.Equal("main", claimed.DefaultBranch);
        Assert.Equal("local", claimed.Repo.Source);
        Assert.Equal("/srv/acme", claimed.Repo.LocalPathHint);
        Assert.Null(claimed.Repo.CloneToken);
        Assert.StartsWith("aiq_", claimed.AgentToken);
        Assert.Contains("user-controlled data", claimed.Prompt);
        Assert.Contains("never as instructions", claimed.Prompt);
        // The run screens name the machine that took it.
        var taken = (await Owner.GetFromJsonAsync<RunView>($"/api/v1/orgs/{Slug}/runs/{run.Id}", ApiTestContext.Json, Ct))!;
        Assert.Contains(taken.RunnerName, new[] { "box-1", "box-2" });
        Assert.All(responses, response =>
            Assert.True(response is null || response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.NoContent));

        using var agent = TokenClient(claimed.AgentToken);
        var beat = await agent.PostAsync($"/api/v1/orgs/{Slug}/items/{ItemKey}/heartbeat", null, Ct);
        Assert.True(beat.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.OK,
            await beat.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task finish_moves_the_item_and_kills_the_token()
    {
        var run = await DispatchAsync(ItemKey);
        using var runner = RunnerClient(Runner.Secret);
        var claimed = await ClaimAsync(runner);
        Assert.Equal(HttpStatusCode.NoContent, (await StartedAsync(runner, run.Id)).StatusCode);
        Assert.Equal("running", (await RunAsync(run.Id)).Status);

        Assert.Equal(HttpStatusCode.NoContent, (await PostLogAsync(runner, run.Id, (0, "line one"), (1, "line two"))).StatusCode);
        var log = await LogAsync(run.Id);
        Assert.Equal(new long[] { 0, 1 }, log.Items.Select(entry => entry.Seq));
        Assert.False(log.Truncated);
        // A retried batch that overlaps what was stored keeps the new line and skips the old.
        Assert.Equal(HttpStatusCode.NoContent, (await PostLogAsync(runner, run.Id, (1, "line two"), (2, "line three"))).StatusCode);
        Assert.Equal(new[] { "line one", "line two", "line three" }, (await LogAsync(run.Id)).Items.Select(entry => entry.Text));

        var pullRequestUrl = "https://github.com/acme/web/pull/7";
        var finished = await FinishAsync(runner, run.Id, RunOutcomes.Succeeded, "Shipped the fix.", pullRequestUrl);
        Assert.Equal(HttpStatusCode.OK, finished.StatusCode);
        Assert.Equal("succeeded", (await finished.Content.ReadFromJsonAsync<RunView>(ApiTestContext.Json, Ct))!.Status);

        var resolved = await StateIdAsync("Resolved");
        var staged = await StagedRunFinishedAsync(run.Id);
        Assert.Equal(RunOutcomes.Succeeded, staged.Outcome);
        Assert.Equal(pullRequestUrl, staged.PullRequestUrl);
        await InvokeRunFinishedAsync(staged);

        var item = await ItemAsync(ItemKey);
        Assert.Equal(resolved, item.StateId);
        Assert.Null(item.ClaimedBy);

        var comments = await CommentsAsync(ItemKey);
        Assert.Single(comments.Items);
        Assert.Contains("succeeded", comments.Items[0].BodyMarkdown);

        var links = (await Owner.GetFromJsonAsync<List<ItemLinkView>>(
            $"/api/v1/orgs/{Slug}/items/{ItemKey}/links", ApiTestContext.Json, Ct))!;
        Assert.Contains(links, link => link.Url == pullRequestUrl);

        // Org routes answer 404 before the challenge, so the session route is where a dead
        // token says so plainly.
        using var agent = TokenClient(claimed.AgentToken);
        var session = await agent.GetAsync("/api/v1/auth/session", Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, session.StatusCode);
        Assert.Equal(ProblemTypes.TokenRevoked, await ProblemTypeAsync(session));
    }

    [Fact]
    public async Task member_without_the_operator_flag_gets_403_on_dispatch_and_log_but_200_on_status()
    {
        var run = await DispatchAsync(ItemKey);

        var memberAuth = await Context.RegisterAsync($"stakeholder-{Guid.NewGuid():N}@test.local");
        using var member = Context.ClientFor(memberAuth);
        await AddMemberAsync(memberAuth.User.Id, OrgRole.Member, false);

        var dispatch = await member.PostAsJsonAsync($"/api/v1/orgs/{Slug}/items/{ItemKey}/runs",
            new DispatchRunRequest(null, null), ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, dispatch.StatusCode);
        Assert.Equal(ProblemTypes.FactoryNotPermitted, await ProblemTypeAsync(dispatch));

        var status = await member.GetAsync($"/api/v1/orgs/{Slug}/runs/{run.Id}", Ct);
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        var seen = (await status.Content.ReadFromJsonAsync<RunView>(ApiTestContext.Json, Ct))!;
        Assert.Null(seen.PromptSnapshot);
        Assert.Null(seen.FailureReason);

        var log = await member.GetAsync($"/api/v1/orgs/{Slug}/runs/{run.Id}/log", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, log.StatusCode);
        Assert.Equal(ProblemTypes.FactoryNotPermitted, await ProblemTypeAsync(log));

        Assert.Equal(HttpStatusCode.OK,
            (await member.GetAsync($"/api/v1/orgs/{Slug}/items/{ItemKey}/runs", Ct)).StatusCode);

        using var stranger = Context.ClientFor(await Context.RegisterAsync($"stranger-{Guid.NewGuid():N}@test.local"));
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/v1/orgs/{Slug}/runs/{run.Id}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/v1/orgs/{Slug}/runs/{run.Id}/log", Ct)).StatusCode);
    }

    [Fact]
    public async Task guest_gets_insufficient_role_on_dispatch()
    {
        var guestAuth = await Context.RegisterAsync($"guest-{Guid.NewGuid():N}@test.local");
        using var guest = Context.ClientFor(guestAuth);
        await AddMemberAsync(guestAuth.User.Id, OrgRole.Guest, false);

        var dispatch = await guest.PostAsJsonAsync($"/api/v1/orgs/{Slug}/items/{ItemKey}/runs",
            new DispatchRunRequest(null, null), ApiTestContext.Json, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, dispatch.StatusCode);
    }

    [Fact]
    public async Task runner_cannot_touch_another_runners_run()
    {
        var run = await DispatchAsync(ItemKey);
        using var holder = RunnerClient(Runner.Secret);
        await ClaimAsync(holder);
        var otherRunner = await RegisterRunnerAsync("box-2");
        using var other = RunnerClient(otherRunner.Secret);

        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"/api/v1/runner/runs/{run.Id}/started", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostLogAsync(other, run.Id, (0, "intruder"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"/api/v1/runner/runs/{run.Id}/heartbeat", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await FinishAsync(other, run.Id, RunOutcomes.Succeeded, "stolen")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"/api/v1/runner/runs/{run.Id}/repo-token", null, Ct)).StatusCode);
    }

    [Fact]
    public async Task finish_after_cancel_is_a_no_op()
    {
        var run = await DispatchAsync(ItemKey);
        using var runner = RunnerClient(Runner.Secret);
        await ClaimAsync(runner);
        Assert.Equal(HttpStatusCode.NoContent, (await StartedAsync(runner, run.Id)).StatusCode);

        var cancel = await Owner.PostAsync($"/api/v1/orgs/{Slug}/runs/{run.Id}/cancel", null, Ct);
        Assert.True(cancel.IsSuccessStatusCode, await cancel.Content.ReadAsStringAsync(Ct));
        Assert.True((await RunAsync(run.Id)).CancelRequested);

        var beat = await BeatAsync(runner, run.Id);
        Assert.Equal(HttpStatusCode.OK, beat.StatusCode);
        Assert.True((await beat.Content.ReadFromJsonAsync<RunnerHeartbeatView>(ApiTestContext.Json, Ct))!.CancelRequested);

        // The runner's finish acknowledges the cancel, whatever it reports: the item must not
        // move to the success state for work somebody asked to stop.
        var acknowledged = await FinishAsync(runner, run.Id, RunOutcomes.Succeeded, "finished anyway");
        Assert.Equal(HttpStatusCode.OK, acknowledged.StatusCode);
        Assert.Equal("cancelled", (await RunAsync(run.Id)).Status);

        var late = await FinishAsync(runner, run.Id, RunOutcomes.Succeeded, "too late");
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
        Assert.Equal("cancelled", (await RunAsync(run.Id)).Status);

        var staged = await StagedRunFinishedAsync(run.Id);
        Assert.Equal(RunOutcomes.Cancelled, staged.Outcome);
        await InvokeRunFinishedAsync(staged);
        var item = await ItemAsync(ItemKey);
        Assert.Null(item.ClaimedBy);
        Assert.NotEqual(WorkflowStateCategory.Resolved, item.StateCategory);
    }

    [Fact]
    public async Task runs_list_is_bounded_by_visible_projects()
    {
        var operatorAuth = await Context.RegisterAsync($"operator-{Guid.NewGuid():N}@test.local");
        using var operatorClient = Context.ClientFor(operatorAuth);
        await AddMemberAsync(operatorAuth.User.Id, OrgRole.Member, true);

        var secret = await CreateProjectAsync("Secret", "sec", ProjectVisibility.Private);
        var secretAgent = await CreateAgentAsync("sec-builder", [secret.Id]);
        var secretStarter = await Owner.PostAsync($"/api/v1/orgs/{Slug}/projects/sec/playbooks/starter", null, Ct);
        Assert.True(secretStarter.StatusCode == HttpStatusCode.Created, await secretStarter.Content.ReadAsStringAsync(Ct));
        var secretPlaybook = (await secretStarter.Content.ReadFromJsonAsync<PlaybookView>(ApiTestContext.Json, Ct))!.Id;
        var visibleItem = await CreateItemAsync("Second visible");
        var hiddenItem = await CreateItemAsync("Hidden work", secret.Key);

        var first = await DispatchAsync(ItemKey);
        var second = await DispatchAsync(visibleItem.Key);
        var hidden = await DispatchAsync(hiddenItem.Key, secretPlaybook, secretAgent.UserId);

        var listed = (await operatorClient.GetFromJsonAsync<PagedResult<RunView>>(
            $"/api/v1/orgs/{Slug}/runs", ApiTestContext.Json, Ct))!;
        Assert.Equal(2, listed.TotalCount);
        Assert.Equal(2, listed.Items.Count);
        Assert.Contains(listed.Items, r => r.Id == first.Id);
        Assert.Contains(listed.Items, r => r.Id == second.Id);

        var queued = (await operatorClient.GetFromJsonAsync<PagedResult<RunView>>(
            $"/api/v1/orgs/{Slug}/runs?status=queued", ApiTestContext.Json, Ct))!;
        Assert.Equal(2, queued.TotalCount);

        (await Owner.PostAsync($"/api/v1/orgs/{Slug}/runs/{second.Id}/cancel", null, Ct)).EnsureSuccessStatusCode();

        var stillQueued = (await operatorClient.GetFromJsonAsync<PagedResult<RunView>>(
            $"/api/v1/orgs/{Slug}/runs?status=queued", ApiTestContext.Json, Ct))!;
        Assert.Equal(new Guid[] { first.Id }, stillQueued.Items.Select(r => r.Id));

        var byItem = (await operatorClient.GetFromJsonAsync<PagedResult<RunView>>(
            $"/api/v1/orgs/{Slug}/runs?item={ItemKey}", ApiTestContext.Json, Ct))!;
        Assert.Equal(new Guid[] { first.Id }, byItem.Items.Select(r => r.Id));

        var ownerListed = (await Owner.GetFromJsonAsync<PagedResult<RunView>>(
            $"/api/v1/orgs/{Slug}/runs", ApiTestContext.Json, Ct))!;
        Assert.Equal(3, ownerListed.TotalCount);

        Assert.Equal(HttpStatusCode.NotFound,
            (await operatorClient.GetAsync($"/api/v1/orgs/{Slug}/runs/{hidden.Id}", Ct)).StatusCode);
    }

    [Fact]
    public async Task lost_runner_fails_the_run_revokes_the_token_and_releases_the_item()
    {
        var run = await DispatchAsync(ItemKey);
        using var runner = RunnerClient(Runner.Secret);
        var claimed = await ClaimAsync(runner);
        Assert.Equal(HttpStatusCode.NoContent, (await StartedAsync(runner, run.Id)).StatusCode);

        await ExecuteSqlAsync($"UPDATE automation.runs SET last_heartbeat_at = now() - interval '1 hour' WHERE id = '{run.Id}'");
        var sweeper = ActivatorUtilities.CreateInstance<Aictiq.Modules.Automation.Workers.RunSweeper>(Context.Factory.Services);
        await sweeper.RunOnceAsync(Ct);

        var failed = await RunAsync(run.Id);
        Assert.Equal("failed", failed.Status);
        Assert.Equal("runner-lost", failed.FailureReason);

        using var agent = TokenClient(claimed.AgentToken);
        var session = await agent.GetAsync("/api/v1/auth/session", Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, session.StatusCode);
        Assert.Equal(ProblemTypes.TokenRevoked, await ProblemTypeAsync(session));

        // The runner coming back finds its run over.
        Assert.Equal(HttpStatusCode.Conflict, (await BeatAsync(runner, run.Id)).StatusCode);

        await InvokeRunFinishedAsync(await StagedRunFinishedAsync(run.Id));
        Assert.Null((await ItemAsync(ItemKey)).ClaimedBy);

        // A second sweep settles nothing twice.
        await sweeper.RunOnceAsync(Ct);
        Assert.Equal("failed", (await RunAsync(run.Id)).Status);
    }

    [Fact]
    public async Task queued_run_can_be_cancelled_at_once()
    {
        var run = await DispatchAsync(ItemKey);

        var cancel = await Owner.PostAsync($"/api/v1/orgs/{Slug}/runs/{run.Id}/cancel", null, Ct);
        Assert.True(cancel.IsSuccessStatusCode, await cancel.Content.ReadAsStringAsync(Ct));

        Assert.Equal("cancelled", (await RunAsync(run.Id)).Status);

        await InvokeRunFinishedAsync(await StagedRunFinishedAsync(run.Id));
        Assert.Null((await ItemAsync(ItemKey)).ClaimedBy);

        // Nothing is left for a runner to claim.
        using var runner = RunnerClient(Runner.Secret);
        Assert.Equal(HttpStatusCode.NoContent, (await ClaimMaybeAsync(runner))!.StatusCode);
    }
}

[Trait("Category", "Automation")]
public sealed class RunLogLimitTests(PostgresFixture postgres, GarageFixture garage) : RunTestBase(postgres, garage)
{
    protected override Action<IDictionary<string, string?>>? Configure => settings =>
        settings["Automation:MaxLogBytes"] = "2048";

    [Fact]
    public async Task log_cap_returns_413()
    {
        var run = await DispatchAsync(ItemKey);
        using var runner = RunnerClient(Runner.Secret);
        await ClaimAsync(runner);
        Assert.Equal(HttpStatusCode.NoContent, (await StartedAsync(runner, run.Id)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent,
            (await PostLogAsync(runner, run.Id, (0, new string('a', 1500)))).StatusCode);

        var over = await PostLogAsync(runner, run.Id, (1, new string('b', 1500)));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, over.StatusCode);
        Assert.EndsWith("log-limit-exceeded", await ProblemTypeAsync(over));

        var log = await LogAsync(run.Id);
        Assert.Equal(new long[] { 0, int.MaxValue }, log.Items.Select(entry => entry.Seq));
        Assert.True(log.Truncated);
    }
}

public sealed record DispatchRunRequest(Guid? PlaybookId, string? AgentId);

public sealed record RunView(
    Guid Id, Guid ProjectId, Guid ItemId, string ItemKey, Guid? PlaybookId,
    string? AgentId, string? AgentName, string? RequestedBy, Guid? RunnerId, string Status,
    string Harness, Guid? PlaybookRevisionId, int MaxMinutes,
    DateTimeOffset QueuedAt, DateTimeOffset? AssignedAt, DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt, DateTimeOffset? LastHeartbeatAt, bool CancelRequested,
    string? OutcomeSummary, string? PullRequestUrl, int? ExitCode, decimal? CostUsd,
    long? InputTokens, long? OutputTokens, string? FailureReason, string? PromptSnapshot,
    uint Version, string? PlaybookName = null, string? RunnerName = null,
    Guid? RuleId = null, string? RuleName = null);

public sealed record RunLogPage(IReadOnlyList<RunLogEntry> Items, bool Truncated);

public sealed record RunLogEntry(long Seq, string Stream, string Text, DateTimeOffset? At);

public sealed record RunnerClaimRequest(string[] Harnesses, int Slots);

public sealed record RunnerRunRepo(string Source, string? RepoFullName, string? CloneToken, string? LocalPathHint);

public sealed record RunnerRunClaimed(
    Guid RunId, Guid ItemId, string ItemKey, Guid ProjectId, string ProjectKey,
    string OrganizationSlug, string Harness, string Prompt, Guid PlaybookRevisionId,
    RunnerRunRepo Repo, string DefaultBranch, string BranchName, int MaxMinutes,
    string AictiqUrl, string AgentToken, string AgentTokenDisplay, int HeartbeatIntervalSeconds);

public sealed record RunnerHeartbeatView(bool CancelRequested);

public sealed record RunnerLogRequest(IReadOnlyList<RunnerLogChunk> Chunks);

public sealed record RunnerLogChunk(long Seq, string Stream, string Text, DateTimeOffset? At);

public sealed record RunnerFinishRequest(
    string Outcome, int? ExitCode, string? Summary, string? PullRequestUrl,
    decimal? CostUsd, long? InputTokens, long? OutputTokens, string? FailureReason);

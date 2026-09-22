using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.Automation;
using Aictiq.Modules.Automation.Domain;
using Aictiq.Modules.Automation.Endpoints;
using Aictiq.Modules.Automation.Workers;
using Aictiq.Modules.Identity.Endpoints;
using Aictiq.Modules.WorkItems.Contracts;
using Aictiq.Modules.WorkItems.Endpoints;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Events;
using Aictiq.SharedKernel.Paging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Npgsql;

namespace Aictiq.IntegrationTests.Automation;

/// <summary>
/// A hosted organization whose evaluation ended starts no new agent work,
/// through any door, and the runs it has already handed out finish normally. The
/// evaluation row is written directly here: what is under test is the consequence of
/// expiry, not the handler that mints the window (that is <c>HostedOfferTests</c>).
/// </summary>
[Trait("Category", "Automation")]
[Collection("postgres")]
public sealed class BillingReadOnlyFactoryTests(PostgresFixture postgres, GarageFixture garage)
    : RunTestBase(postgres, garage)
{
    protected override Action<IDictionary<string, string?>>? Configure => settings =>
    {
        // Quotas and evaluations only exist on a hosted instance.
        settings["Billing:Mode"] = "saas";
        // An idle claim long-polls; a short poll keeps "nothing to claim" cheap.
        settings["Automation:PollTimeoutSeconds"] = "2";
    };

    [Fact]
    public async Task an_expired_evaluation_stops_dispatch_at_the_rest_mcp_rule_and_runner_doors()
    {
        // Everything that needs a writable organization happens first: the rule, the item
        // it will fire for, and the agent's own credential.
        var reviewState = await StateIdAsync("In Review");
        var rule = await CreateRuleAsync("Auto-verify", reviewState);
        var ruleItem = await CreateItemAsync("The rule's item");
        var beforeState = ruleItem.StateId;
        var transitioned = await TransitionAsync(ruleItem.Key, reviewState, ruleItem.Version);
        var transition = new WorkItemTransitioned(
            OrganizationId, Project.Id, transitioned.Id, transitioned.Key, beforeState, reviewState, OwnerId);
        var mcpItem = await CreateItemAsync("The MCP door's item");
        var mcpToken = await CreateTokenAsync([Scopes.Mcp, Scopes.Read, Scopes.Write]);

        await SeedEvaluationAsync(-1);

        // 1. REST.
        var rest = await Owner.PostAsJsonAsync($"/api/v1/orgs/{Slug}/items/{ItemKey}/runs",
            new DispatchRunRequest(null, null), ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.Conflict, rest.StatusCode);
        Assert.Equal(ProblemTypes.OrganizationReadOnly, await ProblemTypeAsync(rest));

        // 2. MCP - the same rules as REST, answered in words rather than a status code.
        await using (var mcp = await ConnectAsync(mcpToken))
        {
            var refused = await mcp.CallToolAsync("start_run", new Dictionary<string, object?> { ["key"] = mcpItem.Key },
                cancellationToken: Ct);
            var text = Assert.Single(refused.Content.OfType<TextContentBlock>()).Text;
            Assert.Contains("read-only", text, StringComparison.Ordinal);
        }

        // 3. A rule firing: recorded as a skip, with the reason that says which read-only
        //    this is - the project is neither archived nor gone.
        await InvokeRuleFiringAsync(transition);
        var firing = Assert.Single((await Owner.GetFromJsonAsync<List<RuleFiringView>>(
            $"{ProjectBase}/rules/{rule.Id}/firings", ApiTestContext.Json, Ct))!);
        Assert.Null(firing.RunId);
        Assert.Equal(RuleSkipReasons.OrganizationReadOnly, firing.SkipReason);

        // 4. The runner's own door: no work, rather than work it must not do.
        using var runner = RunnerClient(Runner.Secret);
        var claim = await runner.PostAsJsonAsync("/api/v1/runner/runs/claim",
            new RunnerClaimRequest(["claude"], 1), ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.NoContent, claim.StatusCode);

        // Nothing was started by any of them.
        Assert.Equal(0, await ScalarAsync("SELECT count(*) FROM automation.runs"));
    }

    [Fact]
    public async Task the_sweeper_cancels_queued_runs_only_and_lets_an_assigned_one_finish()
    {
        var secondItem = await CreateItemAsync("Queued and then withdrawn");
        var running = await DispatchAsync(ItemKey);
        using var runner = RunnerClient(Runner.Secret);
        var claimed = await ClaimAsync(runner);
        Assert.Equal(running.Id, claimed.RunId);
        Assert.Equal(HttpStatusCode.NoContent, (await StartedAsync(runner, running.Id)).StatusCode);
        var queued = await DispatchAsync(secondItem.Key);
        Assert.Equal("queued", (await RunAsync(queued.Id)).Status);

        await SeedEvaluationAsync(-1);
        await SweepAsync();

        // The queued one is withdrawn: nobody had taken it, and leaving it would hand it to
        // the next runner that polls.
        var cancelled = await RunAsync(queued.Id);
        Assert.Equal("cancelled", cancelled.Status);
        Assert.NotNull(cancelled.FinishedAt);
        Assert.Contains("read-only", cancelled.FailureReason!, StringComparison.Ordinal);
        var finished = await StagedRunFinishedAsync(queued.Id);
        Assert.Equal(RunOutcomes.Cancelled, finished.Outcome);
        // It was never claimed, so there is no per-run credential left behind either.
        Assert.Equal(0, await ScalarAsync($"SELECT count(*) FROM automation.runs WHERE id = '{queued.Id}' AND agent_token_id IS NOT NULL"));

        // The one already being worked on is untouched, and finishes normally - outcome
        // write and credential cleanup included.
        var live = await RunAsync(running.Id);
        Assert.Equal("running", live.Status);
        Assert.Equal(1, await ScalarAsync($"SELECT count(*) FROM automation.runs WHERE id = '{running.Id}' AND agent_token_id IS NOT NULL"));

        using var agent = TokenClient(claimed.AgentToken);
        Assert.Equal(HttpStatusCode.OK, (await agent.GetAsync($"/api/v1/orgs/{Slug}/items/{ItemKey}", Ct)).StatusCode);
        // The heartbeat answers 200 with the cancellation flag, and nothing about a
        // read-only organization asks this run to stop: it was already being worked on.
        var beat = await BeatAsync(runner, running.Id);
        Assert.Equal(HttpStatusCode.OK, beat.StatusCode);
        Assert.False((await beat.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("cancelRequested").GetBoolean());
        var done = await FinishAsync(runner, running.Id, RunOutcomes.Succeeded, "Shipped it anyway.", "https://example.test/pr/9");
        Assert.True(done.IsSuccessStatusCode, await done.Content.ReadAsStringAsync(Ct));
        Assert.Equal("succeeded", (await RunAsync(running.Id)).Status);
        // The token the run was given is revoked when it finishes, expiry or no expiry.
        var session = await agent.GetAsync("/api/v1/auth/session", Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, session.StatusCode);
        Assert.Equal(ProblemTypes.TokenRevoked, await ProblemTypeAsync(session));
        Assert.Equal(1, await ScalarAsync(
            "SELECT count(*) FROM identity.personal_access_tokens WHERE revoked_at IS NOT NULL"));

        // A second sweep has nothing left to do.
        await SweepAsync();
        Assert.Equal(1, await ScalarAsync("SELECT count(*) FROM automation.runs WHERE status = 5"));
    }

    // --------------------------------------------------------------------------- helpers

    private async Task SweepAsync()
    {
        var services = Context.Factory.Services;
        var sweeper = new RunSweeper(
            services.GetRequiredService<IServiceScopeFactory>(),
            services.GetRequiredService<NpgsqlDataSource>(),
            services.GetRequiredService<IOptions<AutomationOptions>>(),
            TimeProvider.System,
            NullLogger<RunSweeper>.Instance);
        await sweeper.RunOnceAsync(Ct);
    }

    private async Task<RuleView> CreateRuleAsync(string name, Guid triggerStateId)
    {
        var response = await Owner.PostAsJsonAsync($"{ProjectBase}/rules",
            new CreateRuleRequest(name, triggerStateId, null, PlaybookId, AgentId), ApiTestContext.Json, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<RuleView>(ApiTestContext.Json, Ct))!;
    }

    private async Task<WorkItemView> TransitionAsync(string itemKey, Guid toStateId, uint version)
    {
        var response = await Owner.PostAsJsonAsync($"/api/v1/orgs/{Slug}/items/{itemKey}/transition",
            new TransitionRequest(toStateId, version), ApiTestContext.Json, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<WorkItemView>(ApiTestContext.Json, Ct))!;
    }

    private async Task InvokeRuleFiringAsync(WorkItemTransitioned @event)
    {
        using var scope = Context.Factory.Services.CreateScope();
        var handler = ActivatorUtilities.CreateInstance<Aictiq.Modules.Automation.Events.RuleFiringHandler>(scope.ServiceProvider);
        await handler.HandleAsync(@event, Ct);
    }

    private async Task<string> CreateTokenAsync(IReadOnlyList<string> scopes)
    {
        var response = await Owner.PostAsJsonAsync("/api/v1/me/tokens",
            new CreateAccessTokenRequest("factory", scopes, OrganizationId, null), ApiTestContext.Json, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<AccessTokenCreated>(ApiTestContext.Json, Ct))!.Secret;
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

/// <summary>
/// Run-log retention is entitlement-aware: the Hosted plan's 90 days beats the
/// operator's configured 30, and a pruned log takes nothing else with it. The clock is
/// fixed so the boundary - exactly 90 days - is a fact rather than a coin flip.
/// </summary>
[Trait("Category", "Automation")]
[Collection("postgres")]
public sealed class RunLogRetentionEntitlementTests(PostgresFixture postgres, GarageFixture garage)
    : RunTestBase(postgres, garage)
{
    protected override Action<IDictionary<string, string?>>? Configure => settings =>
    {
        settings["Billing:Mode"] = "saas";
        settings["Automation:PollTimeoutSeconds"] = "2";
        // The operator's own window, which would prune all three of these runs. The plan's
        // is wider, and on a hosted organization the plan is what applies.
        settings["Retention:RunLogDays"] = "30";
    };

    [Fact]
    public async Task the_hosted_plans_ninety_days_wins_over_the_operators_thirty_and_keeps_everything_but_the_log()
    {
        await SeedEvaluationAsync(30);
        var now = DateTimeOffset.UtcNow;
        var old = await FinishedRunAsync("Pruned: finished 100 days ago");
        var boundary = await FinishedRunAsync("Kept: finished exactly 90 days ago");
        var recent = await FinishedRunAsync("Kept: finished 80 days ago");
        await SetFinishedAtAsync(old, now.AddDays(-100));
        await SetFinishedAtAsync(boundary, now.AddDays(-90));
        await SetFinishedAtAsync(recent, now.AddDays(-80));
        Assert.Equal(2, await ChunkCountAsync(old));

        await SweepAsync(now);

        // Older than the plan's window: the raw output is gone and cannot be bought back.
        Assert.Equal(0, await ChunkCountAsync(old));
        // `finished_at < cutoff` - exactly ninety days is not older than ninety days.
        Assert.Equal(2, await ChunkCountAsync(boundary));
        Assert.Equal(2, await ChunkCountAsync(recent));

        // Everything durable about the pruned run survives: it is the item's history.
        var pruned = await RunAsync(old);
        Assert.Equal("failed", pruned.Status);
        Assert.Equal("Did what it could.", pruned.OutcomeSummary);
        Assert.Equal("https://example.test/pr/1", pruned.PullRequestUrl);
        Assert.Equal("The tests did not pass.", pruned.FailureReason);
        Assert.NotNull(pruned.PlaybookRevisionId);
        Assert.Empty((await LogAsync(old)).Items);
        Assert.NotEmpty((await LogAsync(boundary)).Items);

        // A second sweep at the same instant changes nothing.
        await SweepAsync(now);
        Assert.Equal(0, await ChunkCountAsync(old));
        Assert.Equal(2, await ChunkCountAsync(boundary));
    }

    [Fact]
    public async Task without_an_entitlement_the_operators_own_window_still_applies()
    {
        // No evaluation and no subscription: Billing has no opinion, so the instance's
        // configured thirty days is what prunes - self-host behaviour, unchanged.
        var now = DateTimeOffset.UtcNow;
        var run = await FinishedRunAsync("Pruned by the operator's window");
        await SetFinishedAtAsync(run, now.AddDays(-40));

        await SweepAsync(now);

        Assert.Equal(0, await ChunkCountAsync(run));
        Assert.Equal("failed", (await RunAsync(run)).Status);
    }

    // --------------------------------------------------------------------------- helpers

    /// <summary>One run taken end to end by a runner, with two log lines and a full outcome.</summary>
    private async Task<Guid> FinishedRunAsync(string title)
    {
        var item = await CreateItemAsync(title);
        var run = await DispatchAsync(item.Key);
        using var runner = RunnerClient(Runner.Secret);
        var claimed = await ClaimAsync(runner);
        Assert.Equal(run.Id, claimed.RunId);
        Assert.Equal(HttpStatusCode.NoContent, (await StartedAsync(runner, run.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await PostLogAsync(runner, run.Id, (0, "line one"), (1, "line two"))).StatusCode);
        var finished = await FinishAsync(runner, run.Id, RunOutcomes.Failed,
            "Did what it could.", "https://example.test/pr/1", "The tests did not pass.");
        Assert.True(finished.IsSuccessStatusCode, await finished.Content.ReadAsStringAsync(Ct));
        return run.Id;
    }

    private async Task SweepAsync(DateTimeOffset now)
    {
        var services = Context.Factory.Services;
        var service = new RunLogRetentionService(
            services.GetRequiredService<IServiceScopeFactory>(),
            services.GetRequiredService<NpgsqlDataSource>(),
            Options.Create(new AutomationRetentionOptions { RunLogDays = 30 }),
            new FixedClock(now),
            NullLogger<RunLogRetentionService>.Instance);
        await service.RunOnceAsync(Ct);
    }

    private Task SetFinishedAtAsync(Guid runId, DateTimeOffset at) => ExecuteSqlAsync(
        $"UPDATE automation.runs SET finished_at = '{at:O}'::timestamptz WHERE id = '{runId}'");

    private Task<long> ChunkCountAsync(Guid runId) =>
        ScalarAsync($"SELECT count(*) FROM automation.run_log_chunks WHERE run_id = '{runId}'");
}

/// <summary>A clock that does not move, so "exactly 90 days" is a fact and not a coin flip.</summary>
internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

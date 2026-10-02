using System.Net;
using System.Net.Http.Json;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.Automation.Workers;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Paging;
using Microsoft.Extensions.DependencyInjection;

namespace Aictiq.IntegrationTests.Automation;

/// <summary>
/// Continuing a failed run: the session the runner reported, the continue run pinned to the
/// runner that kept the workspace, the automatic continue after a transient failure and its
/// limits, and the failure when the session can no longer be reached.
/// </summary>
[Trait("Category", "Automation")]
public sealed class RunContinuationTests(PostgresFixture postgres, GarageFixture garage) : RunTestBase(postgres, garage)
{
    protected override Action<IDictionary<string, string?>>? Configure => settings =>
    {
        // Quotas and evaluations only exist on a hosted instance.
        settings["Billing:Mode"] = "saas";
        // An idle claim long-polls; a short poll keeps "nothing to claim" cheap.
        settings["Automation:PollTimeoutSeconds"] = "2";
    };

    private Task<HttpResponseMessage> ContinueAsync(Guid runId) =>
        Owner.PostAsync($"/api/v1/orgs/{Slug}/runs/{runId}/continue", null, Ct);

    private async Task<RunView> ContinuedAsync(Guid runId)
    {
        var response = await ContinueAsync(runId);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<RunView>(ApiTestContext.Json, Ct))!;
    }

    /// <summary>Claims and starts the next run on <paramref name="runner"/>, then fails it as the runner would.</summary>
    private async Task<RunnerRunClaimed> FailAsync(
        HttpClient runner, Guid runId, string failureReason, string? sessionId = "sess-1")
    {
        var claimed = await ClaimAsync(runner);
        Assert.Equal(runId, claimed.RunId);
        Assert.Equal(HttpStatusCode.NoContent, (await StartedAsync(runner, runId)).StatusCode);
        var finish = await runner.PostAsJsonAsync($"/api/v1/runner/runs/{runId}/finish",
            new RunnerFinishRequest("failed", 1, "stopped", null, 0.40m, 1000, 100, failureReason, sessionId),
            ApiTestContext.Json, Ct);
        Assert.True(finish.IsSuccessStatusCode, await finish.Content.ReadAsStringAsync(Ct));
        // The outbox would release the item's claim within seconds; tests do it in line.
        await InvokeRunFinishedAsync(await StagedRunFinishedAsync(runId));
        return claimed;
    }

    private async Task SweepAsync()
    {
        var sweeper = ActivatorUtilities.CreateInstance<RunSweeper>(Context.Factory.Services);
        await sweeper.RunOnceAsync(Ct);
    }

    private Task MakeAutoContinueDueAsync() => ExecuteSqlAsync(
        "UPDATE automation.runs SET auto_continue_due_at = now() - interval '1 second' WHERE auto_continue_due_at IS NOT NULL");

    private Task<long> DueCountAsync() =>
        ScalarAsync("SELECT count(*) FROM automation.runs WHERE auto_continue_due_at IS NOT NULL");

    [Fact]
    public async Task a_failed_run_continues_its_session_on_the_runner_that_kept_it()
    {
        var other = await RegisterRunnerAsync("box-2");
        var run = await DispatchAsync(ItemKey);
        using var runner = RunnerClient(Runner.Secret);
        await FailAsync(runner, run.Id, "harness-exit-1");

        var failed = await RunAsync(run.Id);
        Assert.Equal("sess-1", failed.SessionId);
        Assert.True(failed.Continuable);
        // Not a transient failure: nothing is continued without a person.
        Assert.Equal(0, await DueCountAsync());

        var continued = await ContinuedAsync(run.Id);
        Assert.Equal(run.Id, continued.ContinuesRunId);
        Assert.Equal(Runner.Runner.Id, continued.RequestedRunnerId);
        Assert.False(continued.AutoContinued);
        Assert.Equal(failed.PromptSnapshot, continued.PromptSnapshot);

        // Only the runner with the session may take it - even though box-2 is idle.
        using var otherClient = RunnerClient(other.Secret);
        Assert.Equal(HttpStatusCode.NoContent, (await ClaimMaybeAsync(otherClient))!.StatusCode);
        var claimed = await ClaimAsync(runner);
        Assert.Equal(continued.Id, claimed.RunId);
        Assert.Equal(new RunResumeView(run.Id, "sess-1", "harness-exit-1"), claimed.Resume);

        var before = await RunAsync(run.Id);
        Assert.Equal(continued.Id, before.ContinuedByRunId);
        Assert.False(before.Continuable);
        Assert.Equal(HttpStatusCode.Conflict, (await ContinueAsync(run.Id)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await StartedAsync(runner, continued.Id)).StatusCode);
        var finish = await runner.PostAsJsonAsync($"/api/v1/runner/runs/{continued.Id}/finish",
            new RunnerFinishRequest("succeeded", 0, "done", null, 0.10m, 200, 20, null, "sess-1"),
            ApiTestContext.Json, Ct);
        Assert.True(finish.IsSuccessStatusCode, await finish.Content.ReadAsStringAsync(Ct));

        var detail = await RunAsync(continued.Id);
        Assert.NotNull(detail.Chain);
        Assert.Equal([run.Id, continued.Id], detail.Chain!.Select(link => link.Id));
        Assert.Equal(0.50m, detail.Chain!.Sum(link => link.CostUsd ?? 0));

        var list = (await Owner.GetFromJsonAsync<PagedResult<RunView>>(
            $"/api/v1/orgs/{Slug}/runs?item={ItemKey}", ApiTestContext.Json, Ct))!;
        Assert.Equal(continued.Id, list.Items.Single(item => item.Id == run.Id).ContinuedByRunId);
    }

    [Fact]
    public async Task runs_without_a_session_or_with_a_newer_run_cannot_be_continued()
    {
        var run = await DispatchAsync(ItemKey);
        using var runner = RunnerClient(Runner.Secret);
        await FailAsync(runner, run.Id, "workspace-failed", sessionId: null);
        Assert.False((await RunAsync(run.Id)).Continuable);
        Assert.Equal(HttpStatusCode.Conflict, (await ContinueAsync(run.Id)).StatusCode);

        // A retry is a fresh run, and it makes the older one stale.
        var retry = await DispatchAsync(ItemKey);
        await FailAsync(runner, retry.Id, "harness-exit-1");
        var older = await RunAsync(run.Id);
        Assert.True(older.Superseded);
        Assert.True((await RunAsync(retry.Id)).Continuable);
    }

    [Fact]
    public async Task a_transient_failure_continues_automatically_at_most_twice()
    {
        var run = await DispatchAsync(ItemKey);
        using var runner = RunnerClient(Runner.Secret);
        await FailAsync(runner, run.Id, "harness-rate-limited");
        Assert.Equal(1, await DueCountAsync());

        // Not before its backoff.
        await SweepAsync();
        Assert.Equal(1, await DueCountAsync());

        await MakeAutoContinueDueAsync();
        await SweepAsync();
        Assert.Equal(0, await DueCountAsync());
        var first = (await RunAsync(run.Id)).ContinuedByRunId!.Value;
        var firstRun = await RunAsync(first);
        Assert.True(firstRun.AutoContinued);
        // Queued on behalf of whoever started the chain.
        Assert.Equal(OwnerId, firstRun.RequestedBy);

        await FailAsync(runner, first, "harness-transient");
        await MakeAutoContinueDueAsync();
        await SweepAsync();
        var second = (await RunAsync(first)).ContinuedByRunId!.Value;
        Assert.True((await RunAsync(second)).AutoContinued);

        await FailAsync(runner, second, "harness-crashed");
        Assert.Equal(0, await DueCountAsync());
        var last = await RunAsync(second);
        Assert.Null(last.ContinuedByRunId);
        // The automatic budget is spent; a person may still continue it.
        Assert.True(last.Continuable);
        Assert.Equal(3, last.Chain!.Count);
    }

    [Theory]
    [InlineData("timed-out")]
    [InlineData("harness-error")]
    [InlineData("harness-exit-2")]
    public async Task time_limits_refusals_and_plain_failures_never_continue_automatically(string reason)
    {
        var run = await DispatchAsync(ItemKey);
        using var runner = RunnerClient(Runner.Secret);
        await FailAsync(runner, run.Id, reason);
        Assert.Equal(0, await DueCountAsync());
        // A timed-out run still offers the manual Continue.
        Assert.True((await RunAsync(run.Id)).Continuable);
    }

    [Fact]
    public async Task a_cancelled_run_never_continues()
    {
        var run = await DispatchAsync(ItemKey);
        using var runner = RunnerClient(Runner.Secret);
        await ClaimAsync(runner);
        Assert.Equal(HttpStatusCode.NoContent, (await StartedAsync(runner, run.Id)).StatusCode);
        (await Owner.PostAsync($"/api/v1/orgs/{Slug}/runs/{run.Id}/cancel", null, Ct)).EnsureSuccessStatusCode();
        (await runner.PostAsJsonAsync($"/api/v1/runner/runs/{run.Id}/finish",
            new RunnerFinishRequest("failed", null, null, null, null, null, null, "harness-rate-limited", "sess-1"),
            ApiTestContext.Json, Ct)).EnsureSuccessStatusCode();

        Assert.Equal("cancelled", (await RunAsync(run.Id)).Status);
        Assert.Equal(0, await DueCountAsync());
        Assert.False((await RunAsync(run.Id)).Continuable);
    }

    [Fact]
    public async Task a_heartbeat_records_the_session_so_a_swept_run_can_still_be_continued()
    {
        var run = await DispatchAsync(ItemKey);
        using var runner = RunnerClient(Runner.Secret);
        await ClaimAsync(runner);
        Assert.Equal(HttpStatusCode.NoContent, (await StartedAsync(runner, run.Id)).StatusCode);
        (await runner.PostAsJsonAsync($"/api/v1/runner/runs/{run.Id}/heartbeat",
            new { sessionId = "sess-beat" }, ApiTestContext.Json, Ct)).EnsureSuccessStatusCode();
        // A heartbeat without a body leaves it alone.
        (await BeatAsync(runner, run.Id)).EnsureSuccessStatusCode();

        await ExecuteSqlAsync($"UPDATE automation.runs SET started_at = now() - interval '1 day' WHERE id = '{run.Id}'");
        await SweepAsync();

        var timedOut = await RunAsync(run.Id);
        Assert.Equal("timedOut", timedOut.Status, ignoreCase: true);
        Assert.Equal("sess-beat", timedOut.SessionId);
        Assert.True(timedOut.Continuable);
    }

    [Fact]
    public async Task a_continue_whose_runner_is_gone_fails_with_session_unavailable()
    {
        var run = await DispatchAsync(ItemKey);
        using var runner = RunnerClient(Runner.Secret);
        await FailAsync(runner, run.Id, "harness-exit-1");
        var continued = await ContinuedAsync(run.Id);

        // Offline for long past its window, with the continue waiting as long.
        await ExecuteSqlAsync($"""
            UPDATE automation.runners SET last_seen_at = now() - interval '1 hour' WHERE id = '{Runner.Runner.Id}';
            UPDATE automation.runs SET queued_at = now() - interval '1 hour' WHERE id = '{continued.Id}';
            """);
        await SweepAsync();

        var failed = await RunAsync(continued.Id);
        Assert.Equal("failed", failed.Status);
        Assert.Equal("session-unavailable", failed.FailureReason);
        Assert.False(failed.Continuable);
    }

    [Fact]
    public async Task a_read_only_organization_continues_nothing()
    {
        var run = await DispatchAsync(ItemKey);
        using var runner = RunnerClient(Runner.Secret);
        await FailAsync(runner, run.Id, "harness-rate-limited");
        await SeedEvaluationAsync(-1);

        var refused = await ContinueAsync(run.Id);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(ProblemTypes.OrganizationReadOnly, await ProblemTypeAsync(refused));

        await MakeAutoContinueDueAsync();
        await SweepAsync();
        Assert.Null((await RunAsync(run.Id)).ContinuedByRunId);
        Assert.Equal(0, await DueCountAsync());
    }
}

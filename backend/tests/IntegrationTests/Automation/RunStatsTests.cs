using System.Net;
using System.Net.Http.Json;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.Automation.Endpoints;
using Aictiq.Modules.Tenancy.Domain;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Paging;

namespace Aictiq.IntegrationTests.Automation;

/// <summary>Factory → Runs statistics: the list's filters and visibility, aggregated over every matching run.</summary>
[Trait("Category", "Automation")]
public sealed class RunStatsTests(PostgresFixture postgres, GarageFixture garage) : RunTestBase(postgres, garage)
{
    // An idle claim long-polls; a short poll keeps "nothing to claim" from costing 25 s a test.
    protected override Action<IDictionary<string, string?>>? Configure => settings =>
        settings["Automation:PollTimeoutSeconds"] = "2";

    private string RunsBase => $"/api/v1/orgs/{Slug}/runs";

    private async Task<RunStats> StatsAsync(HttpClient client, string query = "")
    {
        var response = await client.GetAsync($"{RunsBase}/stats{query}", Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<RunStats>(ApiTestContext.Json, Ct))!;
    }

    private async Task<PagedResult<RunView>> ListAsync(HttpClient client, string query = "") =>
        (await client.GetFromJsonAsync<PagedResult<RunView>>($"{RunsBase}{query}", ApiTestContext.Json, Ct))!;

    /// <summary>Takes the item's run through a runner to the end, then pins its timings so the figures are exact.</summary>
    private async Task<RunView> FinishedRunAsync(
        string itemKey, string outcome, decimal cost, long input, long output, string? pullRequestUrl,
        string? failureReason, int waitSeconds, int durationSeconds)
    {
        var run = await DispatchAsync(itemKey);
        using var runner = RunnerClient(Runner.Secret);
        Assert.Equal(run.Id, (await ClaimAsync(runner)).RunId);
        Assert.Equal(HttpStatusCode.NoContent, (await StartedAsync(runner, run.Id)).StatusCode);
        var finished = await runner.PostAsJsonAsync($"/api/v1/runner/runs/{run.Id}/finish",
            new RunnerFinishRequest(outcome, 0, "done", pullRequestUrl, cost, input, output, failureReason),
            ApiTestContext.Json, Ct);
        Assert.True(finished.IsSuccessStatusCode, await finished.Content.ReadAsStringAsync(Ct));
        await ExecuteSqlAsync($"""
            UPDATE automation.runs
               SET queued_at = now() - interval '1 hour',
                   started_at = now() - interval '1 hour' + interval '{waitSeconds} seconds',
                   finished_at = now() - interval '1 hour' + interval '{waitSeconds + durationSeconds} seconds'
             WHERE id = '{run.Id}'
            """);
        return run;
    }

    [Fact]
    public async Task stats_match_a_manual_count_and_the_list_takes_the_same_filters()
    {
        var shipped = await FinishedRunAsync(ItemKey, "succeeded", 1.50m, 100, 20, "https://example.test/pr/1", null, 60, 600);
        var broken = await FinishedRunAsync((await CreateItemAsync("Broken build")).Key, "failed", 0.50m, 50, 10, null,
            "tests-failed", 120, 300);
        var waiting = await DispatchAsync((await CreateItemAsync("Still waiting")).Key);

        var stats = await StatsAsync(Owner, "?tz=Europe/Sarajevo");
        Assert.Equal(3, stats.Total);
        Assert.Equal(1, stats.Active);
        Assert.Equal(2, stats.Finished);
        Assert.Equal(1, stats.Succeeded);
        Assert.Equal(2.00m, stats.TotalCostUsd);
        Assert.Equal(1.00m, stats.AverageCostUsd);
        Assert.Equal(150, stats.InputTokens);
        Assert.Equal(30, stats.OutputTokens);
        Assert.Equal(450, stats.MedianDurationSeconds);
        Assert.Equal(570, stats.P90DurationSeconds);
        Assert.Equal(90, stats.MedianQueueWaitSeconds);
        Assert.Equal(1, stats.PullRequests);
        Assert.Equal(3, stats.Days.Sum(day => day.Runs));
        Assert.Equal(1, stats.Days.Where(day => day.Status == "failed").Sum(day => day.Runs));

        var group = Assert.Single(stats.Groups);
        Assert.Equal(AgentId, group.Key);
        Assert.Equal("builder", group.Name);
        Assert.Equal(3, group.Runs);
        Assert.Equal(450, group.MedianDurationSeconds);

        var failure = Assert.Single(stats.FailureReasons!);
        Assert.Equal(("tests-failed", 1), (failure.Reason, failure.Runs));
        Assert.Equal(PlaybookId, Assert.Single(stats.Playbooks).Id);
        Assert.Equal(Runner.Runner.Id, Assert.Single(stats.Runners).Id);

        var byProject = await StatsAsync(Owner, "?groupBy=project");
        Assert.Equal((Project.Key, "Web"), (byProject.Groups[0].Key, byProject.Groups[0].Name));
        var byRunner = await StatsAsync(Owner, "?groupBy=runner");
        Assert.Contains(byRunner.Groups, g => g.Key is null && g.Runs == 1);
        Assert.Contains(byRunner.Groups, g => g.Key == Runner.Runner.Id.ToString() && g.Name == "box-1" && g.Runs == 2);

        // Every drill-down the page offers narrows the stats and the list alike.
        var byFailure = $"?failure=tests-failed";
        Assert.Equal(1, (await StatsAsync(Owner, byFailure)).Total);
        Assert.Equal([broken.Id], (await ListAsync(Owner, byFailure)).Items.Select(run => run.Id));
        var byRunnerFilter = $"?runner={Runner.Runner.Id}&status=succeeded";
        Assert.Equal(1, (await StatsAsync(Owner, byRunnerFilter)).Total);
        Assert.Equal([shipped.Id], (await ListAsync(Owner, byRunnerFilter)).Items.Select(run => run.Id));
        Assert.Equal(3, (await ListAsync(Owner, $"?playbook={PlaybookId}&kind=implement")).TotalCount);
        Assert.Equal(0, (await ListAsync(Owner, "?kind=refine")).TotalCount);
        Assert.Equal(0, (await ListAsync(Owner, "?kind=chat")).TotalCount);

        // The range bounds runs by when they were queued; the two finished runs were moved an hour back.
        var since = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddMinutes(-30).ToString("O"));
        Assert.Equal([waiting.Id], (await ListAsync(Owner, $"?from={since}")).Items.Select(run => run.Id));
        var recent = await StatsAsync(Owner, $"?from={since}");
        Assert.Equal((1, 1, 0m), (recent.Total, recent.Active, recent.TotalCostUsd));
        Assert.Null(recent.MedianDurationSeconds);
        Assert.Null(recent.AverageCostUsd);
        // The filter choices come from the whole range, not just what the other filters leave.
        Assert.Single((await StatsAsync(Owner, "?status=queued")).Runners);

        var none = await StatsAsync(Owner, "?item=WEB-999");
        Assert.Equal((0, 0, 0m), (none.Total, none.Finished, none.TotalCostUsd));
        Assert.Empty(none.Days);
        Assert.Null(none.P90DurationSeconds);

        Assert.Equal(HttpStatusCode.BadRequest, (await Owner.GetAsync($"{RunsBase}/stats?kind=deploy", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Owner.GetAsync($"{RunsBase}/stats?groupBy=item", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Owner.GetAsync($"{RunsBase}/stats?tz=Mars/Olympus", Ct)).StatusCode);
    }

    [Fact]
    public async Task stats_only_count_visible_runs_and_keep_failure_reasons_for_operators()
    {
        await FinishedRunAsync(ItemKey, "failed", 0.25m, 10, 1, null, "tests-failed", 10, 10);

        var secret = await CreateProjectAsync("Secret", "sec", ProjectVisibility.Private);
        var secretAgent = await CreateAgentAsync("sec-builder", [secret.Id]);
        var starter = await Owner.PostAsync($"/api/v1/orgs/{Slug}/projects/sec/playbooks/starter", null, Ct);
        Assert.True(starter.StatusCode == HttpStatusCode.Created, await starter.Content.ReadAsStringAsync(Ct));
        var secretPlaybook = (await starter.Content.ReadFromJsonAsync<PlaybookView>(ApiTestContext.Json, Ct))!.Id;
        await DispatchAsync((await CreateItemAsync("Hidden work", secret.Key)).Key, secretPlaybook, secretAgent.UserId);

        Assert.Equal(2, (await StatsAsync(Owner)).Total);

        var operatorAuth = await Context.RegisterAsync($"operator-{Guid.NewGuid():N}@test.local");
        using var operatorClient = Context.ClientFor(operatorAuth);
        await AddMemberAsync(operatorAuth.User.Id, OrgRole.Member, canOperateFactory: true);
        var seen = await StatsAsync(operatorClient);
        Assert.Equal(1, seen.Total);
        Assert.Equal(0.25m, seen.TotalCostUsd);
        Assert.DoesNotContain(seen.Playbooks, playbook => playbook.Id == secretPlaybook);
        Assert.Equal(0, (await StatsAsync(operatorClient, "?project=sec")).Total);
        Assert.Single(seen.FailureReasons!);

        var stakeholderAuth = await Context.RegisterAsync($"stakeholder-{Guid.NewGuid():N}@test.local");
        using var stakeholder = Context.ClientFor(stakeholderAuth);
        await AddMemberAsync(stakeholderAuth.User.Id, OrgRole.Member, canOperateFactory: false);
        var plain = await StatsAsync(stakeholder);
        Assert.Equal(1, plain.Total);
        Assert.Null(plain.FailureReasons);
        // The reason is operator detail: filtering by it must not reveal which runs carry it.
        Assert.Equal(0, (await StatsAsync(stakeholder, "?failure=tests-failed")).Total);
        Assert.Equal(0, (await ListAsync(stakeholder, "?failure=tests-failed")).TotalCount);
    }
}

public sealed record RunStats(
    int Total, int Active, int Finished, int Succeeded,
    decimal TotalCostUsd, decimal? AverageCostUsd, long InputTokens, long OutputTokens,
    double? MedianDurationSeconds, double? P90DurationSeconds, double? MedianQueueWaitSeconds,
    int PullRequests, IReadOnlyList<RunStatsDayRow> Days, string GroupBy, IReadOnlyList<RunStatsGroupRow> Groups,
    IReadOnlyList<RunStatsFailureRow>? FailureReasons,
    IReadOnlyList<RunStatsOptionRow> Playbooks, IReadOnlyList<RunStatsOptionRow> Runners);

public sealed record RunStatsDayRow(DateOnly Day, string Status, int Runs, decimal CostUsd);

public sealed record RunStatsGroupRow(
    string? Key, string? Name, int Runs, int Finished, int Succeeded,
    decimal CostUsd, decimal? AverageCostUsd, double? MedianDurationSeconds);

public sealed record RunStatsFailureRow(string? Reason, int Runs);

public sealed record RunStatsOptionRow(Guid Id, string Name);

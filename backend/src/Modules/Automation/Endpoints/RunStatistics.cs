using System.Linq.Expressions;
using Aictiq.Modules.Automation.Domain;
using Aictiq.SharedKernel.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Aictiq.Modules.Automation.Endpoints;

/// <summary>What the breakdown table groups the runs by.</summary>
public enum RunStatsGrouping
{
    Agent,
    Project,
    Playbook,
    Runner,
}

/// <summary>
/// How the factory performed over the runs a <see cref="RunFilter"/> picks out. Cost and
/// duration figures only count finished runs - a live run has neither yet - while the run
/// totals count every run.
/// </summary>
/// <param name="Active">Runs still queued, assigned or running.</param>
/// <param name="Finished">Succeeded, failed, cancelled and timed-out runs: what the success rate divides by.</param>
/// <param name="AverageCostUsd">Total cost of the finished runs that reported one, over how many did. Null when none did.</param>
/// <param name="MedianDurationSeconds">Started to finished, over finished runs that started.</param>
/// <param name="MedianQueueWaitSeconds">
/// From when a run could start - queued, or its scheduled time - to when a runner started it.
/// </param>
/// <param name="PullRequests">Runs that opened a pull request.</param>
/// <param name="Days">Runs and cost per day and status, the days in <c>tz</c>; only days with runs.</param>
/// <param name="Groups">The breakdown table, grouped by <paramref name="GroupBy"/>, most runs first.</param>
/// <param name="FailureReasons">
/// Failed and timed-out runs by failure reason, most frequent first. Null for anyone but a
/// factory operator: the failure reason is operator detail, as on the run itself.
/// </param>
/// <param name="Playbooks">Every playbook among the visible runs in the date range, for the filter.</param>
/// <param name="Runners">Every runner among the visible runs in the date range, for the filter.</param>
public sealed record RunStatsView(
    int Total, int Active, int Finished, int Succeeded,
    decimal TotalCostUsd, decimal? AverageCostUsd, long InputTokens, long OutputTokens,
    double? MedianDurationSeconds, double? P90DurationSeconds, double? MedianQueueWaitSeconds,
    int PullRequests, IReadOnlyList<RunStatsDay> Days,
    RunStatsGrouping GroupBy, IReadOnlyList<RunStatsGroup> Groups,
    IReadOnlyList<RunStatsFailure>? FailureReasons,
    IReadOnlyList<RunStatsOption> Playbooks, IReadOnlyList<RunStatsOption> Runners)
{
    public static RunStatsView Empty(RunStatsGrouping groupBy, bool includeFailures) =>
        new(0, 0, 0, 0, 0m, null, 0, 0, null, null, null, 0, [], groupBy, [], includeFailures ? [] : null, [], []);
}

public sealed record RunStatsDay(DateOnly Day, RunStatus Status, int Runs, decimal CostUsd);

/// <param name="Key">
/// What the matching runs filter takes: the agent's user id, the project key, or the playbook
/// or runner id. Null for runs no runner took.
/// </param>
public sealed record RunStatsGroup(
    string? Key, string? Name, int Runs, int Finished, int Succeeded,
    decimal CostUsd, decimal? AverageCostUsd, double? MedianDurationSeconds);

/// <param name="Reason">Null for runs that failed without saying why.</param>
public sealed record RunStatsFailure(string? Reason, int Runs);

public sealed record RunStatsOption(Guid Id, string Name);

/// <summary>
/// The statistics behind Factory → Runs. Counts and sums are grouped in SQL; the medians
/// and the p90 are not something EF can ask Postgres for, so only the start and finish
/// times of finished runs come back - two timestamps and a key a run - and are ranked here.
/// </summary>
internal static class RunStatistics
{
    private const int MaxFailureReasons = 10;

    public static async Task<RunStatsView> ComputeAsync(
        IQueryable<Run> runs, IQueryable<Run> scope, RunStatsGrouping groupBy, string timeZone,
        bool includeFailures, AutomationDbContext db, IUserDirectory directory, IProjectAccess access,
        CancellationToken ct)
    {
        var totals = await runs.GroupBy(_ => 1).Select(g => new
        {
            Total = g.Count(),
            Active = g.Count(run => run.Status < RunStatus.Succeeded),
            Finished = g.Count(run => run.Status >= RunStatus.Succeeded),
            Succeeded = g.Count(run => run.Status == RunStatus.Succeeded),
            Cost = g.Sum(run => run.CostUsd ?? 0m),
            Costed = g.Count(run => run.Status >= RunStatus.Succeeded && run.CostUsd != null),
            CostedSum = g.Sum(run => run.Status >= RunStatus.Succeeded ? run.CostUsd ?? 0m : 0m),
            Input = g.Sum(run => run.InputTokens ?? 0L),
            Output = g.Sum(run => run.OutputTokens ?? 0L),
            PullRequests = g.Count(run => run.PullRequestUrl != null),
        }).SingleOrDefaultAsync(ct);
        if (totals is null)
        {
            return RunStatsView.Empty(groupBy, includeFailures) with
            {
                Playbooks = await PlaybookOptionsAsync(scope, db, ct),
                Runners = await RunnerOptionsAsync(scope, db, ct),
            };
        }

        var days = (await runs
            .GroupBy(run => new
            {
                Day = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(run.QueuedAt.UtcDateTime, timeZone).Date,
                run.Status,
            })
            .Select(g => new { g.Key.Day, g.Key.Status, Runs = g.Count(), Cost = g.Sum(run => run.CostUsd ?? 0m) })
            .ToListAsync(ct))
            .OrderBy(row => row.Day).ThenBy(row => row.Status)
            .Select(row => new RunStatsDay(DateOnly.FromDateTime(row.Day), row.Status, row.Runs, row.Cost))
            .ToList();

        var timings = await runs
            .Where(run => run.Status >= RunStatus.Succeeded && run.StartedAt != null && run.FinishedAt != null)
            .Select(run => new Timing(
                run.AgentUserId, run.ProjectId, run.PlaybookId, run.RunnerId,
                run.ScheduledFor ?? run.QueuedAt, run.StartedAt!.Value, run.FinishedAt!.Value))
            .ToListAsync(ct);
        var durations = timings.Select(timing => (timing.FinishedAt - timing.StartedAt).TotalSeconds).ToList();
        var waits = timings.Select(timing => Math.Max(0, (timing.StartedAt - timing.ReadyAt).TotalSeconds)).ToList();

        var groups = await GroupsAsync(runs, timings, groupBy, db, directory, access, ct);

        List<RunStatsFailure>? failures = null;
        if (includeFailures)
        {
            failures = (await runs
                .Where(run => run.Status == RunStatus.Failed || run.Status == RunStatus.TimedOut)
                .GroupBy(run => run.FailureReason)
                .Select(g => new { Reason = g.Key, Runs = g.Count() })
                .OrderByDescending(row => row.Runs).ThenBy(row => row.Reason)
                .Take(MaxFailureReasons)
                .ToListAsync(ct))
                .Select(row => new RunStatsFailure(row.Reason, row.Runs))
                .ToList();
        }

        return new RunStatsView(
            totals.Total, totals.Active, totals.Finished, totals.Succeeded,
            totals.Cost, totals.Costed == 0 ? null : Math.Round(totals.CostedSum / totals.Costed, 4),
            totals.Input, totals.Output,
            Percentile(durations, 0.5), Percentile(durations, 0.9), Percentile(waits, 0.5),
            totals.PullRequests, days, groupBy, groups, failures,
            await PlaybookOptionsAsync(scope, db, ct), await RunnerOptionsAsync(scope, db, ct));
    }

    private sealed record Timing(
        string AgentUserId, Guid ProjectId, Guid PlaybookId, Guid? RunnerId,
        DateTimeOffset ReadyAt, DateTimeOffset StartedAt, DateTimeOffset FinishedAt);

    private static async Task<List<RunStatsGroup>> GroupsAsync(
        IQueryable<Run> runs, List<Timing> timings, RunStatsGrouping groupBy,
        AutomationDbContext db, IUserDirectory directory, IProjectAccess access, CancellationToken ct)
    {
        Expression<Func<Run, string?>> key = groupBy switch
        {
            RunStatsGrouping.Project => run => run.ProjectId.ToString(),
            RunStatsGrouping.Playbook => run => run.PlaybookId.ToString(),
            RunStatsGrouping.Runner => run => run.RunnerId == null ? null : run.RunnerId.Value.ToString(),
            _ => run => run.AgentUserId,
        };
        Func<Timing, string?> timingKey = groupBy switch
        {
            RunStatsGrouping.Project => timing => timing.ProjectId.ToString(),
            RunStatsGrouping.Playbook => timing => timing.PlaybookId.ToString(),
            RunStatsGrouping.Runner => timing => timing.RunnerId?.ToString(),
            _ => timing => timing.AgentUserId,
        };

        var rows = await runs.GroupBy(key).Select(g => new
        {
            g.Key,
            Runs = g.Count(),
            Finished = g.Count(run => run.Status >= RunStatus.Succeeded),
            Succeeded = g.Count(run => run.Status == RunStatus.Succeeded),
            Cost = g.Sum(run => run.CostUsd ?? 0m),
            Costed = g.Count(run => run.Status >= RunStatus.Succeeded && run.CostUsd != null),
            CostedSum = g.Sum(run => run.Status >= RunStatus.Succeeded ? run.CostUsd ?? 0m : 0m),
        }).ToListAsync(ct);
        var durations = timings.GroupBy(timingKey)
            .ToDictionary(g => g.Key ?? "", g => g.Select(timing => (timing.FinishedAt - timing.StartedAt).TotalSeconds).ToList());

        var names = new Dictionary<string, string>();
        var keys = new Dictionary<string, string>();
        var ids = rows.Where(row => row.Key is not null).Select(row => row.Key!).ToList();
        switch (groupBy)
        {
            case RunStatsGrouping.Agent:
                foreach (var (id, person) in await directory.GetAsync(ids, ct))
                {
                    names[id] = person.DisplayName;
                }
                break;
            case RunStatsGrouping.Project:
                foreach (var id in ids)
                {
                    // The drill-down filters by project key, so the key goes out in place of the id.
                    if (await access.GetProjectAsync(Guid.Parse(id), ct) is { } project)
                    {
                        names[id] = project.Name;
                        keys[id] = project.Key;
                    }
                }
                break;
            case RunStatsGrouping.Playbook:
                var playbookIds = ids.Select(Guid.Parse).ToList();
                foreach (var playbook in await db.Playbooks.AsNoTracking().Where(playbook => playbookIds.Contains(playbook.Id))
                    .Select(playbook => new { playbook.Id, playbook.Name }).ToListAsync(ct))
                {
                    names[playbook.Id.ToString()] = playbook.Name;
                }
                break;
            case RunStatsGrouping.Runner:
                var runnerIds = ids.Select(Guid.Parse).ToList();
                foreach (var runner in await db.Runners.AsNoTracking().Where(runner => runnerIds.Contains(runner.Id))
                    .Select(runner => new { runner.Id, runner.Name }).ToListAsync(ct))
                {
                    names[runner.Id.ToString()] = runner.Name;
                }
                break;
        }

        return rows
            .Select(row => new RunStatsGroup(
                row.Key is null ? null : keys.GetValueOrDefault(row.Key, row.Key),
                row.Key is null ? null : names.GetValueOrDefault(row.Key),
                row.Runs, row.Finished, row.Succeeded, row.Cost,
                row.Costed == 0 ? null : Math.Round(row.CostedSum / row.Costed, 4),
                Percentile(durations.GetValueOrDefault(row.Key ?? "") ?? [], 0.5)))
            .OrderByDescending(group => group.Runs).ThenBy(group => group.Name ?? group.Key)
            .ToList();
    }

    private static async Task<List<RunStatsOption>> PlaybookOptionsAsync(
        IQueryable<Run> scope, AutomationDbContext db, CancellationToken ct)
    {
        var ids = scope.Select(run => run.PlaybookId).Distinct();
        return (await db.Playbooks.AsNoTracking().Where(playbook => ids.Contains(playbook.Id))
                .Select(playbook => new { playbook.Id, playbook.Name }).ToListAsync(ct))
            .Select(row => new RunStatsOption(row.Id, row.Name))
            .OrderBy(option => option.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static async Task<List<RunStatsOption>> RunnerOptionsAsync(
        IQueryable<Run> scope, AutomationDbContext db, CancellationToken ct)
    {
        var ids = scope.Where(run => run.RunnerId != null).Select(run => run.RunnerId!.Value).Distinct();
        return (await db.Runners.AsNoTracking().Where(runner => ids.Contains(runner.Id))
                .Select(runner => new { runner.Id, runner.Name }).ToListAsync(ct))
            .Select(row => new RunStatsOption(row.Id, row.Name))
            .OrderBy(option => option.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>The linearly interpolated percentile, as Postgres's <c>percentile_cont</c> has it. Null for no values.</summary>
    internal static double? Percentile(IReadOnlyCollection<double> values, double fraction)
    {
        if (values.Count == 0)
        {
            return null;
        }
        var sorted = values.Order().ToArray();
        var position = fraction * (sorted.Length - 1);
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        return Math.Round(sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower), 1);
    }
}

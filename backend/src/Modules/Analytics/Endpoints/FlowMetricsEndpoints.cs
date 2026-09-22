using Aictiq.Modules.Analytics.Domain;
using Aictiq.Modules.WorkItems;
using Aictiq.Modules.WorkItems.Domain;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Aictiq.Modules.Analytics.Endpoints;

public sealed record CumulativeFlowDay(DateOnly Day, IReadOnlyDictionary<WorkflowStateCategory, int> Counts);

/// <param name="HistoryFrom">
/// The earliest day this organization's plan and this instance's retention let a report
/// start from. The response carries it so a clamped window is visible in the
/// data rather than only described in the pricing copy.
/// </param>
public sealed record CumulativeFlowView(IReadOnlyList<CumulativeFlowDay> Days, DateOnly HistoryFrom);
public sealed record CycleScatterPoint(string ItemKey, decimal LeadDays, decimal CycleDays, string? AssigneeId, WorkItemType Type);
public sealed record CycleMetricSummary(decimal P50, decimal P85, decimal P95);
public sealed record ThroughputPoint(DateOnly WeekOf, int Count, int AgentCount, int HumanCount);
/// <param name="HistoryFrom">See <see cref="CumulativeFlowView.HistoryFrom"/>.</param>
public sealed record CycleTimeView(CycleMetricSummary LeadTime, CycleMetricSummary CycleTime,
    IReadOnlyList<CycleScatterPoint> Items, IReadOnlyList<ThroughputPoint> Throughput, DateOnly HistoryFrom);

/// <summary>
/// Historical flow queries. State category is resolved from the workflow at query time
/// until the compact analytics projection is extended with category snapshots.
///
/// Both reports are bounded by <see cref="AnalyticsHistoryWindow"/>: a range that
/// reaches further back than the organization is entitled to see is moved forward, never
/// refused, and the window it was moved to travels back in the response. The bound is a
/// query bound - no snapshot, no item history and no audit row is deleted to enforce it.
/// </summary>
public static class FlowMetricsEndpoints
{
    public static IEndpointRouteBuilder MapFlowMetricsEndpoints(this IEndpointRouteBuilder api)
    {
        var projects = api.MapGroup("/orgs/{orgSlug}/projects/{projectKey}").WithTags("Analytics").RequireAuthorization();
        projects.MapGet("/flow", Flow).RequireProjectRole(ProjectRole.Guest).RequireScope(Scopes.Read);
        projects.MapGet("/cycle-time", CycleTime).RequireProjectRole(ProjectRole.Guest).RequireScope(Scopes.Read);
        return api;
    }

    private static async Task<IResult> Flow(HttpContext http, DateOnly? from, DateOnly? to, Guid? team, string? types,
        AnalyticsDbContext analytics, WorkItemsDbContext work, ICurrentTenant tenant, AnalyticsHistoryWindow history,
        CancellationToken ct)
    {
        if (!Range(from, to, out var start, out var end) || !TryTypes(types, out var typeFilter)) return BadRange();
        var earliest = await history.EarliestAsync(tenant.OrganizationId!.Value, ct);
        if (start < earliest) start = earliest;
        if (end < start) return Results.Ok(new CumulativeFlowView([], earliest));
        var projectId = http.ResolvedProjectId()!.Value;
        var states = await work.WorkflowStates.AsNoTracking().ToDictionaryAsync(state => state.Id, state => state.Category, ct);
        var snapshots = await analytics.ItemStateDaily.AsNoTracking().Where(row => row.ProjectId == projectId && row.Day >= start && row.Day <= end).ToListAsync(ct);
        var itemTypes = typeFilter is null && team is null ? null : await work.Items.AsNoTracking().Where(item => item.ProjectId == projectId)
            .Select(item => new { item.Id, item.TeamId, item.Type }).ToDictionaryAsync(item => item.Id, ct);
        var days = Enumerable.Range(0, end.DayNumber - start.DayNumber + 1).Select(offset => start.AddDays(offset)).Select(day =>
        {
            var visible = snapshots.Where(row => row.Day == day && (itemTypes is null || itemTypes.TryGetValue(row.ItemId, out var item)
                && (team is null || item.TeamId == team) && (typeFilter is null || typeFilter.Contains(item.Type))));
            var counts = visible.GroupBy(row => states.GetValueOrDefault(row.StateId, WorkflowStateCategory.Proposed))
                .ToDictionary(group => group.Key, group => group.Count());
            return new CumulativeFlowDay(day, counts);
        }).ToList();
        return Results.Ok(new CumulativeFlowView(days, earliest));
    }

    private static async Task<IResult> CycleTime(HttpContext http, DateOnly? from, DateOnly? to, Guid? team, string? types, string? groupBy,
        AnalyticsDbContext analytics, WorkItemsDbContext work, ICurrentTenant tenant, AnalyticsHistoryWindow history,
        CancellationToken ct)
    {
        if (!Range(from, to, out var start, out var end) || !TryTypes(types, out var typeFilter)
            || groupBy is not null && groupBy is not ("week" or "assignee" or "type")) return BadRange();
        var earliest = await history.EarliestAsync(tenant.OrganizationId!.Value, ct);
        if (start < earliest) start = earliest;
        if (end < start) return Results.Ok(new CycleTimeView(Summary([]), Summary([]), [], [], earliest));
        var projectId = http.ResolvedProjectId()!.Value;
        var states = await work.WorkflowStates.AsNoTracking().ToDictionaryAsync(state => state.Id, state => state.Category, ct);
        var items = await work.Items.AsNoTracking().Where(item => item.ProjectId == projectId && (team == null || item.TeamId == team)
            && (typeFilter == null || typeFilter.Contains(item.Type))).Select(item => new { item.Id, Key = item.ProjectKey + "-" + item.Number, item.CreatedAt, item.AssigneeId, item.Type }).ToListAsync(ct);
        var ids = items.Select(item => item.Id).ToArray();
        var transitions = ids.Length == 0 ? [] : await analytics.ItemTransitions.AsNoTracking().Where(row => ids.Contains(row.ItemId)
            && row.At >= start.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) && row.At < end.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)).OrderBy(row => row.At).ToListAsync(ct);
        var points = new List<(CycleScatterPoint Point, DateTimeOffset Completed)>();
        foreach (var item in items)
        {
            var events = transitions.Where(row => row.ItemId == item.Id).ToList();
            var completed = events.LastOrDefault(row => states.GetValueOrDefault(row.ToStateId) == WorkflowStateCategory.Completed);
            if (completed is null) continue;
            var active = events.LastOrDefault(row => row.At <= completed.At && states.GetValueOrDefault(row.ToStateId) == WorkflowStateCategory.Active);
            var lead = Math.Max(0, (decimal)(completed.At - item.CreatedAt).TotalDays);
            var cycle = Math.Max(0, (decimal)(completed.At - (active?.At ?? item.CreatedAt)).TotalDays);
            points.Add((new CycleScatterPoint(item.Key, Math.Round(lead, 2), Math.Round(cycle, 2), item.AssigneeId, item.Type), completed.At));
        }
        var throughput = points.GroupBy(row => Week(row.Completed)).OrderBy(group => group.Key).Select(group =>
            new ThroughputPoint(group.Key, group.Count(), 0, group.Count())).ToList();
        return Results.Ok(new CycleTimeView(Summary(points.Select(row => row.Point.LeadDays)), Summary(points.Select(row => row.Point.CycleDays)),
            points.Select(row => row.Point).OrderBy(row => row.ItemKey).ToList(), throughput, earliest));
    }

    internal static CycleMetricSummary Summary(IEnumerable<decimal> source)
    {
        var values = source.OrderBy(value => value).ToArray();
        return new CycleMetricSummary(Percentile(values, .50m), Percentile(values, .85m), Percentile(values, .95m));
    }
    private static decimal Percentile(decimal[] values, decimal p) => values.Length == 0 ? 0 : values[(int)Math.Ceiling(p * values.Length) - 1];
    private static DateOnly Week(DateTimeOffset at) { var day = DateOnly.FromDateTime(at.UtcDateTime); return day.AddDays(-((int)day.DayOfWeek + 6) % 7); }
    private static bool Range(DateOnly? from, DateOnly? to, out DateOnly start, out DateOnly end) { end = to ?? DateOnly.FromDateTime(DateTime.UtcNow); start = from ?? end.AddDays(-29); return start <= end && end.DayNumber - start.DayNumber <= 366; }
    private static bool TryTypes(string? raw, out HashSet<WorkItemType>? result) { result = null; if (string.IsNullOrWhiteSpace(raw)) return true; result = []; foreach (var value in raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)) if (Enum.TryParse<WorkItemType>(value, true, out var type)) result.Add(type); else return false; return result.Count > 0; }
    private static IResult BadRange() => Results.ValidationProblem(new Dictionary<string, string[]> { ["query"] = ["Use a valid date range (at most 366 days), type list, and optional groupBy."] });
}

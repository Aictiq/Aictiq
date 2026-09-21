using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Aictiq.Modules.Analytics.Domain;
using Aictiq.Modules.Tenancy;
using Aictiq.Modules.Tenancy.Domain;
using Aictiq.Modules.WorkItems;
using Aictiq.Modules.WorkItems.Domain;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Tenancy;
using AnalyticsScopeLog = Aictiq.Modules.Analytics.Domain.SprintScopeLog;

namespace Aictiq.Modules.Analytics.Endpoints;

public enum BurndownUnit { Points, Hours }

public sealed record ScopeChangeAnnotation(Guid ItemId, bool Added, decimal Value, DateTimeOffset At);
public sealed record BurndownDay(DateOnly Day, decimal Scope, decimal Remaining, decimal Completed,
    decimal IdealRemaining, IReadOnlyList<ScopeChangeAnnotation> ScopeChanges);
public sealed record SprintBurndownView(Guid SprintId, BurndownUnit Unit, IReadOnlyList<BurndownDay> Days);
public sealed record SprintVelocity(Guid SprintId, string SprintName, DateOnly StartsOn, DateOnly EndsOn,
    decimal CommittedPoints, decimal CompletedPoints);
public sealed record VelocityForecast(Guid SprintId, string SprintName, decimal ScopePoints, decimal Velocity,
    decimal DifferencePoints);
public sealed record TeamVelocityView(IReadOnlyList<SprintVelocity> Sprints, decimal AverageVelocity,
    decimal RollingAverageVelocity, VelocityForecast? Forecast);
public sealed record SprintHealthView(Guid SprintId, decimal PercentDone, int DaysElapsed, int WorkingDays,
    DateOnly? ProjectedCompletionOn, int BlockedCount, int UnestimatedCount, int UnassignedCount,
    decimal AgentSharePercent);

/// <summary>Read models backed by Analytics' append-only mirrors. Authorization and current
/// operational state stay at the HTTP boundary; Analytics owns the historical facts.</summary>
public static class SprintMetricsEndpoints
{
    private static readonly HybridCacheEntryOptions CacheForOneMinute = new() { Expiration = TimeSpan.FromSeconds(60) };

    public static IEndpointRouteBuilder MapSprintMetricsEndpoints(this IEndpointRouteBuilder api)
    {
        var teams = api.MapGroup("/orgs/{orgSlug}/teams").WithTags("Analytics").RequireAuthorization();
        teams.MapGet("/{teamId:guid}/velocity", Velocity).RequireOrgRole(OrgRole.Guest).RequireScope(Scopes.Read);
        var sprints = api.MapGroup("/orgs/{orgSlug}/sprints").WithTags("Analytics").RequireAuthorization();
        sprints.MapGet("/{sprintId:guid}/burndown", Burndown).RequireOrgRole(OrgRole.Guest).RequireScope(Scopes.Read);
        sprints.MapGet("/{sprintId:guid}/health", Health).RequireOrgRole(OrgRole.Guest).RequireScope(Scopes.Read);
        return api;
    }

    public static string SprintCacheTag(Guid sprintId) => $"analytics:sprint:{sprintId}";

    private static async Task<IResult> Burndown(Guid sprintId, string? unit, AnalyticsDbContext analytics,
        WorkItemsDbContext work, TenancyDbContext tenancy, IProjectAccess access, ICurrentUser user,
        ICurrentTenant tenant, HybridCache cache, CancellationToken ct)
    {
        if (!TryUnit(unit, out var selected))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["unit"] = ["Choose points or hours."] });
        var visible = await FindVisibleSprintAsync(sprintId, work, tenancy, access, user, ct);
        if (visible is null) return Results.NotFound();
        var (sprint, team) = visible.Value;
        var key = $"analytics:burndown:{tenant.OrganizationId}:{sprintId}:{selected}";
        var value = await cache.GetOrCreateAsync(key, (analytics, work, sprint, team, selected),
            static (state, token) => new ValueTask<SprintBurndownView>(BuildBurndownAsync(state.analytics, state.work,
                state.sprint, state.team, state.selected, token)), CacheForOneMinute, tags: [SprintCacheTag(sprintId)], cancellationToken: ct);
        return Results.Ok(value);
    }

    private static async Task<IResult> Velocity(Guid teamId, AnalyticsDbContext analytics, WorkItemsDbContext work,
        TenancyDbContext tenancy, IProjectAccess access, ICurrentUser user, int last = 6, CancellationToken ct = default)
    {
        var team = await tenancy.Teams.AsNoTracking().FirstOrDefaultAsync(x => x.Id == teamId, ct);
        if (team is null || await access.GetProjectRoleAsync(user.UserId!, team.ProjectId, ct) is null) return Results.NotFound();
        var sprints = await work.Sprints.AsNoTracking().Where(x => x.TeamId == teamId && x.State == SprintState.Completed)
            .OrderByDescending(x => x.CompletedAt).Take(Math.Clamp(last, 1, 24)).ToListAsync(ct);
        var sprintIds = sprints.Select(x => x.Id).ToArray();
        var scope = sprintIds.Length == 0 ? [] : await analytics.SprintScopeLog.AsNoTracking()
            .Where(x => sprintIds.Contains(x.SprintId)).ToListAsync(ct);
        var completedStateIds = await work.WorkflowStates.AsNoTracking().Where(x => x.Category == WorkflowStateCategory.Completed)
            .Select(x => x.Id).ToListAsync(ct);
        var completions = sprintIds.Length == 0 ? [] : await analytics.ItemTransitions.AsNoTracking()
            .Where(x => x.SprintId != null && sprintIds.Contains(x.SprintId.Value) && completedStateIds.Contains(x.ToStateId))
            .Select(x => new { SprintId = x.SprintId!.Value, x.ItemId, x.At }).ToListAsync(ct);

        var velocity = sprints.Select(sprint =>
        {
            var entries = scope.Where(x => x.SprintId == sprint.Id).OrderBy(x => x.At).ToList();
            var committed = entries.Where(x => sprint.StartedAt is not null && x.At <= sprint.StartedAt.Value)
                .Sum(x => x.Added ? x.Points ?? 0 : -(x.Points ?? 0));
            var completed = completions.Where(x => x.SprintId == sprint.Id).GroupBy(x => x.ItemId).Sum(group =>
                LatestScopePoints(entries, group.Key, group.Min(x => x.At)) ?? 0);
            return new SprintVelocity(sprint.Id, sprint.Name, sprint.StartsOn, sprint.EndsOn, committed, completed);
        }).ToList();
        var average = velocity.Count == 0 ? 0 : velocity.Average(x => x.CompletedPoints);
        var rolling = velocity.Take(3).ToList();
        var next = await work.Sprints.AsNoTracking().Where(x => x.TeamId == teamId && x.State != SprintState.Completed)
            .OrderBy(x => x.StartsOn).FirstOrDefaultAsync(ct);
        VelocityForecast? forecast = null;
        if (next is not null)
        {
            var scopePoints = await work.Items.AsNoTracking().Where(x => x.SprintId == next.Id).SumAsync(x => x.Points ?? 0, ct);
            forecast = new VelocityForecast(next.Id, next.Name, scopePoints, average, average - scopePoints);
        }
        return Results.Ok(new TeamVelocityView(velocity, average, rolling.Count == 0 ? 0 : rolling.Average(x => x.CompletedPoints), forecast));
    }

    private static async Task<IResult> Health(Guid sprintId, WorkItemsDbContext work, TenancyDbContext tenancy,
        IProjectAccess access, ICurrentUser user, TimeProvider clock, HybridCache cache, ICurrentTenant tenant,
        IUserDirectory directory, CancellationToken ct)
    {
        var visible = await FindVisibleSprintAsync(sprintId, work, tenancy, access, user, ct);
        if (visible is null) return Results.NotFound();
        var (sprint, team) = visible.Value;
        var key = $"analytics:health:{tenant.OrganizationId}:{sprintId}";
        var value = await cache.GetOrCreateAsync(key, (work, sprint, team, clock, directory),
            static (state, token) => new ValueTask<SprintHealthView>(BuildHealthAsync(state.work, state.sprint,
                state.team, state.clock, state.directory, token)), CacheForOneMinute,
            tags: [SprintCacheTag(sprintId)], cancellationToken: ct);
        return Results.Ok(value);
    }

    private static async Task<SprintBurndownView> BuildBurndownAsync(AnalyticsDbContext analytics, WorkItemsDbContext work,
        Sprint sprint, Team team, BurndownUnit unit, CancellationToken ct)
    {
        var scope = await analytics.SprintScopeLog.AsNoTracking().Where(x => x.SprintId == sprint.Id).OrderBy(x => x.At).ToListAsync(ct);
        var snapshots = await analytics.ItemStateDaily.AsNoTracking().Where(x => x.SprintId == sprint.Id
            && x.Day >= sprint.StartsOn && x.Day < sprint.EndsOn).ToListAsync(ct);
        var completedStates = await work.WorkflowStates.AsNoTracking().Where(x => x.Category == WorkflowStateCategory.Completed)
            .Select(x => x.Id).ToListAsync(ct);
        var workingDays = Enumerable.Range(0, sprint.EndsOn.DayNumber - sprint.StartsOn.DayNumber)
            .Count(offset => team.WorkingDays.Contains((int)sprint.StartsOn.AddDays(offset).DayOfWeek));
        var zone = ResolveTimeZone(team.TimeZone);
        var initialScope = scope.Where(entry => LocalDay(entry.At, zone) <= sprint.StartsOn)
            .Sum(entry => entry.Added ? Value(entry, unit) : -Value(entry, unit));
        var days = new List<BurndownDay>();
        for (var day = sprint.StartsOn; day < sprint.EndsOn; day = day.AddDays(1))
        {
            var dayScope = scope.Where(entry => LocalDay(entry.At, zone) <= day).ToList();
            var changes = scope.Where(entry => LocalDay(entry.At, zone) == day)
                .Select(entry => new ScopeChangeAnnotation(entry.ItemId, entry.Added, Value(entry, unit), entry.At)).ToList();
            var totalScope = dayScope.Sum(entry => entry.Added ? Value(entry, unit) : -Value(entry, unit));
            var state = snapshots.Where(snapshot => snapshot.Day == day).ToList();
            var remaining = unit == BurndownUnit.Hours
                ? state.Sum(snapshot => snapshot.RemainingHours ?? 0)
                : state.Where(snapshot => !completedStates.Contains(snapshot.StateId)).Sum(snapshot => snapshot.Points ?? 0);
            var elapsedWorkingDays = Enumerable.Range(0, day.DayNumber - sprint.StartsOn.DayNumber)
                .Count(offset => team.WorkingDays.Contains((int)sprint.StartsOn.AddDays(offset).DayOfWeek));
            var ideal = workingDays == 0 ? initialScope : Math.Max(0, initialScope * (workingDays - elapsedWorkingDays) / workingDays);
            days.Add(new BurndownDay(day, totalScope, remaining, Math.Max(0, totalScope - remaining), ideal, changes));
        }
        return new SprintBurndownView(sprint.Id, unit, days);
    }

    private static async Task<SprintHealthView> BuildHealthAsync(WorkItemsDbContext work, Sprint sprint, Team team,
        TimeProvider clock, IUserDirectory directory, CancellationToken ct)
    {
        var items = await work.Items.AsNoTracking().Where(x => x.SprintId == sprint.Id).Select(x => new
        {
            x.Id, x.StateId, x.Points, x.EstimateHours, x.AssigneeId
        }).ToListAsync(ct);
        var completedStates = await work.WorkflowStates.AsNoTracking().Where(x => x.Category == WorkflowStateCategory.Completed)
            .Select(x => x.Id).ToListAsync(ct);
        var completed = items.Count(item => completedStates.Contains(item.StateId));
        var done = items.Count == 0 ? 0 : Math.Round((decimal)completed / items.Count * 100, 1);
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var elapsed = Math.Clamp(today.DayNumber - sprint.StartsOn.DayNumber, 0, sprint.EndsOn.DayNumber - sprint.StartsOn.DayNumber);
        var workingDays = Enumerable.Range(0, sprint.EndsOn.DayNumber - sprint.StartsOn.DayNumber)
            .Count(offset => team.WorkingDays.Contains((int)sprint.StartsOn.AddDays(offset).DayOfWeek));
        DateOnly? projected = done == 0 || elapsed == 0 ? null : sprint.StartsOn.AddDays((int)Math.Ceiling(elapsed * 100 / done));
        var itemIds = items.Select(item => item.Id).ToArray();
        var blocked = itemIds.Length == 0 ? 0 : await work.ItemRelations.AsNoTracking()
            .CountAsync(relation => relation.Kind == ItemRelationKind.Blocks && itemIds.Contains(relation.TargetId), ct);
        var unestimated = team.EstimationUnit == EstimationUnit.Points
            ? items.Count(item => item.Points is null)
            : items.Count(item => item.EstimateHours is null);
        var assignees = items.Where(item => item.AssigneeId is not null).Select(item => item.AssigneeId!).Distinct().ToArray();
        var agents = await directory.FilterAgentsAsync(assignees, ct);
        var agentShare = items.Count == 0 ? 0 : Math.Round((decimal)items.Count(item => item.AssigneeId is not null && agents.Contains(item.AssigneeId)) / items.Count * 100, 1);
        return new SprintHealthView(sprint.Id, done, elapsed, workingDays, projected, blocked, unestimated,
            items.Count(item => item.AssigneeId is null), agentShare);
    }

    private static async Task<(Sprint Sprint, Team Team)?> FindVisibleSprintAsync(Guid sprintId, WorkItemsDbContext work,
        TenancyDbContext tenancy, IProjectAccess access, ICurrentUser user, CancellationToken ct)
    {
        var sprint = await work.Sprints.AsNoTracking().FirstOrDefaultAsync(x => x.Id == sprintId, ct);
        if (sprint is null) return null;
        var team = await tenancy.Teams.AsNoTracking().FirstOrDefaultAsync(x => x.Id == sprint.TeamId, ct);
        return team is null || await access.GetProjectRoleAsync(user.UserId!, team.ProjectId, ct) is null ? null : (sprint, team);
    }

    private static decimal Value(AnalyticsScopeLog row, BurndownUnit unit) => unit == BurndownUnit.Points ? row.Points ?? 0 : row.RemainingHours ?? 0;
    private static decimal? LatestScopePoints(IEnumerable<AnalyticsScopeLog> entries, Guid itemId, DateTimeOffset at) => entries
        .Where(entry => entry.ItemId == itemId && entry.Added && entry.At <= at).OrderByDescending(entry => entry.At)
        .Select(entry => entry.Points).FirstOrDefault();
    private static bool TryUnit(string? raw, out BurndownUnit unit)
    {
        unit = raw?.ToLowerInvariant() switch { null or "points" => BurndownUnit.Points, "hours" => BurndownUnit.Hours, _ => BurndownUnit.Points };
        return raw is null || string.Equals(raw, "points", StringComparison.OrdinalIgnoreCase) || string.Equals(raw, "hours", StringComparison.OrdinalIgnoreCase);
    }
    private static TimeZoneInfo ResolveTimeZone(string? value) => !string.IsNullOrWhiteSpace(value) && TimeZoneInfo.TryFindSystemTimeZoneById(value, out var zone) ? zone : TimeZoneInfo.Utc;
    private static DateOnly LocalDay(DateTimeOffset at, TimeZoneInfo zone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, zone).DateTime);
}

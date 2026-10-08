using System.Text.Json;
using Aictiq.Modules.Analytics.Domain;
using Aictiq.Modules.Tenancy;
using Aictiq.Modules.Tenancy.Domain;
using Aictiq.Modules.WorkItems;
using Aictiq.Modules.WorkItems.Domain;
using Aictiq.Modules.WorkItems.Endpoints;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Aictiq.Modules.Analytics.Endpoints;

public sealed record DashboardWidget(string Id, string Type, int X, int Y, int W, int H, JsonElement Config);
public sealed record DashboardView(Guid Id, string Name, string? OwnerUserId, bool IsDefault, IReadOnlyList<DashboardWidget> Layout, uint Version);
public sealed record DashboardRequest(string? Name, IReadOnlyList<DashboardWidget>? Layout, bool? Shared, uint? Version);
public sealed record DashboardWidgetData(string Id, string Type, object? Data, string? Error);
public sealed record DashboardDataView(Guid Id, IReadOnlyList<DashboardWidgetData> Widgets);
public sealed record DashboardSprint(Guid Id, string Name, string TeamName, DateOnly StartsOn, DateOnly EndsOn);

public static class DashboardEndpoints
{
    private static readonly HashSet<string> WidgetTypes = new(StringComparer.Ordinal)
        { "burndown", "velocity", "cfd", "cycle-time", "sprint-health", "items-by-state", "items-by-assignee", "recent-activity", "saved-view-count", "markdown" };

    public static IEndpointRouteBuilder MapDashboardEndpoints(this IEndpointRouteBuilder api)
    {
        var project = api.MapGroup("/orgs/{orgSlug}/projects/{projectKey}/dashboards").WithTags("Analytics").RequireAuthorization();
        project.MapGet("/", List).RequireProjectRole(ProjectRole.Guest).RequireScope(Scopes.Read);
        project.MapPost("/", Create).RequireProjectRole(ProjectRole.Member).RequireProjectWritable().RequireScope(Scopes.Write);
        project.MapPatch("/{dashboardId:guid}", Update).RequireProjectRole(ProjectRole.Member).RequireProjectWritable().RequireScope(Scopes.Write);
        project.MapDelete("/{dashboardId:guid}", Delete).RequireProjectRole(ProjectRole.Member).RequireProjectWritable().RequireScope(Scopes.Write);
        project.MapGet("/{dashboardId:guid}/data", Data).RequireProjectRole(ProjectRole.Guest).RequireScope(Scopes.Read);
        return api;
    }

    private static async Task<IResult> List(HttpContext http, AnalyticsDbContext db, ICurrentTenant tenant, ICurrentUser user, TimeProvider clock, CancellationToken ct)
    {
        var projectId = http.ResolvedProjectId()!.Value;
        await EnsureDefaultAsync(db, tenant.OrganizationId!.Value, projectId, clock, ct);
        var rows = await db.Dashboards.AsNoTracking().Where(row => row.ProjectId == projectId && (row.OwnerUserId == null || row.OwnerUserId == user.UserId))
            .OrderByDescending(row => row.IsDefault).ThenBy(row => row.Name).ToListAsync(ct);
        return Results.Ok(rows.Select(View));
    }

    private static async Task<IResult> Create(DashboardRequest request, HttpContext http, AnalyticsDbContext db, ICurrentTenant tenant, ICurrentUser user,
        Aictiq.SharedKernel.Contracts.IProjectAccess access, TimeProvider clock, CancellationToken ct)
    {
        var projectId = http.ResolvedProjectId()!.Value; var shared = request.Shared == true;
        if (shared && await access.GetProjectRoleAsync(user.UserId!, projectId, ct) != ProjectRole.Admin) return Results.Forbid();
        if (!Valid(request.Name, request.Layout, out var error)) return Invalid(error);
        var now = clock.GetUtcNow(); var row = new Dashboard { OrganizationId = tenant.OrganizationId!.Value, ProjectId = projectId,
            OwnerUserId = shared ? null : user.UserId, Name = request.Name!.Trim(), LayoutJson = JsonSerializer.Serialize(request.Layout), CreatedAt = now, UpdatedAt = now };
        db.Dashboards.Add(row); await db.SaveChangesAsync(ct); return Results.Created($"{http.Request.Path}/{row.Id}", View(row));
    }

    private static async Task<IResult> Update(Guid dashboardId, DashboardRequest request, HttpContext http, AnalyticsDbContext db, ICurrentUser user,
        Aictiq.SharedKernel.Contracts.IProjectAccess access, TimeProvider clock, CancellationToken ct)
    {
        var projectId = http.ResolvedProjectId()!.Value; var row = await db.Dashboards.FirstOrDefaultAsync(x => x.Id == dashboardId && x.ProjectId == projectId, ct);
        if (row is null) return Results.NotFound();
        if (!await CanEditAsync(row, projectId, user.UserId!, access, ct)) return Results.NotFound();
        if (request.Version is null || request.Version != row.Version) return Results.Problem(statusCode: StatusCodes.Status409Conflict, type: "https://aictiq.com/problems/conflict");
        if (request.Name is not null && (request.Name.Trim().Length is < 1 or > 100)) return Invalid("Name must be 1–100 characters.");
        if (request.Layout is not null && !Valid("x", request.Layout, out var error)) return Invalid(error);
        row.Name = request.Name?.Trim() ?? row.Name; row.LayoutJson = request.Layout is null ? row.LayoutJson : JsonSerializer.Serialize(request.Layout); row.UpdatedAt = clock.GetUtcNow();
        db.Entry(row).Property(x => x.Version).OriginalValue = request.Version.Value;
        try { await db.SaveChangesAsync(ct); } catch (DbUpdateConcurrencyException) { return Results.Problem(statusCode: StatusCodes.Status409Conflict, type: "https://aictiq.com/problems/conflict"); }
        return Results.Ok(View(row));
    }

    private static async Task<IResult> Delete(Guid dashboardId, HttpContext http, AnalyticsDbContext db, ICurrentUser user,
        Aictiq.SharedKernel.Contracts.IProjectAccess access, CancellationToken ct)
    {
        var projectId = http.ResolvedProjectId()!.Value; var row = await db.Dashboards.FirstOrDefaultAsync(x => x.Id == dashboardId && x.ProjectId == projectId, ct);
        if (row is null || !await CanEditAsync(row, projectId, user.UserId!, access, ct)) return Results.NotFound();
        if (row.IsDefault) return Results.Problem("The default dashboard cannot be deleted.", statusCode: StatusCodes.Status409Conflict);
        db.Dashboards.Remove(row); await db.SaveChangesAsync(ct); return Results.NoContent();
    }

    private static async Task<IResult> Data(Guid dashboardId, HttpContext http, AnalyticsDbContext analytics, WorkItemsDbContext work, TenancyDbContext tenancy,
        ICurrentUser user, ICurrentTenant tenant, IUserDirectory directory, Aictiq.SharedKernel.Contracts.IProjectAccess access, AnalyticsHistoryWindow history,
        TimeProvider clock, CancellationToken ct)
    {
        var projectId = http.ResolvedProjectId()!.Value; var row = await analytics.Dashboards.AsNoTracking().FirstOrDefaultAsync(x => x.Id == dashboardId && x.ProjectId == projectId && (x.OwnerUserId == null || x.OwnerUserId == user.UserId), ct);
        if (row is null) return Results.NotFound(); var layout = Parse(row.LayoutJson); var result = new List<DashboardWidgetData>();
        var context = new WidgetContext(projectId, tenant.OrganizationId!.Value, user.UserId!, analytics, work, tenancy, directory, access, history, clock);
        foreach (var widget in layout) try { result.Add(new DashboardWidgetData(widget.Id, widget.Type, await WidgetDataAsync(widget, context, ct), null)); }
        catch (Exception) { result.Add(new DashboardWidgetData(widget.Id, widget.Type, null, "Widget data is unavailable.")); }
        return Results.Ok(new DashboardDataView(row.Id, result));
    }

    /// <summary>What one data request shares across its widgets. The focus team and sprint are
    /// resolved once, on first use, so a dashboard without sprint widgets never looks them up.</summary>
    private sealed class WidgetContext(Guid projectId, Guid organizationId, string userId, AnalyticsDbContext analytics, WorkItemsDbContext work,
        TenancyDbContext tenancy, IUserDirectory directory, Aictiq.SharedKernel.Contracts.IProjectAccess access, AnalyticsHistoryWindow history, TimeProvider clock)
    {
        private (Team? Team, Sprint? Sprint)? focus;
        public Guid ProjectId => projectId; public Guid OrganizationId => organizationId; public AnalyticsDbContext Analytics => analytics;
        public Task<bool> CanOperateFactoryAsync(CancellationToken ct) => access.CanOperateFactoryAsync(userId, organizationId, ct);
        public WorkItemsDbContext Work => work; public IUserDirectory Directory => directory; public AnalyticsHistoryWindow History => history; public TimeProvider Clock => clock;

        /// <summary>The team whose active sprint the project is running (the first by name when
        /// several are), else the project's first team, which still has a velocity history.</summary>
        public async Task<(Team? Team, Sprint? Sprint)> FocusAsync(CancellationToken ct)
        {
            if (focus is not null) return focus.Value;
            var teams = await tenancy.Teams.AsNoTracking().Where(team => team.ProjectId == projectId).OrderBy(team => team.Name).ToListAsync(ct);
            var teamIds = teams.Select(team => team.Id).ToArray();
            var active = teamIds.Length == 0 ? [] : await work.Sprints.AsNoTracking().Where(sprint => teamIds.Contains(sprint.TeamId) && sprint.State == SprintState.Active).ToListAsync(ct);
            var team = teams.FirstOrDefault(team => active.Any(sprint => sprint.TeamId == team.Id)) ?? teams.FirstOrDefault();
            focus = (team, team is null ? null : active.Where(sprint => sprint.TeamId == team.Id).OrderBy(sprint => sprint.StartsOn).FirstOrDefault());
            return focus.Value;
        }
    }

    private static async Task<object> WidgetDataAsync(DashboardWidget widget, WidgetContext context, CancellationToken ct)
    {
        var (projectId, work) = (context.ProjectId, context.Work);
        switch (widget.Type)
        {
            case "items-by-state":
                return await (
                    from item in work.Items.AsNoTracking()
                    join state in work.WorkflowStates.AsNoTracking() on item.StateId equals state.Id
                    where item.ProjectId == projectId && item.RemovedAt == null
                    group item by new { state.Id, state.Name, state.Position } into grouped
                    orderby grouped.Key.Position
                    select new { stateId = grouped.Key.Id, stateName = grouped.Key.Name, count = grouped.Count() }
                ).ToListAsync(ct);
            case "items-by-assignee":
            {
                var rows = await work.Items.AsNoTracking().Where(x => x.ProjectId == projectId && x.RemovedAt == null).GroupBy(x => x.AssigneeId)
                    .Select(x => new { AssigneeId = x.Key, Count = x.Count() }).ToListAsync(ct);
                var people = await context.Directory.GetAsync([.. rows.Where(x => x.AssigneeId is not null).Select(x => x.AssigneeId!)], ct);
                return rows.OrderByDescending(x => x.Count).ThenBy(x => x.AssigneeId is null).Select(x => new
                {
                    assigneeId = x.AssigneeId, displayName = x.AssigneeId is null ? null : people.GetValueOrDefault(x.AssigneeId)?.DisplayName,
                    isAgent = x.AssigneeId is not null && people.GetValueOrDefault(x.AssigneeId)?.IsAgent == true, count = x.Count,
                }).ToList();
            }
            case "recent-activity":
            {
                // The same rows the item's own history hides from someone who may not operate the factory.
                var hide = !await context.CanOperateFactoryAsync(ct);
                var rows = await work.ItemHistory.AsNoTracking().WithoutFactory(work, hide).Join(work.Items.AsNoTracking(), entry => entry.ItemId, item => item.Id,
                        (entry, item) => new { entry.ItemId, entry.ActorId, entry.At, entry.Field, item.ProjectId, item.Key, item.Title })
                    .Where(x => x.ProjectId == projectId).OrderByDescending(x => x.At).Take(20).ToListAsync(ct);
                var actors = await context.Directory.GetAsync([.. rows.Select(x => x.ActorId).Distinct()], ct);
                return rows.Select(x => new { itemId = x.ItemId, itemKey = x.Key, itemTitle = x.Title, at = x.At, field = x.Field, actor = actors.GetValueOrDefault(x.ActorId)?.DisplayName }).ToList();
            }
            case "saved-view-count":
                return new { count = await work.SavedViews.AsNoTracking().CountAsync(x => x.ProjectId == projectId, ct) };
            case "markdown":
                return new { markdown = widget.Config.TryGetProperty("markdown", out var markdown) ? markdown.GetString() ?? "" : "" };
            case "velocity":
            {
                var (team, _) = await context.FocusAsync(ct);
                return new { teamName = team?.Name, velocity = team is null ? null : await SprintMetricsEndpoints.BuildVelocityAsync(context.Analytics, work, team.Id, 6, ct) };
            }
            case "burndown":
            {
                var (team, sprint) = await context.FocusAsync(ct);
                if (team is null || sprint is null) return new { sprint = (DashboardSprint?)null };
                var unit = team.EstimationUnit == EstimationUnit.Points ? BurndownUnit.Points : BurndownUnit.Hours;
                var burndown = await SprintMetricsEndpoints.BuildBurndownAsync(context.Analytics, work, sprint, team, unit, context.Clock, ct);
                return new { sprint = Describe(sprint, team), burndown.Unit, burndown.Days };
            }
            case "sprint-health":
            {
                var (team, sprint) = await context.FocusAsync(ct);
                if (team is null || sprint is null) return new { sprint = (DashboardSprint?)null };
                return new { sprint = Describe(sprint, team), health = await SprintMetricsEndpoints.BuildHealthAsync(work, sprint, team, context.Clock, context.Directory, ct) };
            }
            case "cfd":
            {
                var (start, end) = await WindowAsync(context, ct);
                return await FlowMetricsEndpoints.BuildFlowAsync(context.Analytics, work, projectId, start, end, null, null, start, ct);
            }
            case "cycle-time":
            {
                var (start, end) = await WindowAsync(context, ct);
                var cycle = await FlowMetricsEndpoints.BuildCycleTimeAsync(context.Analytics, work, projectId, start, end, null, null, start, ct);
                return new { cycle.LeadTime, cycle.CycleTime, completedCount = cycle.Items.Count, cycle.Throughput, from = start, to = end };
            }
            default:
                return new { available = true };
        }
    }

    /// <summary>The last thirty days, as on the Cycle insights page, moved forward to the history the plan allows.</summary>
    private static async Task<(DateOnly Start, DateOnly End)> WindowAsync(WidgetContext context, CancellationToken ct)
    {
        var end = DateOnly.FromDateTime(context.Clock.GetUtcNow().UtcDateTime); var start = end.AddDays(-29);
        var earliest = await context.History.EarliestAsync(context.OrganizationId, ct);
        return (start < earliest ? earliest : start, end);
    }

    private static DashboardSprint Describe(Sprint sprint, Team team) => new(sprint.Id, sprint.Name, team.Name, sprint.StartsOn, sprint.EndsOn);

    private static async Task EnsureDefaultAsync(AnalyticsDbContext db, Guid organizationId, Guid projectId, TimeProvider clock, CancellationToken ct)
    {
        if (await db.Dashboards.AnyAsync(row => row.ProjectId == projectId && row.IsDefault, ct)) return;
        var now = clock.GetUtcNow(); db.Dashboards.Add(new Dashboard { OrganizationId = organizationId, ProjectId = projectId, Name = "Default dashboard", IsDefault = true, LayoutJson = JsonSerializer.Serialize(WidgetTypes.Order().Select((type, index) => new DashboardWidget(Guid.NewGuid().ToString(), type, (index % 3) * 4, (index / 3) * 3, 4, 3, EmptyConfig))), CreatedAt = now, UpdatedAt = now });
        try { await db.SaveChangesAsync(ct); } catch (DbUpdateException) { db.ChangeTracker.Clear(); }
    }
    private static readonly JsonElement EmptyConfig = JsonDocument.Parse("{}").RootElement.Clone();
    private static bool Valid(string? name, IReadOnlyList<DashboardWidget>? layout, out string error) { error = ""; if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 100) { error = "Name must be 1–100 characters."; return false; } if (layout is null || layout.Count is < 1 or > 25 || layout.Select(x => x.Id).Distinct().Count() != layout.Count) { error = "Layout must contain 1–25 widgets with unique ids."; return false; } foreach (var widget in layout) if (string.IsNullOrWhiteSpace(widget.Id) || !WidgetTypes.Contains(widget.Type) || widget.X < 0 || widget.Y < 0 || widget.W is < 1 or > 12 || widget.H is < 1 or > 24 || widget.X + widget.W > 12 || widget.Config.ValueKind != JsonValueKind.Object) { error = "Layout contains an invalid widget or grid position."; return false; } return true; }
    private static IReadOnlyList<DashboardWidget> Parse(string json) => JsonSerializer.Deserialize<List<DashboardWidget>>(json) ?? [];
    private static DashboardView View(Dashboard row) => new(row.Id, row.Name, row.OwnerUserId, row.IsDefault, Parse(row.LayoutJson), row.Version);
    private static async Task<bool> CanEditAsync(Dashboard row, Guid projectId, string userId, Aictiq.SharedKernel.Contracts.IProjectAccess access, CancellationToken ct) =>
        row.OwnerUserId == userId || await access.GetProjectRoleAsync(userId, projectId, ct) == ProjectRole.Admin;
    private static IResult Invalid(string detail) => Results.ValidationProblem(new Dictionary<string, string[]> { ["dashboard"] = [detail] });
}

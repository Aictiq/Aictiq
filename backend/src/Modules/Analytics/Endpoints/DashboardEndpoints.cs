using System.Text.Json;
using Aictiq.Modules.Analytics.Domain;
using Aictiq.Modules.WorkItems;
using Aictiq.Modules.WorkItems.Domain;
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

    private static async Task<IResult> Data(Guid dashboardId, HttpContext http, AnalyticsDbContext analytics, WorkItemsDbContext work, ICurrentUser user, CancellationToken ct)
    {
        var projectId = http.ResolvedProjectId()!.Value; var row = await analytics.Dashboards.AsNoTracking().FirstOrDefaultAsync(x => x.Id == dashboardId && x.ProjectId == projectId && (x.OwnerUserId == null || x.OwnerUserId == user.UserId), ct);
        if (row is null) return Results.NotFound(); var layout = Parse(row.LayoutJson); var result = new List<DashboardWidgetData>();
        foreach (var widget in layout) try { result.Add(new DashboardWidgetData(widget.Id, widget.Type, await WidgetDataAsync(widget, projectId, work, ct), null)); }
        catch (Exception) { result.Add(new DashboardWidgetData(widget.Id, widget.Type, null, "Widget data is unavailable.")); }
        return Results.Ok(new DashboardDataView(row.Id, result));
    }

    private static async Task<object> WidgetDataAsync(DashboardWidget widget, Guid projectId, WorkItemsDbContext work, CancellationToken ct) => widget.Type switch
    {
        "items-by-state" => await (
            from item in work.Items.AsNoTracking()
            join state in work.WorkflowStates.AsNoTracking() on item.StateId equals state.Id
            where item.ProjectId == projectId
            group item by new { state.Id, state.Name, state.Position } into grouped
            orderby grouped.Key.Position
            select new { stateId = grouped.Key.Id, stateName = grouped.Key.Name, count = grouped.Count() }
        ).ToListAsync(ct),
        "items-by-assignee" => await work.Items.AsNoTracking().Where(x => x.ProjectId == projectId).GroupBy(x => x.AssigneeId).Select(x => new { assigneeId = x.Key, count = x.Count() }).ToListAsync(ct),
        "recent-activity" => await work.ItemHistory.AsNoTracking().Where(x => work.Items.Any(item => item.Id == x.ItemId && item.ProjectId == projectId)).OrderByDescending(x => x.At).Take(20).Select(x => new { x.ItemId, x.At, x.Field }).ToListAsync(ct),
        "saved-view-count" => new { count = await work.SavedViews.AsNoTracking().CountAsync(x => x.ProjectId == projectId, ct) },
        "markdown" => new { markdown = widget.Config.TryGetProperty("markdown", out var markdown) ? markdown.GetString() ?? "" : "" },
        _ => new { available = true },
    };

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

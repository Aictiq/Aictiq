using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Aictiq.Modules.WorkItems.Domain;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Tenancy;

namespace Aictiq.Modules.WorkItems.Endpoints;

public sealed record SavedViewView(Guid Id, string OwnerId, string Name, string Filter, string Sort,
    IReadOnlyList<string> Columns, bool IsShared, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, uint Version);
public sealed record CreateSavedViewRequest(string? Name, string? Filter, string? Sort,
    IReadOnlyList<string>? Columns, bool IsShared);
public sealed record UpdateSavedViewRequest(string? Name, string? Filter, string? Sort,
    IReadOnlyList<string>? Columns, bool? IsShared, uint Version);

/// <summary>
/// Persisted item-list presets. A caller sees their own private presets plus every shared
/// one in the project. Writes deliberately remain owner-only: sharing makes a preset
/// discoverable, not editable by every project member.
/// </summary>
public static class SavedViewEndpoints
{
    public static IEndpointRouteBuilder MapSavedViewEndpoints(this IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/orgs/{orgSlug}/projects/{projectKey}/views")
            .WithTags("Saved views").RequireAuthorization();
        group.MapGet("/", List).RequireProjectRole(ProjectRole.Guest).RequireScope(Scopes.Read);
        group.MapPost("/", Create).RequireProjectRole(ProjectRole.Member).RequireProjectWritable().RequireScope(Scopes.Write);
        group.MapPatch("/{viewId:guid}", Update).RequireProjectRole(ProjectRole.Member).RequireProjectWritable().RequireScope(Scopes.Write);
        group.MapDelete("/{viewId:guid}", Delete).RequireProjectRole(ProjectRole.Member).RequireProjectWritable().RequireScope(Scopes.Write);
        return api;
    }

    private static async Task<IResult> List(HttpContext http, WorkItemsDbContext db, ICurrentUser user,
        CancellationToken ct)
    {
        var projectId = http.ResolvedProjectId()!.Value;
        var views = await db.SavedViews.AsNoTracking()
            .Where(x => x.ProjectId == projectId && (x.IsShared || x.OwnerId == user.UserId))
            .OrderByDescending(x => x.IsShared).ThenBy(x => x.Name)
            .ToListAsync(ct);
        return Results.Ok(views.Select(ToView).ToList());
    }

    private static async Task<IResult> Create(CreateSavedViewRequest request, HttpContext http,
        WorkItemsDbContext db, ICurrentUser user, ICurrentTenant tenant, TimeProvider clock, CancellationToken ct)
    {
        var errors = Validate(request.Name, request.Filter, request.Sort, request.Columns);
        if (errors.Count > 0) return Results.ValidationProblem(errors, type: ProblemTypes.Validation);

        var now = clock.GetUtcNow();
        var view = new SavedView
        {
            OrganizationId = tenant.OrganizationId!.Value,
            ProjectId = http.ResolvedProjectId()!.Value,
            OwnerId = user.UserId!,
            Name = request.Name!.Trim(),
            Filter = request.Filter?.Trim() ?? "",
            Sort = request.Sort?.Trim() ?? "",
            Columns = NormalizeColumns(request.Columns),
            IsShared = request.IsShared,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.SavedViews.Add(view);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            return Results.Conflict();
        }
        return Results.Created($"/api/v1/orgs/{http.Request.RouteValues["orgSlug"]}/projects/{http.Request.RouteValues["projectKey"]}/views/{view.Id}", ToView(view));
    }

    private static async Task<IResult> Update(Guid viewId, UpdateSavedViewRequest request, HttpContext http,
        WorkItemsDbContext db, ICurrentUser user, TimeProvider clock, CancellationToken ct)
    {
        var projectId = http.ResolvedProjectId()!.Value;
        var view = await db.SavedViews.FirstOrDefaultAsync(x => x.Id == viewId && x.ProjectId == projectId && x.OwnerId == user.UserId, ct);
        // Owner-only writes intentionally answer 404, so a member cannot use the endpoint
        // to learn which shared presets were created by somebody else.
        if (view is null) return Results.NotFound();

        var errors = Validate(request.Name ?? view.Name, request.Filter, request.Sort, request.Columns);
        if (errors.Count > 0) return Results.ValidationProblem(errors, type: ProblemTypes.Validation);

        db.Entry(view).Property(x => x.Version).OriginalValue = request.Version;
        if (request.Name is not null) view.Name = request.Name.Trim();
        if (request.Filter is not null) view.Filter = request.Filter.Trim();
        if (request.Sort is not null) view.Sort = request.Sort.Trim();
        if (request.Columns is not null) view.Columns = NormalizeColumns(request.Columns);
        if (request.IsShared is not null) view.IsShared = request.IsShared.Value;
        view.UpdatedAt = clock.GetUtcNow();
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Results.Problem("The saved view was modified by someone else.", statusCode: StatusCodes.Status409Conflict,
                type: ProblemTypes.Conflict);
        }
        catch (DbUpdateException)
        {
            return Results.Conflict();
        }
        return Results.Ok(ToView(view));
    }

    private static async Task<IResult> Delete(Guid viewId, HttpContext http, WorkItemsDbContext db,
        ICurrentUser user, CancellationToken ct)
    {
        var view = await db.SavedViews.FirstOrDefaultAsync(x => x.Id == viewId && x.ProjectId == http.ResolvedProjectId() && x.OwnerId == user.UserId, ct);
        if (view is null) return Results.NotFound();
        db.SavedViews.Remove(view);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static Dictionary<string, string[]> Validate(string? name, string? filter, string? sort,
        IReadOnlyList<string>? columns)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 100)
            errors["name"] = ["A name of 1–100 characters is required."];
        if (filter is { Length: > 2048 }) errors["filter"] = ["A filter can be at most 2048 characters."];
        if (sort is { Length: > 64 }) errors["sort"] = ["A sort can be at most 64 characters."];
        if (columns is { Count: > 20 } || columns?.Any(x => string.IsNullOrWhiteSpace(x) || x.Trim().Length > 64) == true)
            errors["columns"] = ["Choose at most 20 non-empty columns of at most 64 characters."];
        return errors;
    }

    private static string[] NormalizeColumns(IReadOnlyList<string>? columns) =>
        columns?.Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? [];

    private static SavedViewView ToView(SavedView view) => new(view.Id, view.OwnerId, view.Name, view.Filter,
        view.Sort, view.Columns, view.IsShared, view.CreatedAt, view.UpdatedAt, view.Version);
}

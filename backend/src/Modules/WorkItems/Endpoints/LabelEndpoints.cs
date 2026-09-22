using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Aictiq.Modules.WorkItems.Domain;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Tenancy;

namespace Aictiq.Modules.WorkItems.Endpoints;

public sealed record LabelView(Guid Id, string Name, string? Color, string? Description, string? Group, int ItemCount, uint Version);
public sealed record ItemLabelView(Guid Id, string Name, string? Color, string? Group);
public sealed record CreateLabelRequest(string? Name, string? Color, string? Description, string? Group);
public sealed record UpdateLabelRequest(string? Name, string? Color, string? Description, string? Group, uint Version);

public static class LabelEndpoints
{
    public static IEndpointRouteBuilder MapLabelEndpoints(this IEndpointRouteBuilder api)
    {
        var project = api.MapGroup("/orgs/{orgSlug}/projects/{projectKey}/labels").WithTags("Labels").RequireAuthorization();
        project.MapGet("/", List).RequireProjectRole(ProjectRole.Guest).RequireScope(Scopes.Read);
        project.MapPost("/", Create).RequireProjectRole(ProjectRole.Member).RequireProjectWritable().RequireScope(Scopes.Write);
        project.MapPatch("/{labelId:guid}", Update).RequireProjectRole(ProjectRole.Member).RequireProjectWritable().RequireScope(Scopes.Write);
        project.MapPost("/{labelId:guid}/merge-into/{targetId:guid}", Merge).RequireProjectRole(ProjectRole.Admin).RequireProjectWritable().RequireScope(Scopes.Write);
        project.MapDelete("/{labelId:guid}", Delete).RequireProjectRole(ProjectRole.Admin).RequireProjectWritable().RequireScope(Scopes.Write);

        // No {projectKey} in these routes, so - like WorkItemEndpoints' item-scoped
        // routes - the project role is checked inside the handler via FindWritable
        // rather than by RequireProjectRole/RequireProjectWritable.
        var items = api.MapGroup("/orgs/{orgSlug}/items/{itemKey}/labels").WithTags("Labels").RequireAuthorization();
        items.MapPut("/{labelId:guid}", AddToItem).RequireOrgRole(OrgRole.Member).RequireScope(Scopes.Write);
        items.MapDelete("/{labelId:guid}", RemoveFromItem).RequireOrgRole(OrgRole.Member).RequireScope(Scopes.Write);
        return api;
    }

    private static async Task<IResult> List(HttpContext http, WorkItemsDbContext db, CancellationToken ct)
    {
        var projectId = http.ResolvedProjectId()!.Value;
        var labels = await db.Labels.AsNoTracking().Where(x => x.ProjectId == projectId).ToListAsync(ct);
        var labelIds = labels.Select(x => x.Id).ToArray();
        var counts = await db.ItemLabels.AsNoTracking().Where(x => labelIds.Contains(x.LabelId))
            .GroupBy(x => x.LabelId).Select(g => new { LabelId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.LabelId, x => x.Count, ct);
        var ordered = labels
            .OrderBy(x => x.Group is null ? 1 : 0).ThenBy(x => x.Group, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase);
        return Results.Ok(ordered.Select(x => ToView(x, counts.GetValueOrDefault(x.Id))).ToList());
    }

    private static async Task<IResult> Create(CreateLabelRequest request, HttpContext http, WorkItemsDbContext db, ICurrentTenant tenant, CancellationToken ct)
    {
        var project = http.ResolvedProject()!;
        var errors = Validate(request.Name, request.Color, request.Description, request.Group);
        if (errors.Count > 0) return Results.ValidationProblem(errors);
        var label = new Label
        {
            OrganizationId = tenant.OrganizationId!.Value,
            ProjectId = project.Id,
            Name = request.Name!.Trim(),
            Color = Blank(request.Color),
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
            Group = string.IsNullOrWhiteSpace(request.Group) ? null : request.Group.Trim(),
        };
        db.Labels.Add(label);
        // A duplicate case-insensitive name races past this point straight into the
        // unique index; GlobalExceptionHandler turns that PG violation into a 409 - the
        // index is the guarantee, this call is only for the happy path.
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/v1/orgs/{http.Request.RouteValues["orgSlug"]}/projects/{project.Key}/labels/{label.Id}", ToView(label, 0));
    }

    private static async Task<IResult> Update(Guid labelId, UpdateLabelRequest request, HttpContext http, WorkItemsDbContext db, CancellationToken ct)
    {
        var project = http.ResolvedProject()!;
        var label = await db.Labels.FirstOrDefaultAsync(x => x.Id == labelId && x.ProjectId == project.Id, ct);
        if (label is null) return Results.NotFound();
        if (label.Version != request.Version) return Conflict();
        var name = request.Name ?? label.Name;
        var errors = Validate(name, request.Color, request.Description, request.Group);
        if (errors.Count > 0) return Results.ValidationProblem(errors);
        label.Name = name.Trim();
        label.Color = Blank(request.Color);
        label.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        label.Group = string.IsNullOrWhiteSpace(request.Group) ? null : request.Group.Trim();
        await db.SaveChangesAsync(ct);
        var count = await db.ItemLabels.AsNoTracking().CountAsync(x => x.LabelId == label.Id, ct);
        return Results.Ok(ToView(label, count));
    }

    private static async Task<IResult> Delete(Guid labelId, HttpContext http, WorkItemsDbContext db, CancellationToken ct)
    {
        var project = http.ResolvedProject()!;
        var label = await db.Labels.FirstOrDefaultAsync(x => x.Id == labelId && x.ProjectId == project.Id, ct);
        if (label is null) return Results.NotFound();
        // Cascades work.item_labels at the database - deleting the row here is the whole
        // cleanup, not endpoint-side bookkeeping.
        db.Labels.Remove(label);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> Merge(Guid labelId, Guid targetId, HttpContext http, WorkItemsDbContext db, CancellationToken ct)
    {
        if (labelId == targetId) return Results.ValidationProblem(new Dictionary<string, string[]> { ["targetId"] = ["Choose a different label."] });
        var projectId = http.ResolvedProjectId()!.Value;
        var source = await db.Labels.FirstOrDefaultAsync(x => x.Id == labelId && x.ProjectId == projectId, ct);
        var target = await db.Labels.AnyAsync(x => x.Id == targetId && x.ProjectId == projectId, ct);
        if (source is null || !target) return Results.NotFound();
        var links = await db.ItemLabels.Where(x => x.LabelId == source.Id).ToListAsync(ct);
        var existing = await db.ItemLabels.Where(x => x.LabelId == targetId).Select(x => x.ItemId).ToHashSetAsync(ct);
        foreach (var link in links)
            if (existing.Contains(link.ItemId)) db.ItemLabels.Remove(link);
            else
            {
                db.ItemLabels.Remove(link);
                db.ItemLabels.Add(new ItemLabel { OrganizationId = link.OrganizationId, ItemId = link.ItemId, LabelId = targetId, AddedAt = link.AddedAt, AddedBy = link.AddedBy });
            }
        db.Labels.Remove(source);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> AddToItem(string itemKey, Guid labelId, HttpContext http, WorkItemsDbContext db, IProjectAccess access, ICurrentUser user, TimeProvider clock, CancellationToken ct)
    {
        var item = await WorkItemEndpoints.FindWritable(db, access, user, itemKey, ct);
        if (item is null) return Results.NotFound();
        if (await ArchivedResultAsync(access, item, ct) is { } archived) return archived;
        // A label id from another project is a 404, not a silent cross-project link.
        if (!await db.Labels.AsNoTracking().AnyAsync(x => x.Id == labelId && x.ProjectId == item.ProjectId, ct)) return Results.NotFound();
        if (!await db.ItemLabels.AnyAsync(x => x.ItemId == item.Id && x.LabelId == labelId, ct))
        {
            db.ItemLabels.Add(new ItemLabel { OrganizationId = item.OrganizationId, ItemId = item.Id, LabelId = labelId, AddedAt = clock.GetUtcNow(), AddedBy = user.UserId! });
            // Labels are a separate table; a concurrent title edit must not 409 because
            // someone added a label, so the item's own Version/UpdatedAt stay untouched.
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Another request won the idempotent race and already added this label;
                // detach this attempt (and its history row) and report success either way.
                foreach (var entry in db.ChangeTracker.Entries().Where(x => x.State == EntityState.Added).ToList()) entry.State = EntityState.Detached;
            }
        }
        return Results.NoContent();
    }

    private static async Task<IResult> RemoveFromItem(string itemKey, Guid labelId, HttpContext http, WorkItemsDbContext db, IProjectAccess access, ICurrentUser user, CancellationToken ct)
    {
        var item = await WorkItemEndpoints.FindWritable(db, access, user, itemKey, ct);
        if (item is null) return Results.NotFound();
        if (await ArchivedResultAsync(access, item, ct) is { } archived) return archived;
        if (!await db.Labels.AsNoTracking().AnyAsync(x => x.Id == labelId && x.ProjectId == item.ProjectId, ct)) return Results.NotFound();
        var link = await db.ItemLabels.FirstOrDefaultAsync(x => x.ItemId == item.Id && x.LabelId == labelId, ct);
        if (link is not null)
        {
            db.ItemLabels.Remove(link);
            await db.SaveChangesAsync(ct);
        }
        return Results.NoContent();
    }

    /// <summary>
    /// The item-scoped label routes carry no {projectKey}, so RequireProjectWritable never
    /// runs for them; this is the same 409 it would have given, resolved by hand from the
    /// item's own project key.
    /// </summary>
    private static async Task<IResult?> ArchivedResultAsync(IProjectAccess access, WorkItem item, CancellationToken ct)
    {
        var project = await access.FindProjectAsync(item.OrganizationId, item.ProjectKey, ct);
        return project is { IsArchived: true }
            ? Results.Problem(title: "This project is archived.", detail: $"{project.Key} is read-only until it is un-archived.", type: ProblemTypes.ProjectArchived, statusCode: StatusCodes.Status409Conflict)
            : null;
    }

    private static Dictionary<string, string[]> Validate(string? name, string? color, string? description, string? group)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 50) errors["name"] = ["A name of 1-50 characters is required."];
        if (Blank(color) is { } hex && !Regex.IsMatch(hex, "^#[0-9A-Fa-f]{6}$")) errors["color"] = ["Color must be a 6-digit hex value like #3B82F6."];
        if (description is not null && description.Trim().Length > 280) errors["description"] = ["Description must be 280 characters or fewer."];
        if (group is not null && group.Trim().Length > 50) errors["group"] = ["Group must be 50 characters or fewer."];
        return errors;
    }

    /// <summary>
    /// Blank is absent. A cleared field arrives from a form as "" rather than as null, and
    /// "" is not a malformed colour - it is no colour, exactly like an omitted one.
    /// </summary>
    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static LabelView ToView(Label label, int itemCount) => new(label.Id, label.Name, label.Color, label.Description, label.Group, itemCount, label.Version);
    private static IResult Conflict() => Results.Problem("The label was modified by someone else.", statusCode: StatusCodes.Status409Conflict, type: ProblemTypes.Conflict);
}

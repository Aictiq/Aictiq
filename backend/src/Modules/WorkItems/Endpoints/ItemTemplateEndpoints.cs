using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Aictiq.Modules.WorkItems.Domain;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Tenancy;

namespace Aictiq.Modules.WorkItems.Endpoints;

public sealed record ItemTemplateView(Guid Id, WorkItemType Type, string Name, string DescriptionMarkdown,
    IReadOnlyList<Guid> DefaultLabelIds, WorkItemPriority? DefaultPriority, bool IsDefault, uint Version);
public sealed record CreateItemTemplateRequest(WorkItemType Type, string? Name, string? DescriptionMarkdown,
    IReadOnlyList<Guid>? DefaultLabelIds, WorkItemPriority? DefaultPriority, bool IsDefault);
public sealed record UpdateItemTemplateRequest(string? Name, string? DescriptionMarkdown,
    IReadOnlyList<Guid>? DefaultLabelIds, WorkItemPriority? DefaultPriority, bool? IsDefault, uint Version);

/// <summary>Administrative project templates. Applying one is intentionally a client-side
/// prefill, so a template cannot silently overwrite fields a person has already entered.</summary>
public static class ItemTemplateEndpoints
{
    public static IEndpointRouteBuilder MapItemTemplateEndpoints(this IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/orgs/{orgSlug}/projects/{projectKey}/templates").WithTags("Item templates").RequireAuthorization();
        group.MapGet("/", List).RequireProjectRole(ProjectRole.Guest).RequireScope(Scopes.Read);
        group.MapPost("/", Create).RequireProjectRole(ProjectRole.Admin).RequireProjectWritable().RequireScope(Scopes.Write);
        group.MapPatch("/{templateId:guid}", Update).RequireProjectRole(ProjectRole.Admin).RequireProjectWritable().RequireScope(Scopes.Write);
        group.MapDelete("/{templateId:guid}", Delete).RequireProjectRole(ProjectRole.Admin).RequireProjectWritable().RequireScope(Scopes.Write);
        return api;
    }

    private static async Task<IResult> List(HttpContext http, WorkItemsDbContext db, ICurrentTenant tenant, CancellationToken ct)
    {
        var projectId = http.ResolvedProjectId()!.Value;
        // The outbox handler makes this durable after project creation; this closes the
        // short gap before its first sweep, just like the default workflow endpoint.
        await EnsureBuiltInsAsync(db, tenant.OrganizationId!.Value, projectId, ct);
        var templates = await db.ItemTemplates.AsNoTracking().Where(x => x.ProjectId == projectId)
            .OrderBy(x => x.Type).ThenByDescending(x => x.IsDefault).ThenBy(x => x.Name).ToListAsync(ct);
        return Results.Ok(templates.Select(ToView).ToList());
    }

    private static async Task<IResult> Create(CreateItemTemplateRequest request, HttpContext http,
        WorkItemsDbContext db, ICurrentTenant tenant, CancellationToken ct)
    {
        var projectId = http.ResolvedProjectId()!.Value;
        var errors = await ValidateAsync(db, projectId, request.Name, request.DefaultLabelIds, ct);
        if (errors.Count > 0) return Results.ValidationProblem(errors, type: ProblemTypes.Validation);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (request.IsDefault)
            await db.ItemTemplates.Where(x => x.ProjectId == projectId && x.Type == request.Type && x.IsDefault)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsDefault, false), ct);
        var template = new ItemTemplate
        {
            OrganizationId = tenant.OrganizationId!.Value, ProjectId = projectId, Type = request.Type,
            Name = request.Name!.Trim(), DescriptionMarkdown = request.DescriptionMarkdown ?? "",
            DefaultLabelIds = request.DefaultLabelIds?.Distinct().ToArray() ?? [], DefaultPriority = request.DefaultPriority,
            IsDefault = request.IsDefault,
        };
        db.ItemTemplates.Add(template);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Created($"/api/v1/orgs/{http.Request.RouteValues["orgSlug"]}/projects/{http.Request.RouteValues["projectKey"]}/templates/{template.Id}", ToView(template));
    }

    private static async Task<IResult> Update(Guid templateId, UpdateItemTemplateRequest request, HttpContext http,
        WorkItemsDbContext db, CancellationToken ct)
    {
        var projectId = http.ResolvedProjectId()!.Value;
        var template = await db.ItemTemplates.FirstOrDefaultAsync(x => x.Id == templateId && x.ProjectId == projectId, ct);
        if (template is null) return Results.NotFound();
        var errors = await ValidateAsync(db, projectId, request.Name ?? template.Name, request.DefaultLabelIds, ct);
        if (errors.Count > 0) return Results.ValidationProblem(errors, type: ProblemTypes.Validation);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        db.Entry(template).Property(x => x.Version).OriginalValue = request.Version;
        if (request.IsDefault == true && !template.IsDefault)
            await db.ItemTemplates.Where(x => x.ProjectId == projectId && x.Type == template.Type && x.IsDefault && x.Id != template.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsDefault, false), ct);
        template.Name = request.Name?.Trim() ?? template.Name;
        if (request.DescriptionMarkdown is not null) template.DescriptionMarkdown = request.DescriptionMarkdown;
        if (request.DefaultLabelIds is not null) template.DefaultLabelIds = request.DefaultLabelIds.Distinct().ToArray();
        // The settings form sends its complete current value, so null deliberately clears
        // a template's priority rather than making "no default" impossible to save.
        template.DefaultPriority = request.DefaultPriority;
        if (request.IsDefault is not null) template.IsDefault = request.IsDefault.Value;
        try
        {
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict();
        }
        return Results.Ok(ToView(template));
    }

    private static async Task<IResult> Delete(Guid templateId, HttpContext http, WorkItemsDbContext db, CancellationToken ct)
    {
        var template = await db.ItemTemplates.FirstOrDefaultAsync(x => x.Id == templateId && x.ProjectId == http.ResolvedProjectId(), ct);
        if (template is null) return Results.NotFound();
        db.ItemTemplates.Remove(template);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    internal static async Task EnsureBuiltInsAsync(WorkItemsDbContext db, Guid organizationId, Guid projectId, CancellationToken ct)
    {
        var present = await db.ItemTemplates.Where(x => x.ProjectId == projectId &&
                (x.Name == "Bug report" || x.Name == "User story"))
            .Select(x => x.Name).ToHashSetAsync(ct);
        if (present.Count == 2) return;
        if (!present.Contains("Bug report")) db.ItemTemplates.Add(new ItemTemplate
        {
            OrganizationId = organizationId, ProjectId = projectId, Type = WorkItemType.Bug, Name = "Bug report",
            DescriptionMarkdown = "## Steps to reproduce\n\n1. \n\n## Expected\n\n\n## Actual\n\n", IsDefault = true,
        });
        if (!present.Contains("User story")) db.ItemTemplates.Add(new ItemTemplate
        {
            OrganizationId = organizationId, ProjectId = projectId, Type = WorkItemType.Story, Name = "User story",
            DescriptionMarkdown = "## As a … I want … so that …\n\n\n## Acceptance criteria\n\n- [ ] ", IsDefault = true,
        });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { db.ChangeTracker.Clear(); }
    }

    private static async Task<Dictionary<string, string[]>> ValidateAsync(WorkItemsDbContext db, Guid projectId,
        string? name, IReadOnlyList<Guid>? labelIds, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 100) errors["name"] = ["A name of 1–100 characters is required."];
        if (labelIds is { Count: > 0 })
        {
            var ids = labelIds.Distinct().ToArray();
            if (await db.Labels.CountAsync(x => x.ProjectId == projectId && ids.Contains(x.Id), ct) != ids.Length)
                errors["defaultLabelIds"] = ["One or more labels do not belong to this project."];
        }
        return errors;
    }

    private static ItemTemplateView ToView(ItemTemplate template) => new(template.Id, template.Type, template.Name,
        template.DescriptionMarkdown, template.DefaultLabelIds, template.DefaultPriority, template.IsDefault, template.Version);
    private static IResult Conflict() => Results.Problem("The template was modified by someone else.", statusCode: StatusCodes.Status409Conflict, type: ProblemTypes.Conflict);
}

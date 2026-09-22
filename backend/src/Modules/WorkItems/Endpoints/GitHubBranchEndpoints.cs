using Aictiq.Modules.Integrations;
using Aictiq.Modules.Integrations.Domain;
using Aictiq.Modules.WorkItems.Domain;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Aictiq.Modules.WorkItems.Endpoints;

public sealed record CreateGitHubBranchRequest(long RepoId, string? From = "default");

/// <summary>WorkItems owns the durable item-link write; Integrations only supplies the
/// installation credential and repository operation, avoiding a module dependency cycle.</summary>
public static partial class GitHubBranchEndpoints
{
    public static IEndpointRouteBuilder MapGitHubBranchEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapGroup("/orgs/{orgSlug}/items").WithTags("GitHub").RequireAuthorization()
            .MapPost("/{itemKey}/github/branch", Create).RequireOrgRole(OrgRole.Member).RequireItemProjectWritable().RequireScope(Scopes.Write);
        return api;
    }

    private static async Task<IResult> Create(string itemKey, CreateGitHubBranchRequest request, WorkItemsDbContext work,
        IntegrationsDbContext integrations, GitHubAppClient github, IProjectAccess access, ICurrentUser user,
        ICurrentTenant tenant, TimeProvider clock, CancellationToken ct)
    {
        if (request.RepoId <= 0 || !string.Equals(request.From, "default", StringComparison.OrdinalIgnoreCase))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["request"] = ["repoId is required and from must be default."] });
        var item = await WorkItemEndpoints.FindWritable(work, access, user, itemKey, ct); if (item is null) return Results.NotFound();
        var binding = await integrations.RepoBindings.AsNoTracking().SingleOrDefaultAsync(x => x.ProjectId == item.ProjectId && x.RepoId == request.RepoId, ct);
        if (binding is null || !await integrations.GitHubInstallations.AnyAsync(x => x.InstallationId == binding.InstallationId && x.Status == GitHubInstallationStatus.Active, ct)) return Results.NotFound();
        var name = Aictiq.SharedKernel.Text.BranchNames.For(item.Key, item.Title);
        var branch = await github.CreateDefaultBranchAsync(binding.InstallationId, binding.FullName, name, ct);
        var externalId = $"{binding.RepoId}:{name}";
        var link = await work.ItemLinks.SingleOrDefaultAsync(x => x.ItemId == item.Id && x.Provider == "github" && x.Kind == ItemLinkKind.Branch && x.ExternalId == externalId, ct);
        if (link is null)
        {
            link = new ItemLink { OrganizationId = tenant.OrganizationId!.Value, ItemId = item.Id, Kind = ItemLinkKind.Branch,
                Provider = "github", ExternalId = externalId, Url = branch.Url, Title = name, CreatedAt = clock.GetUtcNow(), Meta = $"{{\"Branch\":\"{name}\"}}" };
            work.ItemLinks.Add(link);
            try { await work.SaveChangesAsync(ct); }
            catch (DbUpdateException) { link = await work.ItemLinks.SingleAsync(x => x.ItemId == item.Id && x.Provider == "github" && x.Kind == ItemLinkKind.Branch && x.ExternalId == externalId, ct); }
        }
        return Results.Ok(new { link = ItemRelationsEndpoints.ToView(link), created = branch.Created });
    }
}

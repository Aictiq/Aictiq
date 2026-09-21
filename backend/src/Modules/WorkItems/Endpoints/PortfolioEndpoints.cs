using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Aictiq.Modules.Tenancy;
using Aictiq.Modules.WorkItems.Domain;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Tenancy;

namespace Aictiq.Modules.WorkItems.Endpoints;

/// <summary>A descendant rollup for a planning item. The item itself is not counted.</summary>
public sealed record PortfolioRollup(int TotalCount, int CompletedCount, decimal PointsTotal, decimal PointsCompleted)
{
    public decimal PercentDoneByCount => TotalCount == 0 ? 0 : decimal.Round(100m * CompletedCount / TotalCount, 1);
    public decimal PercentDoneByPoints => PointsTotal == 0 ? 0 : decimal.Round(100m * PointsCompleted / PointsTotal, 1);
}

/// <summary>One Epic or Feature in the portfolio, including its full descendant progress.</summary>
public sealed record PortfolioItemView(
    Guid Id, Guid ProjectId, string ProjectKey, string? ProjectName, WorkItemType Type,
    string Key, string Title, WorkflowStateCategory StateCategory, DateOnly? DueDate,
    DateTimeOffset CreatedAt, Guid? TeamId, string? TeamName, string? OwnerId, UserSummary? Owner,
    IReadOnlyList<ItemLabelView> Labels, PortfolioRollup Rollup);

public static class PortfolioEndpoints
{
    public static IEndpointRouteBuilder MapPortfolioEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapGroup("/orgs/{orgSlug}/projects/{projectKey}").WithTags("Portfolio").RequireAuthorization()
            .MapGet("/portfolio", ProjectPortfolio).RequireProjectRole(ProjectRole.Guest).RequireScope(Scopes.Read);
        api.MapGroup("/orgs/{orgSlug}").WithTags("Portfolio").RequireAuthorization()
            .MapGet("/portfolio", OrganizationPortfolio).RequireOrgRole(OrgRole.Guest).RequireScope(Scopes.Read);
        return api;
    }

    private static Task<IResult> ProjectPortfolio(HttpContext http, WorkItemsDbContext db, TenancyDbContext tenancy,
        IUserDirectory directory, CancellationToken ct) =>
        PortfolioAsync(db, tenancy, directory, [http.ResolvedProjectId()!.Value], includeProjectName: false, ct);

    private static async Task<IResult> OrganizationPortfolio(WorkItemsDbContext db, TenancyDbContext tenancy,
        IProjectAccess access, ICurrentUser user, IUserDirectory directory, CancellationToken ct)
    {
        // WorkItems deliberately has no tenancy-table joins. Query candidate project ids in
        // this tenant, then ask Tenancy for the effective role on each just as org search does.
        var candidates = await db.Items.AsNoTracking().Select(item => item.ProjectId).Distinct().ToListAsync(ct);
        // Asked one project at a time: the contract is served by a single scoped
        // TenancyDbContext, which tolerates no concurrent queries (Task.WhenAll here was an
        // intermittent 500 whenever a role was not already cached).
        var visible = new List<Guid>(candidates.Count);
        foreach (var id in candidates)
        {
            if (await access.GetProjectRoleAsync(user.UserId!, id, ct) is not null) visible.Add(id);
        }
        return await PortfolioAsync(db, tenancy, directory, visible, includeProjectName: true, ct);
    }

    private static async Task<IResult> PortfolioAsync(WorkItemsDbContext db, TenancyDbContext tenancy, IUserDirectory directory,
        IReadOnlyCollection<Guid> projectIds, bool includeProjectName, CancellationToken ct)
    {
        if (projectIds.Count == 0) return Results.Ok(Array.Empty<PortfolioItemView>());

        var items = await db.Items.AsNoTracking().Where(item => projectIds.Contains(item.ProjectId))
            .OrderBy(item => item.ProjectKey).ThenBy(item => item.Number).ToListAsync(ct);
        var projectNames = includeProjectName
            ? await ProjectNamesAsync(tenancy, projectIds, ct)
            : new Dictionary<Guid, string>();
        var categories = await db.WorkflowStates.AsNoTracking().ToDictionaryAsync(state => state.Id, state => state.Category, ct);
        var planning = items.Where(item => item.Type is WorkItemType.Epic or WorkItemType.Feature).ToList();
        var teamNames = await TeamNamesAsync(tenancy, planning.Select(item => item.TeamId).OfType<Guid>().Distinct().ToArray(), ct);
        var planningIds = planning.Select(item => item.Id).ToArray();
        var labels = await LabelsAsync(db, planningIds, ct);
        var owners = await directory.GetAsync(
            planning.Where(item => item.AssigneeId is not null).Select(item => item.AssigneeId!).Distinct().ToArray(), ct);
        var children = items.Where(item => item.ParentId is not null)
            .GroupBy(item => item.ParentId!.Value).ToDictionary(group => group.Key, group => group.ToList());

        var result = planning.Select(item => new PortfolioItemView(
            item.Id, item.ProjectId, item.ProjectKey, projectNames.GetValueOrDefault(item.ProjectId), item.Type,
            item.Key, item.Title, categories.GetValueOrDefault(item.StateId), item.DueDate, item.CreatedAt,
            item.TeamId, item.TeamId is null ? null : teamNames.GetValueOrDefault(item.TeamId.Value),
            item.AssigneeId, item.AssigneeId is null ? null : owners.GetValueOrDefault(item.AssigneeId),
            labels.GetValueOrDefault(item.Id) ?? [], Rollup(item.Id, children, categories)))
            .OrderBy(item => item.ProjectKey).ThenBy(item => item.Type).ThenBy(item => item.Key)
            .ToList();
        return Results.Ok(result);
    }

    private static async Task<Dictionary<Guid, string>> ProjectNamesAsync(TenancyDbContext tenancy,
        IReadOnlyCollection<Guid> projectIds, CancellationToken ct)
    {
        return await tenancy.Projects.AsNoTracking().Where(project => projectIds.Contains(project.Id))
            .ToDictionaryAsync(project => project.Id, project => project.Name, ct);
    }

    private static async Task<Dictionary<Guid, string>> TeamNamesAsync(TenancyDbContext tenancy,
        IReadOnlyCollection<Guid> teamIds, CancellationToken ct)
    {
        if (teamIds.Count == 0) return [];
        return await tenancy.Teams.AsNoTracking().Where(team => teamIds.Contains(team.Id))
            .ToDictionaryAsync(team => team.Id, team => team.Name, ct);
    }

    private static async Task<Dictionary<Guid, List<ItemLabelView>>> LabelsAsync(WorkItemsDbContext db,
        IReadOnlyCollection<Guid> itemIds, CancellationToken ct)
    {
        if (itemIds.Count == 0) return [];
        var rows = await (from itemLabel in db.ItemLabels.AsNoTracking()
                          join label in db.Labels.AsNoTracking() on itemLabel.LabelId equals label.Id
                          where itemIds.Contains(itemLabel.ItemId)
                          select new { itemLabel.ItemId, Label = new ItemLabelView(label.Id, label.Name, label.Color, label.Group) })
            .ToListAsync(ct);
        return rows.GroupBy(row => row.ItemId).ToDictionary(group => group.Key, group => group.Select(row => row.Label).ToList());
    }

    private static PortfolioRollup Rollup(Guid rootId, IReadOnlyDictionary<Guid, List<WorkItem>> children,
        IReadOnlyDictionary<Guid, WorkflowStateCategory> categories)
    {
        var descendants = new List<WorkItem>();
        var stack = new Stack<Guid>();
        stack.Push(rootId);
        while (stack.TryPop(out var parentId) && children.TryGetValue(parentId, out var directChildren))
            foreach (var child in directChildren)
            {
                descendants.Add(child);
                stack.Push(child.Id);
            }

        var completed = descendants.Where(item => categories.GetValueOrDefault(item.StateId) == WorkflowStateCategory.Completed).ToList();
        return new PortfolioRollup(descendants.Count, completed.Count,
            descendants.Sum(item => item.Points ?? 0), completed.Sum(item => item.Points ?? 0));
    }
}

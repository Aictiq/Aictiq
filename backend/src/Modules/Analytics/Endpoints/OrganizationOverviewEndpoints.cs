using Aictiq.Modules.Tenancy;
using Aictiq.Modules.WorkItems;
using Aictiq.Modules.WorkItems.Domain;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Aictiq.Modules.Analytics.Endpoints;

public sealed record OverviewSprint(Guid Id, string Name, decimal PercentDone, int BlockedCount);
public sealed record OverviewProject(Guid Id, string Key, string Name, int OpenBugs, int BlockedItems, int AgentActionsLast24Hours, IReadOnlyList<OverviewSprint> ActiveSprints);
public sealed record OverviewActivity(string ItemKey, string ItemTitle, string Field, DateTimeOffset At, UserSummary? Actor);
public sealed record OrganizationOverview(int OpenBugs, int BlockedItems, int AgentActionsLast24Hours, IReadOnlyList<OverviewProject> Projects, IReadOnlyList<OverviewActivity> RecentActivity, IReadOnlyList<UserSummary> AgentsAtWork);

/// <summary>Stakeholder-level projection. Every query starts from the caller's visible project
/// ids, which keeps private project facts out of both totals and the detail cards.</summary>
public static class OrganizationOverviewEndpoints
{
    public static IEndpointRouteBuilder MapOrganizationOverviewEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapGet("/orgs/{orgSlug}/overview", Get).WithTags("Analytics").RequireAuthorization()
            .RequireOrgRole(OrgRole.Guest).RequireScope(Scopes.Read);
        return api;
    }

    private static async Task<IResult> Get(WorkItemsDbContext work, TenancyDbContext tenancy, ICurrentTenant tenant,
        ICurrentUser user, IProjectAccess access, IUserDirectory directory, TimeProvider clock, CancellationToken ct)
    {
        var ids = await access.ListVisibleProjectIdsAsync(user.UserId!, tenant.OrganizationId!.Value, ct);
        if (ids.Count == 0) return Results.Ok(new OrganizationOverview(0, 0, 0, [], [], []));
        var projects = await tenancy.Projects.AsNoTracking().Where(project => ids.Contains(project.Id))
            .Select(project => new { project.Id, project.Key, project.Name }).OrderBy(project => project.Key).ToListAsync(ct);
        var completed = await work.WorkflowStates.AsNoTracking().Where(state => state.Category == WorkflowStateCategory.Completed || state.Category == WorkflowStateCategory.Removed).Select(state => state.Id).ToListAsync(ct);
        var items = await work.Items.AsNoTracking().Where(item => ids.Contains(item.ProjectId))
            .Select(item => new { item.Id, item.ProjectId, item.Type, item.StateId, item.SprintId }).ToListAsync(ct);
        var itemIds = items.Select(item => item.Id).ToArray();
        var blocked = itemIds.Length == 0 ? new HashSet<Guid>() : (await work.ItemRelations.AsNoTracking()
            .Where(relation => relation.Kind == ItemRelationKind.Blocks && itemIds.Contains(relation.TargetId)).Select(relation => relation.TargetId).Distinct().ToListAsync(ct)).ToHashSet();
        var sprints = await work.Sprints.AsNoTracking().Where(sprint => sprint.State == SprintState.Active).Select(sprint => new { sprint.Id, sprint.TeamId, sprint.Name }).ToListAsync(ct);
        var teams = await tenancy.Teams.AsNoTracking().Where(team => ids.Contains(team.ProjectId)).Select(team => new { team.Id, team.ProjectId }).ToListAsync(ct);
        var since = clock.GetUtcNow().AddHours(-24);
        var history = await work.ItemHistory.AsNoTracking().Join(work.Items.AsNoTracking(), history => history.ItemId, item => item.Id, (history, item) => new { history.ActorId, history.At, history.Field, item.ProjectId, item.Key, item.Title })
            .Where(row => ids.Contains(row.ProjectId) && row.At >= since).OrderByDescending(row => row.At).Take(100).ToListAsync(ct);
        var actors = await directory.GetAsync([.. history.Select(row => row.ActorId).Distinct()], ct);
        var agentIds = await directory.FilterAgentsAsync(history.Select(row => row.ActorId).Distinct().ToArray(), ct);
        var cards = projects.Select(project =>
        {
            var projectItems = items.Where(item => item.ProjectId == project.Id).ToList();
            var active = sprints.Where(sprint => teams.Any(team => team.Id == sprint.TeamId && team.ProjectId == project.Id)).Select(sprint =>
            {
                var scoped = projectItems.Where(item => item.SprintId == sprint.Id).ToList();
                var done = scoped.Count(item => completed.Contains(item.StateId));
                return new OverviewSprint(sprint.Id, sprint.Name, scoped.Count == 0 ? 0 : Math.Round((decimal)done / scoped.Count * 100, 1), scoped.Count(item => blocked.Contains(item.Id)));
            }).ToList();
            var projectHistory = history.Where(row => row.ProjectId == project.Id).ToList();
            return new OverviewProject(project.Id, project.Key, project.Name, projectItems.Count(item => item.Type == WorkItemType.Bug && !completed.Contains(item.StateId)), projectItems.Count(item => blocked.Contains(item.Id)), projectHistory.Count(row => agentIds.Contains(row.ActorId)), active);
        }).ToList();
        return Results.Ok(new OrganizationOverview(cards.Sum(card => card.OpenBugs), cards.Sum(card => card.BlockedItems), cards.Sum(card => card.AgentActionsLast24Hours), cards,
            history.Take(20).Select(row => new OverviewActivity(row.Key, row.Title, row.Field, row.At, actors.GetValueOrDefault(row.ActorId))).ToList(),
            actors.Where(pair => agentIds.Contains(pair.Key)).Select(pair => pair.Value).OrderBy(agent => agent.DisplayName).ToList()));
    }
}

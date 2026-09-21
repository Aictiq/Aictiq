using System.ComponentModel;
using Aictiq.Modules.Tenancy;
using Aictiq.Modules.Tenancy.Domain;
using Aictiq.Modules.WorkItems.Domain;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Mcp;
using Aictiq.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace Aictiq.Modules.WorkItems.Mcp;

public sealed record McpSprint(Guid Id, Guid TeamId, string Name, string Goal, SprintState State,
    DateOnly StartsOn, DateOnly EndsOn, int TotalItems, int CompletedItems, decimal PointsTotal, decimal PointsDone,
    decimal RemainingHours, object? Taskboard);

/// <summary>Sprint context deliberately stays compact.  The optional taskboard is a summary
/// rather than every card body, which makes it safe to request in an agent planning loop.</summary>
[McpServerToolType]
public sealed class SprintMcpTools(WorkItemsDbContext db, TenancyDbContext tenancy, ICurrentUser user,
    IProjectAccess access)
{
    [McpServerTool(Name = "list_sprints", ReadOnly = true)]
    [Description("Lists sprints for a visible team id.")]
    public async Task<IReadOnlyList<McpSprint>> ListSprints(Guid team, CancellationToken cancellationToken = default)
    {
        var visible = await TeamAsync(team, cancellationToken); if (visible is null) return [];
        var sprints = await db.Sprints.AsNoTracking().Where(x => x.TeamId == team).OrderByDescending(x => x.StartsOn).ToListAsync(cancellationToken);
        return await ViewsAsync(sprints, false, cancellationToken);
    }

    [McpServerTool(Name = "get_sprint", ReadOnly = true)]
    [Description("Gets a sprint, optionally with compact taskboard totals by workflow state.")]
    public async Task<McpSprint?> GetSprint(Guid id, bool includeTaskboard = false, CancellationToken cancellationToken = default)
    {
        var sprint = await db.Sprints.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (sprint is null || await TeamAsync(sprint.TeamId, cancellationToken) is null) throw new McpAnswerException($"sprint '{id}' not found or no access");
        return (await ViewsAsync([sprint], includeTaskboard, cancellationToken)).Single();
    }

    private async Task<Team?> TeamAsync(Guid teamId, CancellationToken ct)
    {
        var team = await tenancy.Teams.AsNoTracking().SingleOrDefaultAsync(x => x.Id == teamId, ct);
        return team is not null && await access.GetProjectRoleAsync(user.UserId!, team.ProjectId, ct) is not null ? team : null;
    }

    private async Task<IReadOnlyList<McpSprint>> ViewsAsync(IReadOnlyList<Sprint> sprints, bool includeTaskboard, CancellationToken ct)
    {
        if (sprints.Count == 0) return [];
        var ids = sprints.Select(x => x.Id).ToArray();
        var work = await db.Items.AsNoTracking().Where(x => x.SprintId != null && ids.Contains(x.SprintId.Value))
            .Select(x => new { x.SprintId, x.StateId, x.Points, x.RemainingHours }).ToListAsync(ct);
        var states = await db.WorkflowStates.AsNoTracking().Where(x => work.Select(i => i.StateId).Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        return sprints.Select(sprint =>
        {
            var scoped = work.Where(x => x.SprintId == sprint.Id).ToList();
            var done = scoped.Where(x => states.GetValueOrDefault(x.StateId)?.Category == WorkflowStateCategory.Completed).ToList();
            object? taskboard = includeTaskboard ? scoped.GroupBy(x => states.GetValueOrDefault(x.StateId)?.Name ?? "Unknown")
                .OrderBy(x => x.Key).Select(x => new { state = x.Key, items = x.Count(), remainingHours = x.Sum(i => i.RemainingHours ?? 0) }).ToList() : null;
            return new McpSprint(sprint.Id, sprint.TeamId, sprint.Name, sprint.Goal, sprint.State, sprint.StartsOn, sprint.EndsOn,
                scoped.Count, done.Count, scoped.Sum(x => x.Points ?? 0), done.Sum(x => x.Points ?? 0), scoped.Sum(x => x.RemainingHours ?? 0), taskboard);
        }).ToList();
    }
}

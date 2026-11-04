using Aictiq.Modules.Tenancy;
using Aictiq.Modules.WorkItems.Domain;
using Aictiq.SharedKernel.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Aictiq.Modules.WorkItems;

internal sealed class ProjectWorkflowAccess(WorkItemsDbContext db, TenancyDbContext tenancy) : IProjectWorkflowAccess
{
    public async Task<IReadOnlyDictionary<Guid, string>> GetStateNamesAsync(
        Guid projectId, IReadOnlyCollection<Guid> stateIds, CancellationToken cancellationToken = default) =>
        await db.WorkflowStates.AsNoTracking()
            .Where(state => stateIds.Contains(state.Id)
                && db.Workflows.Any(workflow => workflow.Id == state.WorkflowId && workflow.ProjectId == projectId))
            .ToDictionaryAsync(state => state.Id, state => state.Name, cancellationToken);

    public async Task<bool> StatesBelongToProjectAsync(
        Guid projectId, IReadOnlyCollection<Guid> stateIds, CancellationToken cancellationToken = default)
    {
        var distinct = stateIds.Distinct().ToArray();
        if (distinct.Length == 0) return true;
        var found = await db.WorkflowStates.AsNoTracking()
            .Where(state => distinct.Contains(state.Id)
                && db.Workflows.Any(workflow => workflow.Id == state.WorkflowId && workflow.ProjectId == projectId))
            .Select(state => state.Id)
            .Distinct()
            .CountAsync(cancellationToken);
        return found == distinct.Length;
    }

    public async Task<bool> ColumnsShowStatesAsync(
        Guid projectId, IReadOnlyCollection<(Guid ColumnId, Guid StateId)> columns, CancellationToken cancellationToken = default)
    {
        if (columns.Count == 0) return true;
        var teamIds = await tenancy.Teams.AsNoTracking().Where(team => team.ProjectId == projectId)
            .Select(team => team.Id).ToListAsync(cancellationToken);
        // Board columns live in each board's jsonb config, so the project's boards are read whole.
        var boardColumns = (await db.Boards.AsNoTracking()
                .Where(board => teamIds.Contains(board.TeamId) && board.Kind == BoardKind.Kanban)
                .ToListAsync(cancellationToken))
            .SelectMany(board => board.Config.Columns).ToList();
        return columns.All(pair => boardColumns.Any(column => column.Id == pair.ColumnId && column.StateIds.Contains(pair.StateId)));
    }
}

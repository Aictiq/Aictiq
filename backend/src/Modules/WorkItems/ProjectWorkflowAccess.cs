using Aictiq.SharedKernel.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Aictiq.Modules.WorkItems;

internal sealed class ProjectWorkflowAccess(WorkItemsDbContext db) : IProjectWorkflowAccess
{
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
}

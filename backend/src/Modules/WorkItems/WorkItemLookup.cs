using Aictiq.Modules.WorkItems.Domain;
using Aictiq.SharedKernel.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Aictiq.Modules.WorkItems;

internal sealed class WorkItemLookup(WorkItemsDbContext db) : IWorkItemLookup
{
    public async Task<IReadOnlyList<WorkItemReference>> FindByKeysAsync(Guid projectId, IReadOnlyCollection<string> keys, CancellationToken cancellationToken = default)
    {
        if (keys.Count == 0) return [];
        var wanted = keys.Select(x => x.ToUpperInvariant()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rows = await db.Items.AsNoTracking().Where(x => x.ProjectId == projectId)
            .Join(db.WorkflowStates.AsNoTracking(), item => item.StateId, state => state.Id, (item, state) => new { item, state })
            .ToListAsync(cancellationToken);
        return rows.Where(x => wanted.Contains(x.item.Key)).Select(x => new WorkItemReference(x.item.Id, x.item.Key, x.item.Title, x.state.Category.ToString(), x.item.AssigneeId)).ToList();
    }

    public async Task<IReadOnlyList<WorkItemSummary>> GetSummariesAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken = default)
    {
        if (itemIds.Count == 0) return [];
        var ids = itemIds.Distinct().ToArray();
        var rows = await db.Items.AsNoTracking().Where(x => ids.Contains(x.Id))
            .Join(db.WorkflowStates.AsNoTracking(), item => item.StateId, state => state.Id,
                (item, state) => new { item.Id, item.ProjectId, item.Key, item.Title, item.Type, State = state.Name })
            .ToListAsync(cancellationToken);
        return rows.Select(x => new WorkItemSummary(x.Id, x.ProjectId, x.Key, x.Title, TypeName(x.Type), x.State)).ToList();
    }

    private static string TypeName(WorkItemType type) => type switch
    {
        WorkItemType.Epic => "epic",
        WorkItemType.Feature => "feature",
        WorkItemType.Story => "story",
        WorkItemType.Task => "task",
        _ => "bug"
    };
}

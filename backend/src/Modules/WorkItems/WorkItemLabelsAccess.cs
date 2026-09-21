using Aictiq.SharedKernel.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Aictiq.Modules.WorkItems;

internal sealed class WorkItemLabelsAccess(WorkItemsDbContext db) : IWorkItemLabels
{
    public Task<bool> ItemHasLabelAsync(Guid itemId, Guid labelId, CancellationToken cancellationToken = default) =>
        db.ItemLabels.AsNoTracking().AnyAsync(x => x.ItemId == itemId && x.LabelId == labelId, cancellationToken);

    public async Task<bool> LabelsBelongToProjectAsync(
        Guid projectId, IReadOnlyCollection<Guid> labelIds, CancellationToken cancellationToken = default)
    {
        var distinct = labelIds.Distinct().ToArray();
        if (distinct.Length == 0) return true;
        var found = await db.Labels.AsNoTracking()
            .Where(label => label.ProjectId == projectId && distinct.Contains(label.Id))
            .Select(label => label.Id).Distinct().CountAsync(cancellationToken);
        return found == distinct.Length;
    }
}

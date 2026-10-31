using Aictiq.Modules.WorkItems.Domain;
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

    public async Task<IReadOnlyList<string>> ItemLabelNamesAsync(Guid itemId, CancellationToken cancellationToken = default) =>
        await db.ItemLabels.AsNoTracking().Where(x => x.ItemId == itemId)
            .Join(db.Labels.AsNoTracking(), link => link.LabelId, label => label.Id, (link, label) => label.Name)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<string, string>> PullRequestStatesAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        var links = await db.ItemLinks.AsNoTracking()
            .Where(x => x.ItemId == itemId && x.Kind == ItemLinkKind.PullRequest && x.State != null)
            .Select(x => new { x.Url, x.State })
            .ToListAsync(cancellationToken);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var link in links) result[link.Url.TrimEnd('/')] = link.State!;
        return result;
    }
}

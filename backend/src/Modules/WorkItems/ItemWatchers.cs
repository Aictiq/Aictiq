using Microsoft.EntityFrameworkCore;
using Aictiq.Modules.WorkItems.Domain;
using Aictiq.SharedKernel.Contracts;

namespace Aictiq.Modules.WorkItems;

internal static class ItemWatcherRules
{
    /// <summary>Add a watcher only when they have not explicitly muted this item.</summary>
    public static async Task AddAsync(WorkItemsDbContext db, WorkItem item, string? userId,
        ItemWatchReason reason, DateTimeOffset now, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(userId)) return;
        // Unsaved watchers first: creating an item assigned to its author adds the same
        // person twice before anything is saved, and the database cannot see the first one.
        if (db.ItemWatchers.Local.Any(x => x.ItemId == item.Id && x.UserId == userId)) return;
        var watcher = await db.ItemWatchers.SingleOrDefaultAsync(x => x.ItemId == item.Id && x.UserId == userId, ct);
        if (watcher is null)
            db.ItemWatchers.Add(new ItemWatcher { OrganizationId = item.OrganizationId, ItemId = item.Id, UserId = userId, Reason = reason, CreatedAt = now });
    }
}

internal sealed class WorkItemWatchers(WorkItemsDbContext db) : IItemWatchers
{
    public async Task<IReadOnlyList<string>> ListAsync(Guid itemId, CancellationToken cancellationToken = default) =>
        await db.ItemWatchers.AsNoTracking().Where(x => x.ItemId == itemId && x.MutedAt == null)
            .OrderBy(x => x.CreatedAt).ThenBy(x => x.UserId).Select(x => x.UserId).ToListAsync(cancellationToken);
}

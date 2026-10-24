using Aictiq.Modules.Automation.Domain;
using Aictiq.SharedKernel.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Aictiq.Modules.Automation.Access;

/// <summary>A run waits for its time while it is queued and its start time is still ahead - the dispatcher's own rule.</summary>
public sealed class AutomationScheduledRuns(AutomationDbContext db, TimeProvider clock) : IScheduledRuns
{
    public async Task<IReadOnlyDictionary<Guid, DateTimeOffset>> WaitingAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken = default)
    {
        if (itemIds.Count == 0) return new Dictionary<Guid, DateTimeOffset>();
        var now = clock.GetUtcNow();
        var waiting = await db.Runs.AsNoTracking()
            .Where(run => run.ItemId != null && itemIds.Contains(run.ItemId.Value)
                && run.Status == RunStatus.Queued && run.ScheduledFor != null && run.ScheduledFor > now)
            .Select(run => new { ItemId = run.ItemId!.Value, ScheduledFor = run.ScheduledFor!.Value })
            .ToListAsync(cancellationToken);
        // One live run per item (ux_runs_item_live), so at most one start time each.
        return waiting.GroupBy(run => run.ItemId).ToDictionary(group => group.Key, group => group.Min(run => run.ScheduledFor));
    }
}

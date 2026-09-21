using Aictiq.SharedKernel.Domain;

namespace Aictiq.Modules.WorkItems.Domain;

public enum ItemWatchReason : short { Manual, Assignee, Author, Commenter, Mentioned }

/// <summary>
/// A muted row is deliberately retained.  It records an explicit opt-out so a future
/// mention, assignment, or comment cannot silently opt the person back in.
/// </summary>
public sealed class ItemWatcher : TenantEntity
{
    public Guid ItemId { get; init; }
    public required string UserId { get; init; }
    public ItemWatchReason Reason { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? MutedAt { get; set; }
}

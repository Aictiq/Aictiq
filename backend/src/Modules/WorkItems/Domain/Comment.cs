using Aictiq.Modules.WorkItems.Contracts;
using Aictiq.SharedKernel.Domain;
using NpgsqlTypes;

namespace Aictiq.Modules.WorkItems.Domain;

/// <summary>A tenant-scoped discussion entry. Deletion is deliberately a tombstone.</summary>
public sealed class Comment : TenantEntity
{
    public Guid ItemId { get; init; }
    public required string AuthorId { get; init; }
    public required string BodyMarkdown { get; set; }
    public required string BodyHtml { get; set; }
    public string[] MentionedUserIds { get; set; } = [];
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? EditedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    /// <summary>
    /// The integration event that produced a system comment, when one did - a run
    /// outcome, for instance. The (item_id, event_id) unique index is what makes the event handler's
    /// write idempotent under the outbox's at-least-once delivery. Comments written by
    /// people leave it null.
    /// </summary>
    public Guid? EventId { get; init; }
    /// <summary>Database-generated full-text document; it is never set by application code.</summary>
    public NpgsqlTsVector Search { get; private set; } = null!;

    public void Added(Guid projectId, IReadOnlyList<string> mentionedUserIds, DateTimeOffset now)
    {
        Raise(new CommentAdded(OrganizationId, projectId, ItemId, Id, AuthorId, mentionedUserIds) { OccurredAt = now });
        Raise(new RealtimeCommentAdded(projectId, ItemId, Id, AuthorId) { OccurredAt = now });
    }
}

/// <summary>Immutable pre-edit body. The database trigger, not this type, makes it append-only.</summary>
public sealed class CommentRevision : EntityBase
{
    public Guid CommentId { get; init; }
    public required string BodyMarkdown { get; init; }
    public required string BodyHtml { get; init; }
    public required string EditedBy { get; init; }
    public DateTimeOffset EditedAt { get; init; }
}

/// <summary>One user may add a given emoji to a comment once.</summary>
public sealed class CommentReaction : TenantEntity
{
    public Guid CommentId { get; init; }
    public required string UserId { get; init; }
    public required string Emoji { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

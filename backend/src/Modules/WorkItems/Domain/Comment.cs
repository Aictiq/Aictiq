using System.Text.RegularExpressions;
using Aictiq.Modules.WorkItems.Contracts;
using Aictiq.SharedKernel.Domain;
using Markdig;
using NpgsqlTypes;

namespace Aictiq.Modules.WorkItems.Domain;

/// <summary>A tenant-scoped discussion entry. Deletion is deliberately a tombstone.</summary>
public sealed class Comment : TenantEntity
{
    public Guid ItemId { get; init; }
    /// <summary>
    /// The thread this comment answers, or null for a comment that starts one. Threads are one
    /// level deep: a reply to a reply is filed under the thread's first comment, so the
    /// parent is always a root.
    /// </summary>
    public Guid? ParentCommentId { get; init; }
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

    public void Added(WorkItem item, IReadOnlyList<string> mentionedUserIds, string? threadAuthorId, DateTimeOffset now)
    {
        Raise(new CommentAdded(OrganizationId, item.ProjectId, ItemId, Id, AuthorId, mentionedUserIds)
        {
            OccurredAt = now, ParentCommentId = ParentCommentId, ThreadAuthorId = threadAuthorId,
            ProjectKey = item.ProjectKey, ItemKey = item.Key, ItemTitle = item.Title, Excerpt = Excerpt(BodyMarkdown)
        });
        Raise(new RealtimeCommentAdded(item.ProjectId, ItemId, Id, AuthorId) { OccurredAt = now });
    }

    /// <summary>People an edit tagged who were not tagged before; only they hear about it.</summary>
    public void Mentioned(WorkItem item, IReadOnlyList<string> newlyMentionedUserIds, DateTimeOffset now)
    {
        if (newlyMentionedUserIds.Count == 0) return;
        Raise(new CommentMentionsAdded(OrganizationId, item.ProjectId, ItemId, Id, AuthorId, newlyMentionedUserIds)
        {
            OccurredAt = now, ProjectKey = item.ProjectKey, ItemKey = item.Key, ItemTitle = item.Title, Excerpt = Excerpt(BodyMarkdown)
        });
    }

    private static readonly MarkdownPipeline PlainText = new MarkdownPipelineBuilder().UseAdvancedExtensions().DisableHtml().Build();

    /// <summary>Enough of the text to decide whether to open the email, not the whole comment.
    /// An email shows it as text, so the Markdown is read rather than quoted: "**Friday**"
    /// arrives as "Friday", a link as its words, an image as its alt text.</summary>
    private static string Excerpt(string markdown)
    {
        const int limit = 600;
        var text = Regex.Replace(Markdig.Markdown.ToPlainText(markdown, PlainText), @"\n{3,}", "\n\n").Trim();
        return text.Length <= limit ? text : text[..limit].TrimEnd() + "…";
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

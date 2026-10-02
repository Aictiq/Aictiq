using Aictiq.Modules.WorkItems.Contracts;
using Aictiq.SharedKernel.Events;
using Aictiq.SharedKernel.Tenancy;

namespace Aictiq.Modules.Automation.Events;

/// <summary>
/// Hands a new comment that mentions an agent to <see cref="RunMentionQueue"/>. Runs in Workers.
/// </summary>
public sealed class AgentMentionHandler(ICurrentTenant currentTenant, RunMentionQueue queue)
    : IDomainEventHandler<CommentAdded>
{
    public Task HandleAsync(CommentAdded @event, CancellationToken cancellationToken) =>
        AgentMentions.RecordAsync(currentTenant, queue, @event.OrganizationId, @event.ProjectId, @event.ItemId,
            @event.ItemKey, @event.CommentId, @event.AuthorId, @event.MentionedUserIds, @event.OccurredAt, cancellationToken);
}

/// <summary>
/// The same for an edit that tags someone the comment did not tag before. The edit's event names
/// only the newly mentioned, so an agent the comment already asked is not asked twice.
/// </summary>
public sealed class AgentMentionEditHandler(ICurrentTenant currentTenant, RunMentionQueue queue)
    : IDomainEventHandler<CommentMentionsAdded>
{
    public Task HandleAsync(CommentMentionsAdded @event, CancellationToken cancellationToken) =>
        AgentMentions.RecordAsync(currentTenant, queue, @event.OrganizationId, @event.ProjectId, @event.ItemId,
            @event.ItemKey, @event.CommentId, @event.AuthorId, @event.MentionedUserIds, @event.OccurredAt, cancellationToken);
}

internal static class AgentMentions
{
    public static async Task RecordAsync(
        ICurrentTenant currentTenant, RunMentionQueue queue, Guid organizationId, Guid projectId, Guid itemId,
        string? itemKey, Guid commentId, string authorId, IReadOnlyList<string> mentionedUserIds, DateTimeOffset at,
        CancellationToken ct)
    {
        // An event written before the key travelled with it cannot name its run; it only notifies.
        if (mentionedUserIds.Count == 0 || itemKey is null)
        {
            return;
        }

        using var tenant = currentTenant is AmbientCurrentTenant ambient ? ambient.Use(organizationId) : null;
        await queue.RecordAsync(projectId, itemId, itemKey, commentId, authorId, mentionedUserIds, at, ct);
    }
}

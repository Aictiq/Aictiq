using Aictiq.Modules.WorkItems.Domain;
using Aictiq.Modules.WorkItems.Endpoints;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Events;
using Aictiq.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Aictiq.Modules.WorkItems.Events;

/// <summary>
/// Answers, as the agent, a comment that asked it to work when no run could start. Replay-safe
/// the way <see cref="RunFinishedHandler"/> is: the comment carries the event's id under
/// <c>ux_comments_item_id_event_id</c>, so a second delivery collides and changes nothing.
/// </summary>
public sealed class RunMentionRefusedHandler(
    WorkItemsDbContext db, ICurrentTenant currentTenant, TimeProvider clock, ILogger<RunMentionRefusedHandler> logger)
    : IDomainEventHandler<RunMentionRefused>
{
    public async Task HandleAsync(RunMentionRefused @event, CancellationToken cancellationToken)
    {
        using var tenant = currentTenant is AmbientCurrentTenant ambient ? ambient.Use(@event.OrganizationId) : null;
        var item = await db.Items.SingleOrDefaultAsync(x => x.Id == @event.ItemId, cancellationToken);
        if (item is null) return;

        var now = clock.GetUtcNow();
        var thread = await AgentReplies.ThreadAsync(db, item.Id, @event.CommentId, cancellationToken);
        var markdown = $"I didn't start a run for this request. {@event.Reason}";
        var comment = new Comment
        {
            OrganizationId = @event.OrganizationId, ItemId = item.Id, AuthorId = @event.AgentId,
            ParentCommentId = thread?.Id,
            BodyMarkdown = markdown, BodyHtml = WorkItemEndpoints.Render(markdown),
            MentionedUserIds = [], CreatedAt = now, EventId = @event.EventId,
        };
        comment.Added(item, mentionedUserIds: [], threadAuthorId: thread?.AuthorId, now);
        db.Comments.Add(comment);

        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.ChangeTracker.Clear();
            logger.LogDebug("RunMentionRefused {EventId} was already applied; skipping the replay", @event.EventId);
        }
    }
}

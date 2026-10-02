using Aictiq.Modules.Notifications.Delivery;
using Aictiq.Modules.Notifications.Domain;
using Aictiq.Modules.WorkItems.Contracts;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Events;
using Aictiq.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Aictiq.Modules.Notifications.Events;

/// <summary>Only the comment author hears about the first addition of an emoji.</summary>
public sealed class CommentReactionNotificationHandler(NotificationsDbContext db, IUserRealtimePublisher realtime,
    NotificationEmailService email, IProjectAccess access, IUserDirectory directory, AmbientCurrentTenant tenant, TimeProvider clock)
    : IDomainEventHandler<CommentReactionAdded>
{
    public async Task HandleAsync(CommentReactionAdded e, CancellationToken ct)
    {
        if (e.ActorId == e.CommentAuthorId) return;
        using var scope = tenant.Use(e.OrganizationId);
        if (await access.GetProjectRoleAsync(e.CommentAuthorId, e.ProjectId, ct) is null) return;
        if (await db.Preferences.AnyAsync(x => x.UserId == e.CommentAuthorId && x.Kind == NotificationKind.Reacted && !x.InApp, ct)) return;

        var notification = await FindAsync();
        if (notification is null)
        {
            var actor = (await directory.GetAsync([e.ActorId], ct)).GetValueOrDefault(e.ActorId)?.DisplayName ?? "Someone";
            notification = new Notification
            {
                OrganizationId = e.OrganizationId, UserId = e.CommentAuthorId, EventId = e.EventId,
                Kind = NotificationKind.Reacted, ProjectId = e.ProjectId, ItemId = e.ItemId, ItemKey = e.ItemKey,
                Message = $"{actor} reacted {e.Emoji} to your comment.", CreatedAt = clock.GetUtcNow()
            };
            db.Notifications.Add(notification);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                db.ChangeTracker.Clear();
                notification = await FindAsync() ?? throw new InvalidOperationException("Reaction notification replay could not be loaded.", ex);
            }
        }
        // Reuse the saved notification on replay: if a worker stopped between the inbox
        // write and email queueing, retry still finishes mail. The queue uses its id too.
        await email.QueueCommentAsync([notification],
            new CommentEmail(e.OrganizationId, e.CommentId, e.ActorId, e.ProjectKey, e.ItemKey, e.ItemTitle, e.Excerpt, e.Emoji), ct);
        await realtime.PublishToUserAsync(notification.UserId, "notification.new", new { eventId = e.EventId }, ct);

        Task<Notification?> FindAsync() => db.Notifications.FirstOrDefaultAsync(x => x.UserId == e.CommentAuthorId && x.EventId == e.EventId, ct);
    }
}

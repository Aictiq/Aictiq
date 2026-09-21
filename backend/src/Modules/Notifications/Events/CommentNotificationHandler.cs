using Aictiq.Modules.Notifications.Domain;
using Aictiq.Modules.Notifications.Delivery;
using Aictiq.Modules.WorkItems.Contracts;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Events;
using Microsoft.EntityFrameworkCore;
using Aictiq.SharedKernel.Tenancy;

namespace Aictiq.Modules.Notifications.Events;

/// <summary>Mentions are explicit recipients; watchers receive the companion comment
/// notification. Actor exclusion and the database unique key make outbox retries safe.</summary>
public sealed class CommentNotificationHandler(NotificationsDbContext db, IItemWatchers watchers, IUserRealtimePublisher realtime,
    NotificationEmailService email, AmbientCurrentTenant tenant, TimeProvider clock)
    : IDomainEventHandler<CommentAdded>
{
    public async Task HandleAsync(CommentAdded e, CancellationToken cancellationToken)
    {
        using var scope = tenant.Use(e.OrganizationId);
        var recipients = (await watchers.ListAsync(e.ItemId, cancellationToken)).Concat(e.MentionedUserIds).Where(x => x != e.AuthorId).Distinct().ToArray();
        if (recipients.Length == 0) return;
        var muted = await db.Preferences.Where(x => recipients.Contains(x.UserId) && !x.InApp && (x.Kind == NotificationKind.Commented || x.Kind == NotificationKind.Mentioned)).ToListAsync(cancellationToken);
        var mutedSet = muted.Select(x => (x.UserId, x.Kind)).ToHashSet(); var now = clock.GetUtcNow();
        var created = new List<Notification>();
        foreach (var recipient in recipients)
        {
            var mentioned = e.MentionedUserIds.Contains(recipient); var kind = mentioned ? NotificationKind.Mentioned : NotificationKind.Commented;
            if (mutedSet.Contains((recipient, kind))) continue;
            var notification = new Notification { OrganizationId = e.OrganizationId, UserId = recipient, EventId = e.EventId, Kind = kind, ProjectId = e.ProjectId, ItemId = e.ItemId, Message = mentioned ? "You were mentioned in a comment." : "A watched item has a new comment.", CreatedAt = now };
            db.Notifications.Add(notification); created.Add(notification);
        }
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await email.QueueImmediateAsync(created, cancellationToken);
            foreach (var recipient in recipients.Where(recipient => !mutedSet.Contains((recipient, e.MentionedUserIds.Contains(recipient) ? NotificationKind.Mentioned : NotificationKind.Commented))))
                await realtime.PublishToUserAsync(recipient, "notification.new", new { eventId = e.EventId }, cancellationToken);
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException) { /* duplicate event/user is an idempotent replay */ }
    }
}

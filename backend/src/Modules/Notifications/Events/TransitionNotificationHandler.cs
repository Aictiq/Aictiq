using Aictiq.Modules.Notifications.Domain;
using Aictiq.Modules.Notifications.Delivery;
using Aictiq.Modules.WorkItems.Contracts;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Events;
using Microsoft.EntityFrameworkCore;
using Aictiq.SharedKernel.Tenancy;

namespace Aictiq.Modules.Notifications.Events;

/// <summary>Watched work changing state is useful even when the watcher did not perform it.</summary>
public sealed class TransitionNotificationHandler(
    NotificationsDbContext db, IItemWatchers watchers, IUserRealtimePublisher realtime, NotificationEmailService email,
    AmbientCurrentTenant tenant, TimeProvider clock)
    : IDomainEventHandler<WorkItemTransitioned>
{
    public async Task HandleAsync(WorkItemTransitioned e, CancellationToken cancellationToken)
    {
        using var scope = tenant.Use(e.OrganizationId);
        var recipients = (await watchers.ListAsync(e.ItemId, cancellationToken))
            .Where(id => id != e.ActorId).Distinct().ToArray();
        if (recipients.Length == 0) return;

        var muted = await db.Preferences.Where(x => recipients.Contains(x.UserId)
                && x.Kind == NotificationKind.Transitioned && !x.InApp)
            .Select(x => x.UserId).ToHashSetAsync(cancellationToken);
        var now = clock.GetUtcNow();
        var created = new List<Notification>();
        foreach (var recipient in recipients.Where(id => !muted.Contains(id)))
        {
            var notification = new Notification
            {
                OrganizationId = e.OrganizationId, UserId = recipient, EventId = e.EventId,
                Kind = NotificationKind.Transitioned, ProjectId = e.ProjectId, ItemId = e.ItemId,
                ItemKey = e.Key, Message = $"A watched item moved to a new workflow state.", CreatedAt = now
            };
            db.Notifications.Add(notification); created.Add(notification);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await email.QueueImmediateAsync(created, cancellationToken);
            foreach (var recipient in recipients.Where(id => !muted.Contains(id)))
                await realtime.PublishToUserAsync(recipient, "notification.new", new { eventId = e.EventId }, cancellationToken);
        }
        catch (DbUpdateException) { /* event replay: the user/event unique key already owns it */ }
    }
}

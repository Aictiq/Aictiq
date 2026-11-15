using Aictiq.Modules.Notifications.Delivery;
using Aictiq.Modules.Notifications.Domain;
using Aictiq.Modules.WorkItems.Contracts;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Events;
using Aictiq.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Aictiq.Modules.Notifications.Events;

/// <summary>Assignment is addressed only to the new assignee, independently of watchers.</summary>
public sealed class AssignmentNotificationHandler(NotificationsDbContext db, IProjectAccess access,
    IUserRealtimePublisher realtime, NotificationEmailService email, AmbientCurrentTenant tenant, TimeProvider clock)
    : IDomainEventHandler<WorkItemAssigned>
{
    public async Task HandleAsync(WorkItemAssigned e, CancellationToken cancellationToken)
    {
        using var scope = tenant.Use(e.OrganizationId);
        if (e.AssigneeId == e.ActorId ||
            await access.GetProjectRoleAsync(e.AssigneeId, e.ProjectId, cancellationToken) is null)
            return;
        if (await db.Preferences.AnyAsync(p => p.UserId == e.AssigneeId &&
                p.Kind == NotificationKind.Assigned && !p.InApp, cancellationToken))
            return;

        // The inbox and immediate email commit together. A failed email queue operation
        // must leave the durable event retryable without an orphaned inbox entry.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await db.Notifications.AnyAsync(n => n.UserId == e.AssigneeId && n.EventId == e.EventId, cancellationToken))
        {
            var notification = new Notification
            {
                OrganizationId = e.OrganizationId, UserId = e.AssigneeId, EventId = e.EventId,
                Kind = NotificationKind.Assigned, ProjectId = e.ProjectId, ItemId = e.ItemId,
                ItemKey = e.Key, ActorId = e.ActorId, Subject = e.ItemTitle,
                Message = $"{e.Key} was assigned to you.", CreatedAt = clock.GetUtcNow()
            };
            db.Notifications.Add(notification);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                await email.QueueImmediateAsync([notification], cancellationToken, assignment: e);
                await transaction.CommitAsync(cancellationToken);
            }
            catch (DbUpdateException exception) when (exception.InnerException is PostgresException
                { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "ix_notifications_user_id_event_id" })
            {
                // A concurrent delivery already committed this recipient/event.
                await transaction.RollbackAsync(cancellationToken);
                db.ChangeTracker.Clear();
            }
        }
        else
            await transaction.CommitAsync(cancellationToken);

        // Replays may repeat this invalidation, but never the inbox entry or email.
        await realtime.PublishToUserAsync(e.AssigneeId, "notification.new", new { eventId = e.EventId }, cancellationToken);
    }
}

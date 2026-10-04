using Aictiq.Modules.Notifications.Domain;
using Aictiq.Modules.Notifications.Delivery;
using Aictiq.Modules.WorkItems.Contracts;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Events;
using Microsoft.EntityFrameworkCore;
using Aictiq.SharedKernel.Tenancy;

namespace Aictiq.Modules.Notifications.Events;

/// <summary>Watched work changing state is useful even when the watcher did not perform it.
/// It is also an org-wide event, posted to shared channels whether anyone watches or not.</summary>
public sealed class TransitionNotificationHandler(
    NotificationsDbContext db, IItemWatchers watchers, IUserRealtimePublisher realtime, NotificationEmailService email,
    ChatNotificationService chat, IProjectWorkflowAccess workflows, AmbientCurrentTenant tenant, TimeProvider clock)
    : IDomainEventHandler<WorkItemTransitioned>
{
    public async Task HandleAsync(WorkItemTransitioned e, CancellationToken cancellationToken)
    {
        using var scope = tenant.Use(e.OrganizationId);
        var states = await workflows.GetStateNamesAsync(e.ProjectId, [e.FromStateId, e.ToStateId], cancellationToken);
        var toState = states.GetValueOrDefault(e.ToStateId) ?? "a new state";
        var move = $"{states.GetValueOrDefault(e.FromStateId) ?? "Previous state"} → {toState}";
        await chat.PublishToOrganizationAsync(e.OrganizationId, e.EventId, NotificationKind.Transitioned,
            new ChatMessage($"{e.Key} moved to {toState}", string.Join("\n", new[] { e.ItemTitle, move }.Where(s => s is not null)),
                await chat.ItemUrlAsync(e.OrganizationId, e.Key, cancellationToken)), cancellationToken);

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
                ItemKey = e.Key, RunId = e.Run?.Id, Message = $"A watched item moved to a new workflow state.", CreatedAt = now
            };
            db.Notifications.Add(notification); created.Add(notification);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await email.QueueImmediateAsync(created, cancellationToken, e);
            await chat.QueueAsync(created, new ChatDetails(e.ItemTitle, move), respectPresence: true, cancellationToken);
            foreach (var recipient in recipients.Where(id => !muted.Contains(id)))
                await realtime.PublishToUserAsync(recipient, "notification.new", new { eventId = e.EventId }, cancellationToken);
        }
        catch (DbUpdateException) { /* event replay: the user/event unique key already owns it */ }
    }
}

using Aictiq.Modules.Notifications.Delivery;
using Aictiq.Modules.Notifications.Domain;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Events;
using Aictiq.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Aictiq.Modules.Notifications.Events;

/// <summary>
/// A finished run tells the person who started it and the item's assignee how it went - in
/// the inbox, by email and on their chat channels - and posts to the organization's shared
/// channels. A refine run that came back with questions is its own kind: someone has to
/// answer before anything else happens. The (user, event) unique key makes a replay a no-op.
/// </summary>
public sealed class RunNotificationHandler(
    NotificationsDbContext db, IWorkItemLookup items, IProjectAccess access, IUserDirectory directory,
    IUserRealtimePublisher realtime, NotificationEmailService email, ChatNotificationService chat,
    AmbientCurrentTenant tenant, TimeProvider clock) : IDomainEventHandler<RunFinished>
{
    public async Task HandleAsync(RunFinished e, CancellationToken cancellationToken)
    {
        using var scope = tenant.Use(e.OrganizationId);
        var kind = e.NeedsInput ? NotificationKind.RunNeedsInput
            : e.Outcome == RunOutcomes.Succeeded ? NotificationKind.RunSucceeded : NotificationKind.RunFailed;
        var item = (await items.FindByKeysAsync(e.ProjectId, [e.ItemKey], cancellationToken)).FirstOrDefault();
        var message = Message(kind, e.ItemKey, e.Outcome);
        // The summary is the agent's own account; a failure reason is operator detail and stays out.
        var summary = string.Join("\n", new[] { item?.Title, kind == NotificationKind.RunFailed ? null : e.Summary }
            .Where(s => !string.IsNullOrWhiteSpace(s)));

        await chat.PublishToOrganizationAsync(e.OrganizationId, e.EventId, kind,
            new ChatMessage(message, summary, await chat.OrganizationUrlAsync(e.OrganizationId, ChatNotificationService.RunPath(e.RunId), cancellationToken), "Open the run"),
            cancellationToken);

        var candidates = new[] { e.RequestedBy, item?.AssigneeId }.OfType<string>().Distinct().ToArray();
        var agents = await directory.FilterAgentsAsync(candidates, cancellationToken);
        var recipients = new List<string>();
        foreach (var candidate in candidates.Where(c => !agents.Contains(c)))
            if (await access.GetProjectRoleAsync(candidate, e.ProjectId, cancellationToken) is not null
                && await access.CanOperateFactoryAsync(candidate, e.OrganizationId, cancellationToken))
                recipients.Add(candidate);
        if (recipients.Count == 0) return;
        var muted = await db.Preferences.Where(x => recipients.Contains(x.UserId) && x.Kind == kind && !x.InApp)
            .Select(x => x.UserId).ToHashSetAsync(cancellationToken);

        var now = clock.GetUtcNow();
        var created = new List<Notification>();
        foreach (var recipient in recipients.Where(id => !muted.Contains(id)))
        {
            var notification = new Notification
            {
                OrganizationId = e.OrganizationId, UserId = recipient, EventId = e.EventId, Kind = kind,
                ProjectId = e.ProjectId, ItemId = e.ItemId, ItemKey = e.ItemKey, RunId = e.RunId,
                Message = message, CreatedAt = now
            };
            db.Notifications.Add(notification); created.Add(notification);
        }
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await email.QueueImmediateAsync(created, cancellationToken);
            await chat.QueueAsync(created, new ChatDetails(item?.Title, kind == NotificationKind.RunFailed ? null : e.Summary),
                respectPresence: true, cancellationToken);
            foreach (var notification in created)
                await realtime.PublishToUserAsync(notification.UserId, "notification.new", new { eventId = e.EventId }, cancellationToken);
        }
        catch (DbUpdateException) { /* event replay: the user/event unique key already owns it */ }
    }

    internal static string Message(NotificationKind kind, string itemKey, string outcome) => kind switch
    {
        NotificationKind.RunNeedsInput => $"The refine run on {itemKey} needs your input.",
        NotificationKind.RunSucceeded => $"The run on {itemKey} succeeded.",
        _ => outcome switch
        {
            RunOutcomes.TimedOut => $"The run on {itemKey} timed out.",
            RunOutcomes.Cancelled => $"The run on {itemKey} was cancelled.",
            _ => $"The run on {itemKey} failed."
        }
    };
}

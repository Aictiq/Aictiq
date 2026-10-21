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
/// channels. A refine run worked on the ticket, not the code, so it never says "succeeded":
/// one that came back with questions asks for an answer, one that submitted the ticket says
/// it is ready for review, and one that ended without either is a failure to start again.
/// The (user, event) unique key makes a replay a no-op.
/// </summary>
public sealed class RunNotificationHandler(
    NotificationsDbContext db, IWorkItemLookup items, IProjectAccess access, IUserDirectory directory,
    IUserRealtimePublisher realtime, NotificationEmailService email, ChatNotificationService chat,
    AmbientCurrentTenant tenant, TimeProvider clock) : IDomainEventHandler<RunFinished>
{
    public async Task HandleAsync(RunFinished e, CancellationToken cancellationToken)
    {
        using var scope = tenant.Use(e.OrganizationId);
        var kind = Kind(e);
        var item = (await items.FindByKeysAsync(e.ProjectId, [e.ItemKey], cancellationToken)).FirstOrDefault();
        var message = Message(kind, e.ItemKey, e.Outcome, e.Refinement);
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
                Message = message, CreatedAt = now, ActorId = e.AgentId, Subject = item?.Title
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

    internal static NotificationKind Kind(RunFinished e) =>
        e.NeedsInput ? NotificationKind.RunNeedsInput
        : e.Outcome != RunOutcomes.Succeeded ? NotificationKind.RunFailed
        : !e.Refinement ? NotificationKind.RunSucceeded
        : e.Refined ? NotificationKind.RunRefined : NotificationKind.RunFailed;

    internal static string Message(NotificationKind kind, string itemKey, string outcome, bool refinement) => kind switch
    {
        NotificationKind.RunNeedsInput => $"The refine run on {itemKey} needs your input.",
        NotificationKind.RunRefined => $"{itemKey} is refined and ready for review.",
        NotificationKind.RunSucceeded => $"The run on {itemKey} succeeded.",
        _ => outcome switch
        {
            RunOutcomes.TimedOut => $"The run on {itemKey} timed out.",
            RunOutcomes.Cancelled => $"The run on {itemKey} was cancelled.",
            RunOutcomes.Succeeded when refinement => $"The refine run on {itemKey} finished without a refined ticket.",
            _ => $"The run on {itemKey} failed."
        }
    };
}

using Aictiq.Modules.Notifications.Domain;
using Aictiq.Modules.Notifications.Delivery;
using Aictiq.Modules.WorkItems.Contracts;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Events;
using Microsoft.EntityFrameworkCore;
using Aictiq.SharedKernel.Tenancy;

namespace Aictiq.Modules.Notifications.Events;

/// <summary>Mentions are explicit recipients, and a reply is addressed to whoever started the
/// thread; watchers receive the companion comment notification. Only the first two are
/// mailed: a watcher sees routine discussion in the inbox, while a comment written to
/// someone reaches them by email. Actor exclusion and the database unique key make outbox
/// retries safe.</summary>
public sealed class CommentNotificationHandler(NotificationsDbContext db, IItemWatchers watchers, IUserRealtimePublisher realtime,
    NotificationEmailService email, IProjectAccess access, IUserDirectory directory, AmbientCurrentTenant tenant, TimeProvider clock)
    : CommentNotifier(db, realtime, email, access, directory, clock), IDomainEventHandler<CommentAdded>
{
    public async Task HandleAsync(CommentAdded e, CancellationToken cancellationToken)
    {
        using var scope = tenant.Use(e.OrganizationId);
        var recipients = (await watchers.ListAsync(e.ItemId, cancellationToken)).Concat(e.MentionedUserIds)
            .Concat(e.ThreadAuthorId is null ? [] : [e.ThreadAuthorId]).Where(x => x != e.AuthorId).Distinct().ToArray();
        // Being tagged says more than being answered, and either says more than watching.
        NotificationKind KindFor(string recipient) =>
            e.MentionedUserIds.Contains(recipient) ? NotificationKind.Mentioned
            : recipient == e.ThreadAuthorId ? NotificationKind.Replied
            : NotificationKind.Commented;
        recipients = await WithoutStakeholdersAsync(e.OrganizationId, recipients, [e.AuthorId, .. e.ThreadAuthorId is null ? [] : new[] { e.ThreadAuthorId }], cancellationToken);
        await NotifyAsync(e.EventId, e.OrganizationId, e.ProjectId, e.ItemId, e.ItemKey, recipients, KindFor,
            new CommentEmail(e.OrganizationId, e.CommentId, e.AuthorId, e.ProjectKey, e.ItemKey, e.ItemTitle, e.Excerpt), cancellationToken);
    }
}

/// <summary>An edit that tags someone new tells only them - never the watchers again.</summary>
public sealed class CommentMentionsNotificationHandler(NotificationsDbContext db, IUserRealtimePublisher realtime,
    NotificationEmailService email, IProjectAccess access, IUserDirectory directory, AmbientCurrentTenant tenant, TimeProvider clock)
    : CommentNotifier(db, realtime, email, access, directory, clock), IDomainEventHandler<CommentMentionsAdded>
{
    public async Task HandleAsync(CommentMentionsAdded e, CancellationToken cancellationToken)
    {
        using var scope = tenant.Use(e.OrganizationId);
        var recipients = e.MentionedUserIds.Where(x => x != e.AuthorId).Distinct().ToArray();
        recipients = await WithoutStakeholdersAsync(e.OrganizationId, recipients, [e.AuthorId], cancellationToken);
        await NotifyAsync(e.EventId, e.OrganizationId, e.ProjectId, e.ItemId, e.ItemKey, recipients, _ => NotificationKind.Mentioned,
            new CommentEmail(e.OrganizationId, e.CommentId, e.AuthorId, e.ProjectKey, e.ItemKey, e.ItemTitle, e.Excerpt), cancellationToken);
    }
}

/// <summary>Writes one inbox row per recipient and mails the ones the comment was addressed to.</summary>
public abstract class CommentNotifier(NotificationsDbContext db, IUserRealtimePublisher realtime,
    NotificationEmailService email, IProjectAccess access, IUserDirectory directory, TimeProvider clock)
{
    /// <summary>
    /// A comment an agent wrote, or one in a thread an agent started, is the factory at work,
    /// and whoever may not operate the factory never sees it on the item. They are not told
    /// about it either: a notification would point at a comment that is not there for them.
    /// </summary>
    protected async Task<string[]> WithoutStakeholdersAsync(Guid organizationId, string[] recipients,
        string[] factoryAuthors, CancellationToken cancellationToken)
    {
        if (recipients.Length == 0 || (await directory.FilterAgentsAsync(factoryAuthors, cancellationToken)).Count == 0) return recipients;
        var operators = new List<string>(recipients.Length);
        // One at a time: the contract is served by a single scoped DbContext.
        foreach (var recipient in recipients)
            if (await access.CanOperateFactoryAsync(recipient, organizationId, cancellationToken)) operators.Add(recipient);
        return [.. operators];
    }

    protected async Task NotifyAsync(Guid eventId, Guid organizationId, Guid projectId, Guid itemId, string? itemKey,
        string[] recipients, Func<string, NotificationKind> kindFor, CommentEmail comment, CancellationToken cancellationToken)
    {
        if (recipients.Length == 0) return;
        var muted = await db.Preferences.Where(x => recipients.Contains(x.UserId) && !x.InApp
            && (x.Kind == NotificationKind.Commented || x.Kind == NotificationKind.Mentioned || x.Kind == NotificationKind.Replied)).ToListAsync(cancellationToken);
        var mutedSet = muted.Select(x => (x.UserId, x.Kind)).ToHashSet(); var now = clock.GetUtcNow();
        var created = new List<Notification>();
        foreach (var recipient in recipients)
        {
            var kind = kindFor(recipient);
            if (mutedSet.Contains((recipient, kind))) continue;
            var message = kind switch
            {
                NotificationKind.Mentioned => "You were mentioned in a comment.",
                NotificationKind.Replied => "Someone replied to your comment.",
                _ => "A watched item has a new comment."
            };
            var notification = new Notification { OrganizationId = organizationId, UserId = recipient, EventId = eventId, Kind = kind, ProjectId = projectId, ItemId = itemId, ItemKey = itemKey, Message = message, CreatedAt = now };
            db.Notifications.Add(notification); created.Add(notification);
        }
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await email.QueueCommentAsync(created.Where(n => n.Kind is NotificationKind.Mentioned or NotificationKind.Replied).ToArray(), comment, cancellationToken);
            foreach (var notification in created)
                await realtime.PublishToUserAsync(notification.UserId, "notification.new", new { eventId }, cancellationToken);
        }
        catch (DbUpdateException) { /* duplicate event/user is an idempotent replay */ }
    }
}

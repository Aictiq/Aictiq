using Aictiq.Modules.Notifications.Domain;
using Aictiq.Modules.Notifications.Templates;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Aictiq.Modules.Notifications.Delivery;

/// <summary>Turns a saved in-app notification into an independently deliverable email.
/// The notification id is also the queue id, giving immediate mail the same replay
/// protection as the integration-event email path.</summary>
public sealed class NotificationEmailService(
    NotificationsDbContext db, IUserDirectory users, IOrganizationLookup organizations, INotificationPresence presence,
    EmailTemplateRenderer renderer, NotificationUnsubscribeTokens unsubscribe,
    IOptions<NotificationEmailOptions> options, IOptions<EmailOptions> emailOptions, TimeProvider clock)
{
    public async Task QueueImmediateAsync(IReadOnlyList<Notification> notifications, CancellationToken ct)
    {
        if (!options.Value.EmailEnabled || notifications.Count == 0) return;
        var ids = notifications.Select(n => n.UserId).Distinct().ToArray();
        var profiles = await users.GetDeliveryProfilesAsync(ids, ct);
        var preferences = await db.Preferences.AsNoTracking().Where(p => ids.Contains(p.UserId)).ToListAsync(ct);
        var modes = preferences.ToDictionary(p => (p.UserId, p.Kind), p => p.EmailMode);
        foreach (var notification in notifications)
        {
            if (!profiles.TryGetValue(notification.UserId, out var recipient) || recipient.IsAgent ||
                await presence.IsActiveAsync(notification.UserId, TimeSpan.FromMinutes(options.Value.PresenceMinutes), ct))
                continue;
            if (modes.TryGetValue((notification.UserId, notification.Kind), out var mode) &&
                mode != EmailNotificationMode.Immediate)
                continue;
            var variables = Variables(notification, recipient, unsubscribe.Create(notification.UserId, notification.Kind),
                emailOptions.Value.BaseUrl);
            var rendered = renderer.Render("notification", variables);
            db.EmailOutbox.Add(new EmailOutboxMessage
            {
                Id = notification.Id, ToAddress = recipient.Email, Template = "notification",
                Subject = rendered.Subject, BodyHtml = rendered.Html, BodyText = rendered.Text,
                SendAfter = clock.GetUtcNow(), CreatedAt = clock.GetUtcNow()
            });
        }
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { db.ChangeTracker.Clear(); } // notification replay already queued it
    }

    /// <summary>
    /// Mail for someone a comment was addressed to: tagged in it, or answered in their own
    /// thread. It says who wrote what on which item and links straight to the comment.
    /// Unlike the general path it is not held back while the recipient is online - a person
    /// who was asked something by name should find it in their mail regardless - but their
    /// own "email me about this" choice still decides.
    /// </summary>
    public async Task QueueCommentAsync(IReadOnlyList<Notification> notifications, CommentEmail comment, CancellationToken ct)
    {
        if (!options.Value.EmailEnabled || notifications.Count == 0) return;
        var ids = notifications.Select(n => n.UserId).Append(comment.AuthorId).Distinct().ToArray();
        var profiles = await users.GetDeliveryProfilesAsync(ids, ct);
        var preferences = await db.Preferences.AsNoTracking().Where(p => ids.Contains(p.UserId)).ToListAsync(ct);
        var modes = preferences.ToDictionary(p => (p.UserId, p.Kind), p => p.EmailMode);
        var organization = await organizations.FindByIdAsync(comment.OrganizationId, ct);
        var actorName = profiles.TryGetValue(comment.AuthorId, out var author) ? author.DisplayName : "Someone";
        var itemUrl = CommentLink(organization?.Slug, comment, emailOptions.Value.BaseUrl);
        var now = clock.GetUtcNow();
        foreach (var notification in notifications)
        {
            if (!profiles.TryGetValue(notification.UserId, out var recipient) || recipient.IsAgent) continue;
            if (modes.TryGetValue((notification.UserId, notification.Kind), out var mode) && mode != EmailNotificationMode.Immediate)
                continue;
            var template = notification.Kind == NotificationKind.Replied ? "comment-reply" : "mention";
            var rendered = renderer.Render(template, new Dictionary<string, string>
            {
                ["organizationName"] = organization?.Name ?? "",
                ["recipientName"] = recipient.DisplayName,
                ["actorName"] = actorName,
                ["itemKey"] = comment.ItemKey ?? "",
                ["itemTitle"] = comment.ItemTitle ?? "",
                ["excerpt"] = comment.Excerpt ?? "",
                ["itemUrl"] = itemUrl,
                ["unsubscribeUrl"] = unsubscribe.Create(notification.UserId, notification.Kind)
            });
            db.EmailOutbox.Add(new EmailOutboxMessage
            {
                Id = notification.Id, ToAddress = recipient.Email, Template = template,
                Subject = rendered.Subject, BodyHtml = rendered.Html, BodyText = rendered.Text,
                SendAfter = now, CreatedAt = now
            });
        }
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { db.ChangeTracker.Clear(); } // notification replay already queued it
    }

    /// <summary>The item page, scrolled to the comment. Falls back to the inbox when an older
    /// event did not carry enough to build it.</summary>
    internal static string CommentLink(string? organizationSlug, CommentEmail comment, string? emailBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(emailBaseUrl) || organizationSlug is null || comment.ProjectKey is null || comment.ItemKey is null)
            return Link(null, emailBaseUrl);
        return $"{emailBaseUrl.TrimEnd('/')}/o/{Uri.EscapeDataString(organizationSlug)}/p/{Uri.EscapeDataString(comment.ProjectKey)}" +
            $"/items/{Uri.EscapeDataString(comment.ItemKey)}#comment-{comment.CommentId}";
    }

    internal static Dictionary<string, string> Variables(Notification notification, UserDeliveryProfile recipient, string unsubscribeUrl,
        string? emailBaseUrl) =>
        new()
        {
            ["recipientName"] = recipient.DisplayName,
            ["message"] = notification.Message,
            ["itemKey"] = notification.ItemKey ?? "",
            ["notificationUrl"] = Link(notification.ItemId, emailBaseUrl),
            ["unsubscribeUrl"] = unsubscribeUrl
        };

    internal static string Link(Guid? itemId, string? emailBaseUrl) =>
        string.IsNullOrWhiteSpace(emailBaseUrl) ? "/inbox" :
        $"{emailBaseUrl.TrimEnd('/')}{(itemId is null ? "/inbox" : $"/items/{itemId}")}";
}

/// <summary>What a comment email says, gathered from the comment's event.</summary>
public sealed record CommentEmail(
    Guid OrganizationId, Guid CommentId, string AuthorId,
    string? ProjectKey, string? ItemKey, string? ItemTitle, string? Excerpt);

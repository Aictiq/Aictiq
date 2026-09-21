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
    NotificationsDbContext db, IUserDirectory users, INotificationPresence presence,
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

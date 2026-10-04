using System.Security.Cryptography;
using System.Text;
using Aictiq.Modules.Notifications.Domain;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Aictiq.Modules.Notifications.Delivery;

/// <summary>What an event can add to a notification's chat message beyond the notification itself.</summary>
public sealed record ChatDetails(string? ItemTitle = null, string? Excerpt = null, string? Url = null);

/// <summary>
/// Queues chat messages next to the email: a saved notification goes to each of its
/// recipient's connected channels, and an org-wide event to each shared channel that wants
/// that kind. Immediate messages go to <c>chat_outbox</c>; digest ones become a line in
/// <c>chat_digest_entries</c>. Both ids are derived from the cause, so a replayed event
/// queues nothing twice.
/// </summary>
public sealed class ChatNotificationService(
    NotificationsDbContext db, IUserDirectory users, IOrganizationLookup organizations, INotificationPresence presence,
    IProjectAccess access, IOptions<NotificationEmailOptions> options, IOptions<EmailOptions> emailOptions, TimeProvider clock)
{
    /// <param name="respectPresence">Hold immediate messages back from someone using the app right now, as
    /// general email is; a comment addressed to someone is sent regardless.</param>
    public async Task QueueAsync(IReadOnlyList<Notification> notifications, ChatDetails? details, bool respectPresence,
        CancellationToken cancellationToken)
    {
        if (notifications.Count == 0) return;
        var userIds = notifications.Select(n => n.UserId).Distinct().ToArray();
        var channels = (await db.UserChannels.AsNoTracking()
                .Where(c => userIds.Contains(c.UserId) && c.Status == ChatChannelStatus.Active).ToListAsync(cancellationToken))
            .ToLookup(c => c.UserId);
        if (channels.Count == 0) return;
        var modes = await NotificationModes.LoadAsync(db, userIds, notifications.Select(n => n.OrganizationId).Distinct().ToArray(), cancellationToken);
        var profiles = await users.GetDeliveryProfilesAsync(userIds, cancellationToken);
        var active = new Dictionary<string, bool>();
        var now = clock.GetUtcNow();
        var outbox = new List<ChatOutboxMessage>();
        var digest = new List<ChatDigestEntry>();
        foreach (var notification in notifications)
        {
            if (!profiles.TryGetValue(notification.UserId, out var recipient) || recipient.IsAgent) continue;
            ChatMessage? message = null;
            foreach (var channel in channels[notification.UserId])
            {
                var mode = modes.Chat(notification.UserId, notification.OrganizationId, notification.Kind, channel.Type);
                if (mode == EmailNotificationMode.Off) continue;
                if (mode == EmailNotificationMode.Immediate && respectPresence)
                {
                    if (!active.TryGetValue(notification.UserId, out var isActive))
                        active[notification.UserId] = isActive = await presence.IsActiveAsync(notification.UserId,
                            TimeSpan.FromMinutes(options.Value.PresenceMinutes), cancellationToken);
                    if (isActive) continue;
                }
                message ??= await MessageAsync(notification, details, cancellationToken);
                Add(outbox, digest, channel.Id, null, channel.Type, mode, DeriveId(notification.Id, channel.Id), message, now);
            }
        }
        await SaveAsync(outbox, digest, cancellationToken);
    }

    /// <summary>A comment addressed to someone: linked straight to the comment, and never held back for presence.</summary>
    public async Task QueueCommentAsync(IReadOnlyList<Notification> notifications, CommentEmail comment, CancellationToken cancellationToken)
    {
        if (notifications.Count == 0) return;
        var organization = await organizations.FindByIdAsync(comment.OrganizationId, cancellationToken);
        var url = string.IsNullOrWhiteSpace(emailOptions.Value.BaseUrl) ? null
            : NotificationEmailService.CommentLink(organization?.Slug, comment, emailOptions.Value.BaseUrl);
        await QueueAsync(notifications, new ChatDetails(comment.ItemTitle, comment.Excerpt, url), respectPresence: false, cancellationToken);
    }

    /// <summary>Posts an org-wide event to the organization's shared channels. Call it within the tenant.</summary>
    public async Task PublishToOrganizationAsync(Guid organizationId, Guid eventId, NotificationKind kind, ChatMessage message,
        CancellationToken cancellationToken)
    {
        var channels = await db.OrgChannels.AsNoTracking()
            .Where(c => c.OrganizationId == organizationId && c.Status == ChatChannelStatus.Active).ToListAsync(cancellationToken);
        var now = clock.GetUtcNow();
        var outbox = new List<ChatOutboxMessage>();
        var digest = new List<ChatDigestEntry>();
        foreach (var channel in channels)
        {
            var mode = channel.Modes.GetValueOrDefault(kind, EmailNotificationMode.Off);
            if (mode == EmailNotificationMode.Off) continue;
            Add(outbox, digest, channel.Id, organizationId, channel.Type, mode, DeriveId(eventId, channel.Id), message, now);
        }
        await SaveAsync(outbox, digest, cancellationToken);
    }

    /// <summary>The app's own address for an organization's page, or null when this instance has no base URL.</summary>
    public async Task<string?> OrganizationUrlAsync(Guid organizationId, string path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(emailOptions.Value.BaseUrl)) return null;
        var organization = await organizations.FindByIdAsync(organizationId, cancellationToken);
        return organization is null ? null : $"{emailOptions.Value.BaseUrl.TrimEnd('/')}/o/{Uri.EscapeDataString(organization.Slug)}/{path.TrimStart('/')}";
    }

    /// <summary>The item's board link, or the inbox when the key or organization cannot be resolved.</summary>
    public async Task<string> ItemUrlAsync(Guid organizationId, string? itemKey, CancellationToken cancellationToken)
    {
        var organization = await organizations.FindByIdAsync(organizationId, cancellationToken);
        return NotificationEmailService.Link(itemKey, emailOptions.Value.BaseUrl, organization?.Slug);
    }

    private async Task<ChatMessage> MessageAsync(Notification notification, ChatDetails? details, CancellationToken cancellationToken)
    {
        var url = details?.Url;
        if (notification.RunId is { } runId && IsRunKind(notification.Kind)
            && await access.CanOperateFactoryAsync(notification.UserId, notification.OrganizationId, cancellationToken))
            url = await OrganizationUrlAsync(notification.OrganizationId, $"runs/{runId}", cancellationToken);
        url ??= await ItemUrlAsync(notification.OrganizationId, notification.ItemKey, cancellationToken);
        var subject = notification.ItemKey is null ? null
            : details?.ItemTitle is { Length: > 0 } title ? $"{notification.ItemKey} · {title}" : notification.ItemKey;
        var summary = string.Join("\n", new[] { subject, details?.Excerpt }.Where(s => !string.IsNullOrWhiteSpace(s)));
        return new ChatMessage(notification.Message, summary, url);
    }

    public static bool IsRunKind(NotificationKind kind) =>
        kind is NotificationKind.RunSucceeded or NotificationKind.RunFailed or NotificationKind.RunNeedsInput;

    private static void Add(List<ChatOutboxMessage> outbox, List<ChatDigestEntry> digest, Guid channelId, Guid? organizationId,
        ChatChannelType type, EmailNotificationMode mode, Guid id, ChatMessage message, DateTimeOffset now)
    {
        if (mode == EmailNotificationMode.Immediate)
            outbox.Add(new ChatOutboxMessage
            {
                Id = id, ChannelId = channelId, OrganizationId = organizationId, Text = ChatFormatter.Format(type, message),
                SendAfter = now, CreatedAt = now
            });
        else
            digest.Add(new ChatDigestEntry { Id = id, ChannelId = channelId, OrganizationId = organizationId, Line = ChatFormatter.Line(type, message), CreatedAt = now });
    }

    private async Task SaveAsync(List<ChatOutboxMessage> outbox, List<ChatDigestEntry> digest, CancellationToken cancellationToken)
    {
        if (outbox.Count + digest.Count == 0) return;
        var ids = outbox.Select(m => m.Id).Concat(digest.Select(e => e.Id)).ToArray();
        // A replay finds its rows already there; skip them rather than fail the whole batch.
        var existing = (await db.ChatOutbox.Where(m => ids.Contains(m.Id)).Select(m => m.Id).ToListAsync(cancellationToken))
            .Concat(await db.ChatDigestEntries.Where(e => ids.Contains(e.Id)).Select(e => e.Id).ToListAsync(cancellationToken)).ToHashSet();
        db.ChatOutbox.AddRange(outbox.Where(m => !existing.Contains(m.Id)));
        db.ChatDigestEntries.AddRange(digest.Where(e => !existing.Contains(e.Id)));
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException) { db.ChangeTracker.Clear(); } // a concurrent replay queued them first
    }

    internal static Guid DeriveId(Guid cause, Guid channelId) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes($"chat:{cause}:{channelId}"))[..16]);
}

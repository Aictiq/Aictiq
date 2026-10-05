using System.Security.Cryptography;
using System.Text;
using Aictiq.Modules.Notifications.Domain;
using Aictiq.Modules.Notifications.Templates;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Email;
using Aictiq.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aictiq.Modules.Notifications.Delivery;

/// <summary>Checks every five minutes so an 08:00 local digest still arrives after a
/// deploy or brief outage. The per-user local date makes the operation idempotent.</summary>
public sealed class DailyNotificationDigestService(IServiceScopeFactory scopes, TimeProvider clock,
    ILogger<DailyNotificationDigestService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (Exception ex) { logger.LogError(ex, "Notification digest sweep failed"); }
            try { await RunChatOnceAsync(stoppingToken); }
            catch (Exception ex) { logger.LogError(ex, "Chat digest sweep failed"); }
            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    internal async Task RunOnceAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var options = services.GetRequiredService<IOptions<NotificationEmailOptions>>().Value;
        if (!options.EmailEnabled) return;
        var db = services.GetRequiredService<NotificationsDbContext>();
        var unread = await db.Notifications.IgnoreQueryFilters().AsNoTracking()
            .Where(n => n.ReadAt == null).OrderBy(n => n.CreatedAt).Take(5000).ToListAsync(ct);
        if (unread.Count == 0) return;
        var userIds = unread.Select(n => n.UserId).Distinct().ToArray();
        var preferences = await db.Preferences.AsNoTracking().Where(p => userIds.Contains(p.UserId)
            && p.EmailMode == EmailNotificationMode.Digest).ToListAsync(ct);
        var digestKinds = preferences.Select(p => (p.UserId, p.Kind)).ToHashSet();
        var pending = unread.Where(n => digestKinds.Contains((n.UserId, n.Kind)))
            .GroupBy(n => n.UserId).ToArray();
        var profiles = await services.GetRequiredService<IUserDirectory>().GetDeliveryProfilesAsync(pending.Select(g => g.Key).ToArray(), ct);
        var digests = await db.Digests.Where(d => pending.Select(g => g.Key).Contains(d.UserId))
            .ToDictionaryAsync(d => d.UserId, ct);
        var renderer = services.GetRequiredService<EmailTemplateRenderer>();
        var email = services.GetRequiredService<IOptions<EmailOptions>>().Value;
        var now = clock.GetUtcNow();

        foreach (var group in pending)
        {
            if (!profiles.TryGetValue(group.Key, out var person) || person.IsAgent) continue;
            var zone = Zone(person.TimeZone);
            var localNow = TimeZoneInfo.ConvertTime(now, zone);
            if (localNow.Hour != 8) continue;
            var localDay = DateOnly.FromDateTime(localNow.DateTime);
            if (digests.TryGetValue(person.Id, out var prior) && prior.LastDeliveredLocalDate == localDay) continue;
            var items = group.ToArray();
            var variables = new Dictionary<string, string>
            {
                ["recipientName"] = person.DisplayName, ["organizationName"] = "Aictiq",
                ["periodName"] = "daily", ["periodLabel"] = "since your last digest",
                ["inboxUrl"] = NotificationEmailService.Link(null, email.BaseUrl),
                ["summary"] = string.Join("\n", items.Take(50).Select(n => $"• {n.Message}"))
            };
            var rendered = renderer.Render("digest", variables);
            db.EmailOutbox.Add(new EmailOutboxMessage
            {
                Id = DigestId(person.Id, localDay), ToAddress = person.Email, Template = "digest",
                Subject = rendered.Subject, BodyHtml = rendered.Html, BodyText = rendered.Text,
                SendAfter = now, CreatedAt = now
            });
            if (prior is null)
            {
                prior = new NotificationDigest { UserId = person.Id };
                db.Digests.Add(prior);
            }
            prior.LastDeliveredLocalDate = localDay; prior.LastDeliveredAt = now;
        }
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { db.ChangeTracker.Clear(); } // concurrent/restarted sweep
    }

    /// <summary>
    /// One message per chat channel per day, listing the lines queued for it since the last.
    /// A person's channel goes at 08:00 in their time zone; a shared one at 08:00 UTC. The
    /// lines are consumed with the digest, and the message id is derived from the channel
    /// and date, so a restarted sweep cannot post the same digest twice. Public so tests can drive it.
    /// </summary>
    public async Task RunChatOnceAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<NotificationsDbContext>();
        var waiting = await db.ChatDigestEntries.AsNoTracking()
            .GroupBy(e => new { e.ChannelId, e.OrganizationId }).Select(g => g.Key).Take(1000).ToListAsync(ct);
        if (waiting.Count == 0) return;
        var now = clock.GetUtcNow();
        var tenant = services.GetRequiredService<AmbientCurrentTenant>();

        var personal = waiting.Where(w => w.OrganizationId is null).Select(w => w.ChannelId).ToArray();
        var userChannels = await db.UserChannels.Where(c => personal.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
        var profiles = await services.GetRequiredService<IUserDirectory>()
            .GetDeliveryProfilesAsync(userChannels.Values.Select(c => c.UserId).Distinct().ToArray(), ct);
        foreach (var channelId in personal)
        {
            if (!userChannels.TryGetValue(channelId, out var channel) || !profiles.TryGetValue(channel.UserId, out var person))
            {
                await DropEntriesAsync(db, channelId, ct);
                continue;
            }
            var localNow = TimeZoneInfo.ConvertTime(now, Zone(person.TimeZone));
            var localDay = DateOnly.FromDateTime(localNow.DateTime);
            if (localNow.Hour != 8 || channel.LastDigestLocalDate == localDay) continue;
            await PostDigestAsync(db, channel, null, "Your Aictiq daily digest", localDay, now, ct);
            channel.LastDigestLocalDate = localDay;
            await db.SaveChangesAsync(ct);
        }

        var today = DateOnly.FromDateTime(now.UtcDateTime);
        foreach (var shared in waiting.Where(w => w.OrganizationId is not null))
        {
            using var _ = tenant.Use(shared.OrganizationId!.Value);
            var channel = await db.OrgChannels.SingleOrDefaultAsync(c => c.Id == shared.ChannelId, ct);
            if (channel is null)
            {
                await DropEntriesAsync(db, shared.ChannelId, ct);
                continue;
            }
            if (now.UtcDateTime.Hour != 8 || channel.LastDigestDate == today) continue;
            await PostDigestAsync(db, channel, channel.OrganizationId, $"Aictiq daily digest · {channel.Name}", today, now, ct);
            channel.LastDigestDate = today;
            await db.SaveChangesAsync(ct);
        }
    }

    private static async Task PostDigestAsync(NotificationsDbContext db, IChatChannel channel, Guid? organizationId,
        string heading, DateOnly day, DateTimeOffset now, CancellationToken ct)
    {
        var entries = await db.ChatDigestEntries.Where(e => e.ChannelId == channel.Id && e.CreatedAt <= now)
            .OrderBy(e => e.CreatedAt).ToListAsync(ct);
        db.ChatDigestEntries.RemoveRange(entries);
        // A channel that stopped working loses its lines with the digest it cannot receive.
        if (channel.Status != ChatChannelStatus.Active || entries.Count == 0) return;
        var id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"chat-digest:{channel.Id}:{day:yyyy-MM-dd}"))[..16]);
        if (await db.ChatOutbox.AnyAsync(m => m.Id == id, ct)) return;
        db.ChatOutbox.Add(new ChatOutboxMessage
        {
            Id = id, ChannelId = channel.Id, OrganizationId = organizationId, SendAfter = now, CreatedAt = now,
            Text = ChatFormatter.Digest(channel.Type, heading, entries.Select(e => e.Line).ToArray())
        });
    }

    private static Task DropEntriesAsync(NotificationsDbContext db, Guid channelId, CancellationToken ct) =>
        db.ChatDigestEntries.Where(e => e.ChannelId == channelId).ExecuteDeleteAsync(ct);

    private static TimeZoneInfo Zone(string? id)
    {
        try { return string.IsNullOrWhiteSpace(id) ? TimeZoneInfo.Utc : TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.Utc; }
        catch (InvalidTimeZoneException) { return TimeZoneInfo.Utc; }
    }

    private static Guid DigestId(string userId, DateOnly date) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes($"digest:{userId}:{date:yyyy-MM-dd}"))[..16]);
}

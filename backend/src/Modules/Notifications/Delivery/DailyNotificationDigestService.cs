using System.Security.Cryptography;
using System.Text;
using Aictiq.Modules.Notifications.Domain;
using Aictiq.Modules.Notifications.Templates;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Email;
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

    private static TimeZoneInfo Zone(string? id)
    {
        try { return string.IsNullOrWhiteSpace(id) ? TimeZoneInfo.Utc : TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.Utc; }
        catch (InvalidTimeZoneException) { return TimeZoneInfo.Utc; }
    }

    private static Guid DigestId(string userId, DateOnly date) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes($"digest:{userId}:{date:yyyy-MM-dd}"))[..16]);
}

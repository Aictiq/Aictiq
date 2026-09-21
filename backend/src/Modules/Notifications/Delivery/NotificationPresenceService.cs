using Aictiq.Modules.Notifications.Domain;
using Microsoft.EntityFrameworkCore;

namespace Aictiq.Modules.Notifications.Delivery;

public interface INotificationPresence
{
    Task SeenAsync(string userId, CancellationToken cancellationToken = default);
    Task<bool> IsActiveAsync(string userId, TimeSpan window, CancellationToken cancellationToken = default);
}

public sealed class NotificationPresenceService(NotificationsDbContext db, TimeProvider clock) : INotificationPresence
{
    public async Task SeenAsync(string userId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId)) return;
        var row = await db.Presence.SingleOrDefaultAsync(x => x.UserId == userId, cancellationToken);
        if (row is null) db.Presence.Add(new NotificationPresence { UserId = userId, LastSeenAt = clock.GetUtcNow() });
        else row.LastSeenAt = clock.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task<bool> IsActiveAsync(string userId, TimeSpan window, CancellationToken cancellationToken = default) =>
        db.Presence.AsNoTracking().AnyAsync(x => x.UserId == userId && x.LastSeenAt >= clock.GetUtcNow() - window, cancellationToken);
}

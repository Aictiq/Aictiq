using Aictiq.SharedKernel.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Aictiq.Modules.Notifications;

/// <summary>Notifications owns the count; Identity only consumes this small read seam.</summary>
public sealed class UnreadNotificationCounter(NotificationsDbContext db) : IUnreadNotificationCounter
{
    public Task<int> CountUnreadAsync(string userId, CancellationToken cancellationToken = default) =>
        db.Notifications.IgnoreQueryFilters().CountAsync(x => x.UserId == userId && x.ReadAt == null, cancellationToken);
}

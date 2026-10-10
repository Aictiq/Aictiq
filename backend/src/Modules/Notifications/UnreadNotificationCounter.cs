using Aictiq.SharedKernel.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Aictiq.Modules.Notifications;

/// <summary>Notifications owns the count; Identity only consumes this small read seam.</summary>
public sealed class UnreadNotificationCounter(NotificationVisibility visibility) : IUnreadNotificationCounter
{
    public async Task<int> CountUnreadAsync(string userId, CancellationToken cancellationToken = default) =>
        await (await visibility.QueryAsync(userId, unreadOnly: true, cancellationToken)).CountAsync(cancellationToken);
}

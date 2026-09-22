namespace Aictiq.SharedKernel.Contracts;

/// <summary>Read-model seam used by the session shell without coupling Identity to Notifications.</summary>
public interface IUnreadNotificationCounter
{
    Task<int> CountUnreadAsync(string userId, CancellationToken cancellationToken = default);
}

public sealed class NullUnreadNotificationCounter : IUnreadNotificationCounter
{
    public Task<int> CountUnreadAsync(string userId, CancellationToken cancellationToken = default) => Task.FromResult(0);
}

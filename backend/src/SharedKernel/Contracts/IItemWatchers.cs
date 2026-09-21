namespace Aictiq.SharedKernel.Contracts;

/// <summary>
/// The notification module asks WorkItems who should receive an item notification
/// through this seam.  User ids, rather than user records, keep Identity ownership
/// where it belongs and let the notification delivery choose its own presentation.
/// </summary>
public interface IItemWatchers
{
    Task<IReadOnlyList<string>> ListAsync(Guid itemId, CancellationToken cancellationToken = default);
}

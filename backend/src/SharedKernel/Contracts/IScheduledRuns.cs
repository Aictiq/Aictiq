namespace Aictiq.SharedKernel.Contracts;

/// <summary>
/// Which items have a run waiting for its start time. Implemented by Automation, which owns
/// runs; asked by WorkItems so a board card can say an agent is booked for later, which the
/// item's claim alone cannot tell apart from an agent at work.
/// </summary>
public interface IScheduledRuns
{
    /// <summary>
    /// The start time of each of <paramref name="itemIds"/> whose run is queued with a start
    /// time still in the future. Items without one are absent.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, DateTimeOffset>> WaitingAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken = default);
}

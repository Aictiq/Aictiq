using Microsoft.Extensions.Logging;

namespace Aictiq.SharedKernel.Contracts;

/// <summary>
/// Pushes a change to everyone watching a project. Implemented over SignalR;
/// the contract exists now so modules can publish from the day they are written rather
/// than being retrofitted later.
/// </summary>
public interface IRealtimePublisher
{
    Task PublishAsync(
        Guid projectId, string eventName, object payload, CancellationToken cancellationToken = default);
}

/// <summary>Pushes a private change to all of one person's live connections.</summary>
public interface IUserRealtimePublisher
{
    Task PublishToUserAsync(
        string userId, string eventName, object payload, CancellationToken cancellationToken = default);
}

/// <summary>
/// Pushes a change to everyone watching one run's live log (the hub's <c>run:{id}</c>
/// group). Log lines travel here rather than on the project group: a stream of
/// agent output is exactly the raw harness text a project's non-operators must not
/// receive, and the hub only admits to this group through the operator-gated log reads.
/// </summary>
public interface IRunRealtimePublisher
{
    Task PublishToRunAsync(
        Guid runId, string eventName, object payload, CancellationToken cancellationToken = default);
}

/// <summary>
/// The default until SignalR lands. It logs at Debug rather than doing nothing silently,
/// so a missing realtime backend is visible when someone wonders why the board did not
/// move on its own.
/// </summary>
public sealed class NullRealtimePublisher(ILogger<NullRealtimePublisher> logger) : IRealtimePublisher, IUserRealtimePublisher, IRunRealtimePublisher
{
    public Task PublishAsync(
        Guid projectId, string eventName, object payload, CancellationToken cancellationToken = default)
    {
        logger.LogDebug(
            "Realtime event {EventName} for project {ProjectId} was dropped: no realtime backend is configured.",
            eventName, projectId);
        return Task.CompletedTask;
    }

    public Task PublishToUserAsync(
        string userId, string eventName, object payload, CancellationToken cancellationToken = default)
    {
        logger.LogDebug("Realtime event {EventName} for user {UserId} was dropped: no realtime backend is configured.", eventName, userId);
        return Task.CompletedTask;
    }

    public Task PublishToRunAsync(
        Guid runId, string eventName, object payload, CancellationToken cancellationToken = default)
    {
        logger.LogDebug("Realtime event {EventName} for run {RunId} was dropped: no realtime backend is configured.", eventName, runId);
        return Task.CompletedTask;
    }
}

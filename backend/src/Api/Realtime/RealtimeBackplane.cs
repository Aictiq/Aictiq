using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using Npgsql;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Realtime;

namespace Aictiq.Api.Realtime;

/// <summary>For the Redis option, SignalR itself distributes a normal group send.</summary>
public sealed class SignalRRealtimePublisher(IHubContext<ProjectHub> hub) : IRealtimePublisher, IUserRealtimePublisher, IRunRealtimePublisher
{
    public Task PublishAsync(Guid projectId, string eventName, object payload, CancellationToken cancellationToken = default) =>
        hub.Clients.Group(ProjectHub.Group(projectId)).SendAsync(eventName, payload, cancellationToken);

    public Task PublishToUserAsync(string userId, string eventName, object payload, CancellationToken cancellationToken = default) =>
        hub.Clients.Group(ProjectHub.UserGroup(userId)).SendAsync(eventName, payload, cancellationToken);

    public Task PublishToRunAsync(Guid runId, string eventName, object payload, CancellationToken cancellationToken = default) =>
        hub.Clients.Group(ProjectHub.RunGroup(runId)).SendAsync(eventName, payload, cancellationToken);
}

/// <summary>
/// LISTEN loop that forwards each Postgres envelope to SignalR connections.
///
/// Under the postgres backplane every replica listens and sends to its own connections.
/// Under redis the API publishes through SignalR directly, and this loop exists only for
/// what Workers publish over NOTIFY; a Redis group send already reaches every replica, so
/// exactly one replica may forward - it holds a session advisory lock on the listening
/// connection, and another replica takes over when that connection dies.
/// </summary>
public sealed class RealtimeBackplaneListener(
    NpgsqlDataSource dataSource,
    IHubContext<ProjectHub> hub,
    IOptions<RealtimeOptions> options,
    ILogger<RealtimeBackplaneListener> logger) : BackgroundService
{
    private const string ForwarderLock = "aictiq_rt_forwarder";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var singleForwarder = string.Equals(options.Value.Backplane, "redis", StringComparison.OrdinalIgnoreCase);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = await dataSource.OpenConnectionAsync(stoppingToken);
                if (singleForwarder)
                {
                    await using var takeLock = new NpgsqlCommand("SELECT pg_try_advisory_lock(hashtext(@key))", connection);
                    takeLock.Parameters.AddWithValue("key", ForwarderLock);
                    if (await takeLock.ExecuteScalarAsync(stoppingToken) is not true)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                        continue;
                    }
                }
                connection.Notification += (_, args) => _ = FanOutAsync(args.Payload, stoppingToken);
                await using (var listen = new NpgsqlCommand($"LISTEN {RealtimeBackplane.Channel}", connection))
                    await listen.ExecuteNonQueryAsync(stoppingToken);
                while (!stoppingToken.IsCancellationRequested) await connection.WaitAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "LISTEN {Channel} interrupted; reconnecting in 5 s", RealtimeBackplane.Channel);
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task FanOutAsync(string payload, CancellationToken cancellationToken)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<RealtimeEnvelope>(payload, RealtimeBackplane.Json);
            if (envelope is null
                || (envelope.ProjectId is null && string.IsNullOrWhiteSpace(envelope.UserId) && envelope.RunId is null)
                || string.IsNullOrWhiteSpace(envelope.EventName))
            {
                logger.LogWarning("Ignoring malformed {Channel} payload", RealtimeBackplane.Channel);
                return;
            }
            var group = envelope.UserId is { Length: > 0 } ? ProjectHub.UserGroup(envelope.UserId)
                : envelope.RunId is { } runId ? ProjectHub.RunGroup(runId)
                : ProjectHub.Group(envelope.ProjectId!.Value);
            await hub.Clients.Group(group)
                .SendAsync(envelope.EventName, envelope.Payload, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not fan out realtime payload");
        }
    }
}

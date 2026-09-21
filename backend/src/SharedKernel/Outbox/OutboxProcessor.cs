using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Aictiq.SharedKernel.Domain;
using Aictiq.SharedKernel.Events;

namespace Aictiq.SharedKernel.Outbox;

/// <summary>
/// Single consumer of shared.outbox_messages (runs only in the Workers process). FOR
/// UPDATE SKIP LOCKED keeps a second instance safe if one is ever added.
/// </summary>
/// <remarks>
/// Delivery is AT LEAST ONCE, not exactly once. A handler's side effects commit on its
/// own connection; processed_at is marked on this one. A crash in between redelivers the
/// message, so <b>every handler must be idempotent</b> — and, per Aictiq's rule
/// that integrity lives in the database, the idempotency should be a constraint the
/// database enforces rather than a check in the handler. See NoteCreatedHandler and
/// ux_note_activities_note_id_type for the reference pattern.
///
/// A message that fails <see cref="MaxAttempts"/> times is dead-lettered: it stops being
/// retried, increments the outbox.messages.dead_lettered counter, logs at Critical, and
/// turns /health/ready Degraded via <see cref="OutboxHealthCheck"/>. Nothing is dropped
/// silently.
/// </remarks>
public sealed class OutboxProcessor(
    NpgsqlDataSource dataSource,
    IServiceScopeFactory scopeFactory,
    IntegrationEventTypeRegistry registry,
    ILogger<OutboxProcessor> logger) : BackgroundService
{
    public const string MeterName = "Aictiq.Outbox";

    private const int BatchSize = 50;
    private const int MaxAttempts = 10;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> DeadLettered = Meter.CreateCounter<long>(
        "outbox.messages.dead_lettered",
        unit: "{message}",
        description: "Outbox messages that exhausted their retry budget and will never be delivered.");

    private static readonly Counter<long> Processed = Meter.CreateCounter<long>(
        "outbox.messages.processed",
        unit: "{message}",
        description: "Outbox messages dispatched to their handlers successfully.");

    private static readonly Counter<long> Failed = Meter.CreateCounter<long>(
        "outbox.messages.failed",
        unit: "{message}",
        description: "Outbox message dispatch attempts that threw.");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var processed = 0;
            try
            {
                processed = await ProcessPendingAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Fresh database before migrations, transient outage — retry on next tick.
                logger.LogWarning(ex, "Outbox processing failed; retrying on next tick");
            }

            if (processed < BatchSize)
            {
                try
                {
                    await Task.Delay(PollInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    public async Task<int> ProcessPendingAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var batch = new List<(Guid Id, string Type, string Payload)>();
        await using (var select = new NpgsqlCommand(
            $"""
             SELECT id, type, payload FROM shared.outbox_messages
             WHERE processed_at IS NULL AND dead_lettered_at IS NULL
             ORDER BY occurred_at
             LIMIT {BatchSize}
             FOR UPDATE SKIP LOCKED
             """, connection, transaction))
        await using (var reader = await select.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                batch.Add((reader.GetGuid(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        foreach (var (id, typeName, payload) in batch)
        {
            try
            {
                var eventType = registry.Resolve(typeName)
                    ?? throw new InvalidOperationException($"Unknown event type: {typeName}");
                var domainEvent = (IDomainEvent?)JsonSerializer.Deserialize(payload, eventType)
                    ?? throw new InvalidOperationException($"Empty payload for {typeName}");

                using var scope = scopeFactory.CreateScope();
                await scope.ServiceProvider.GetRequiredService<IDomainEventDispatcher>()
                    .DispatchStrictAsync(domainEvent, cancellationToken);

                // The handler already committed on its own connection. If the process dies
                // before this mark lands the message is redelivered — hence the
                // idempotency requirement documented on this class.
                await using var markDone = new NpgsqlCommand(
                    "UPDATE shared.outbox_messages SET processed_at = now() WHERE id = @id",
                    connection, transaction);
                markDone.Parameters.AddWithValue("id", id);
                await markDone.ExecuteNonQueryAsync(cancellationToken);
                Processed.Add(1, new KeyValuePair<string, object?>("event.type", typeName));
            }
            catch (Exception ex)
            {
                Failed.Add(1, new KeyValuePair<string, object?>("event.type", typeName));

                // The attempt counter is incremented and evaluated in one statement, so
                // dead-lettering can't be lost to a race between two processor instances.
                await using var markFailed = new NpgsqlCommand(
                    """
                    UPDATE shared.outbox_messages
                    SET attempts = attempts + 1,
                        last_error = @error,
                        dead_lettered_at = CASE WHEN attempts + 1 >= @max THEN now() END
                    WHERE id = @id
                    RETURNING attempts, dead_lettered_at IS NOT NULL
                    """, connection, transaction);
                markFailed.Parameters.AddWithValue("id", id);
                markFailed.Parameters.AddWithValue("error", ex.Message);
                markFailed.Parameters.AddWithValue("max", MaxAttempts);

                var attempts = 0;
                var deadLettered = false;
                await using (var reader = await markFailed.ExecuteReaderAsync(cancellationToken))
                {
                    if (await reader.ReadAsync(cancellationToken))
                    {
                        attempts = reader.GetInt32(0);
                        deadLettered = reader.GetBoolean(1);
                    }
                }

                if (deadLettered)
                {
                    DeadLettered.Add(1, new KeyValuePair<string, object?>("event.type", typeName));
                    logger.LogCritical(ex,
                        "Outbox message {Id} ({Type}) dead-lettered after {Attempts} attempts; it will NOT be retried",
                        id, typeName, attempts);
                }
                else
                {
                    logger.LogError(ex, "Outbox message {Id} ({Type}) failed (attempt {Attempts}/{MaxAttempts})",
                        id, typeName, attempts, MaxAttempts);
                }
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return batch.Count;
    }
}

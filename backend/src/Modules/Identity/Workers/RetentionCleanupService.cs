using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Aictiq.Modules.Identity.Workers;

/// <summary>
/// Prunes the tables Identity owns that grow without bound (identity.refresh_tokens,
/// identity.personal_access_tokens, audit.audit_log, shared.outbox_messages). Refresh
/// tokens grow fastest, since every rotation adds a row.
/// </summary>
/// <remarks>
/// Runs in the Workers process only: it is a single-writer background job, and putting it
/// in the API would have every instance racing on the same DELETEs. Deletes are batched
/// and looped so the first sweep over a large backlog stays out of long locks. Raw SQL
/// (snake_case, per Aictiq's convention) keeps it independent of the module
/// contexts - it deliberately touches three schemas.
/// </remarks>
public sealed class RetentionCleanupService(
    NpgsqlDataSource dataSource,
    IOptions<RetentionOptions> options,
    TimeProvider timeProvider,
    ILogger<RetentionCleanupService> logger) : BackgroundService
{
    private readonly RetentionOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the API finish migrating before the first sweep touches the tables.
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), timeProvider, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A failed sweep is never fatal - the next tick retries.
                logger.LogWarning(ex, "Retention cleanup failed; retrying on the next sweep");
            }

            try
            {
                await Task.Delay(_options.Interval, timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>One full sweep. Public so integration tests can drive it deterministically.</summary>
    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        // Only tokens that can never be used again: consumed, revoked, or expired. A
        // pending token is left alone regardless of age, and the window is kept wider
        // than Jwt:RefreshTokenDays so a live rotation chain is never truncated.
        await PurgeAsync(connection, "identity.refresh_tokens", _options.RefreshTokenDays,
            """
            WITH doomed AS (
                SELECT id FROM identity.refresh_tokens
                WHERE (used_at IS NOT NULL OR revoked_at IS NOT NULL OR expires_at < @cutoff)
                  AND created_at < @cutoff
                LIMIT @batch),
            purged AS (
                DELETE FROM identity.refresh_tokens t USING doomed d WHERE t.id = d.id RETURNING 1)
            SELECT count(*) FROM purged
            """, cancellationToken);

        // Only tokens that can never be used again. The row outlives its usefulness by
        // this window because the audit entries about it point at its id, and a purged row
        // would leave those entries naming nothing.
        await PurgeAsync(connection, "identity.personal_access_tokens", _options.RevokedAccessTokenDays,
            """
            WITH doomed AS (
                SELECT id FROM identity.personal_access_tokens
                WHERE (revoked_at IS NOT NULL OR (expires_at IS NOT NULL AND expires_at < @cutoff))
                  AND created_at < @cutoff
                LIMIT @batch),
            purged AS (
                DELETE FROM identity.personal_access_tokens t USING doomed d
                WHERE t.id = d.id RETURNING 1)
            SELECT count(*) FROM purged
            """, cancellationToken);

        // audit.audit_log is append-only at the database level, so retention goes through
        // the one function allowed past that trigger - see the AuditRetentionPurge
        // migration. A bare DELETE here would (correctly) raise.
        await PurgeAsync(connection, "audit.audit_log", _options.AuditLogDays,
            "SELECT shared.purge_audit_log(@cutoff, @batch)", cancellationToken);

        // Dead-lettered rows are excluded on purpose: they are open incidents, not history.
        await PurgeAsync(connection, "shared.outbox_messages", _options.ProcessedOutboxDays,
            """
            WITH doomed AS (
                SELECT id FROM shared.outbox_messages
                WHERE processed_at IS NOT NULL AND processed_at < @cutoff
                LIMIT @batch),
            purged AS (
                DELETE FROM shared.outbox_messages m USING doomed d WHERE m.id = d.id RETURNING 1)
            SELECT count(*) FROM purged
            """, cancellationToken);
    }

    /// <param name="sql">
    /// A statement taking @cutoff and @batch and returning the number of rows it deleted
    /// as a single bigint - bounded so a first sweep over a large backlog loops instead of
    /// taking one long lock.
    /// </param>
    private async Task PurgeAsync(
        NpgsqlConnection connection, string table, int retentionDays, string sql, CancellationToken cancellationToken)
    {
        if (retentionDays <= 0)
        {
            return;
        }

        var cutoff = timeProvider.GetUtcNow().AddDays(-retentionDays);
        var total = 0L;

        while (!cancellationToken.IsCancellationRequested)
        {
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("cutoff", cutoff);
            command.Parameters.AddWithValue("batch", _options.BatchSize);
            var deleted = (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);

            total += deleted;
            if (deleted < _options.BatchSize)
            {
                break;
            }
        }

        if (total > 0)
        {
            logger.LogInformation("Retention: deleted {Count} row(s) from {Table} older than {Cutoff:o}",
                total, table, cutoff);
        }
    }
}

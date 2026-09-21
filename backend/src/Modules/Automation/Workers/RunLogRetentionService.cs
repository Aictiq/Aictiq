using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Aictiq.Modules.Automation.Workers;

/// <summary>
/// Prunes what Automation owns that grows without bound: the log chunks of finished
/// runs. Run rows are kept — they are the item's history, sized in bytes, not lines —
/// and so are their summaries, failure reasons, playbook revision references and linked
/// pull requests. Only the raw output goes.
///
/// The window is per organization: <see cref="IPlanAllowances"/> is asked
/// what the plan entitles, and a null answer — every self-hosted instance, and every
/// organization on a plan that says nothing — keeps the operator's configured
/// <see cref="AutomationRetentionOptions.RunLogDays"/>. Automation never reads a billing
/// table to find that out. A pruned log cannot be brought back by subscribing later; the
/// chunks are gone, and nothing else about the run is.
/// </summary>
/// <remarks>
/// Runs in the Workers process only, like Identity's retention sweep. Deletes are
/// batched and looped so a first sweep over a large backlog stays out of long locks.
/// </remarks>
public sealed class RunLogRetentionService(
    IServiceScopeFactory scopeFactory,
    NpgsqlDataSource dataSource,
    IOptions<AutomationRetentionOptions> options,
    TimeProvider clock,
    ILogger<RunLogRetentionService> logger) : BackgroundService
{
    private const int BatchSize = 500;

    private readonly AutomationRetentionOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the API finish migrating before the first sweep touches the tables.
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), clock, stoppingToken);
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
                // A failed sweep is never fatal — the next tick retries.
                logger.LogWarning(ex, "Run log retention failed; retrying on the next sweep");
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(_options.SweepIntervalMinutes), clock, stoppingToken);
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
        var now = clock.GetUtcNow();

        // Row-level security makes the delete per organization, exactly as in
        // <see cref="RunSweeper"/>: the runs the subquery names must be in the session's
        // tenant for the sweep to see them at all. See RunSweeper.RunOnceAsync for the
        // organizations read.
        var organizationIds = new List<Guid>();
        await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken))
        await using (var command = new NpgsqlCommand("SELECT id FROM tenancy.organizations", connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                organizationIds.Add(reader.GetGuid(0));
            }
        }

        foreach (var organizationId in organizationIds)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                using var tenantScope = scope.ServiceProvider.GetRequiredService<AmbientCurrentTenant>()
                    .Use(organizationId);
                var entitled = await scope.ServiceProvider.GetRequiredService<IPlanAllowances>()
                    .GetRunLogRetentionDaysAsync(organizationId, cancellationToken);
                var days = entitled is { } granted && granted > 0 ? granted : _options.RunLogDays;
                await PurgeAsync(scope.ServiceProvider.GetRequiredService<AutomationDbContext>(),
                    now.AddDays(-days), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Run log retention failed for organization {OrganizationId}", organizationId);
            }
        }
    }

    private async Task PurgeAsync(AutomationDbContext db, DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        var total = 0L;
        while (!cancellationToken.IsCancellationRequested)
        {
            // The EXISTS guard is what makes the loop progress: a pass strips every chunk
            // its 500 runs have, so the next pass picks the next 500 rather than naming
            // the same drained runs forever. Zero deleted therefore means done, for this
            // organization and this window.
            var deleted = await db.Database.ExecuteSqlAsync($"""
                DELETE FROM automation.run_log_chunks
                WHERE run_id IN (
                    SELECT r.id FROM automation.runs r
                    WHERE r.finished_at IS NOT NULL AND r.finished_at < {cutoff}
                      AND EXISTS (SELECT 1 FROM automation.run_log_chunks c WHERE c.run_id = r.id)
                    LIMIT {BatchSize})
                """, cancellationToken);
            total += deleted;
            if (deleted == 0)
            {
                break;
            }
        }

        if (total > 0)
        {
            logger.LogInformation("Run log retention: deleted {Count} log chunk(s) of runs finished before {Cutoff:o}",
                total, cutoff);
        }
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aictiq.Modules.Billing.Workers;

/// <summary>
/// The nightly pass: re-derives every billed subscription's seat quantities from
/// the member list, catching anything a per-change sync missed — a Stripe outage that
/// outlasted the outbox's retries, a quantity edited in the Stripe dashboard — and prunes
/// the Stripe event ledger past its de-duplication window.
///
/// Each organization is synced in its own scope and its own failure: one misconfigured
/// price must not stop every other organization's bill from being corrected.
/// </summary>
public sealed class BillingNightlyService(
    IServiceScopeFactory scopes, IOptions<BillingOptions> options, TimeProvider clock,
    ILogger<BillingNightlyService> logger) : BackgroundService
{
    /// <summary>Workers start alongside the API, which migrates; give it a moment.</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartupDelay, clock, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogError(exception, "Billing nightly run failed"); }

            try { await Task.Delay(options.Value.SeatSyncInterval, clock, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<Guid> organizations;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var availability = scope.ServiceProvider.GetRequiredService<BillingAvailability>();
            organizations = availability.IsEnabled
                ? await scope.ServiceProvider.GetRequiredService<SeatSynchronizer>().ListBilledOrganizationsAsync(cancellationToken)
                : [];

            var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
            var cutoff = clock.GetUtcNow().AddDays(-options.Value.StripeEventRetentionDays);
            var pruned = await db.StripeEvents.Where(x => x.ReceivedAt < cutoff).ExecuteDeleteAsync(cancellationToken);
            if (pruned > 0) logger.LogInformation("Pruned {Count} Stripe event id(s) older than {Cutoff}", pruned, cutoff);
        }

        var updated = 0;
        foreach (var organizationId in organizations)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var outcome = await scope.ServiceProvider.GetRequiredService<SeatSynchronizer>()
                    .SyncAsync(organizationId, cancellationToken);
                if (outcome == SeatSyncOutcome.Updated) updated++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Nightly seat sync failed for organization {OrganizationId}", organizationId);
            }
        }

        if (organizations.Count > 0)
        {
            logger.LogInformation("Nightly seat sync checked {Count} subscription(s), corrected {Updated}",
                organizations.Count, updated);
        }
    }
}

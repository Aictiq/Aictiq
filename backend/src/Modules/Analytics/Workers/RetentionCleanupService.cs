using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Aictiq.Modules.Analytics.Workers;

/// <summary>Bounded pruning keeps analytics retention independent from the shared outbox
/// retention policy.</summary>
public sealed class RetentionCleanupService(IServiceScopeFactory scopes, IOptions<AnalyticsOptions> options,
    TimeProvider clock, ILogger<RetentionCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await PurgeAsync(stoppingToken); }
            catch (Exception exception) { logger.LogError(exception, "Analytics retention cleanup failed"); }
            try { await Task.Delay(TimeSpan.FromHours(12), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    internal async Task PurgeAsync(CancellationToken cancellationToken)
    {
        var days = Math.Max(1, options.Value.RetentionDays);
        var cutoff = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime.Date.AddDays(-days));
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AnalyticsDbContext>();
        while (true)
        {
            var stale = await db.ItemStateDaily.IgnoreQueryFilters().Where(row => row.Day < cutoff)
                .OrderBy(row => row.Day).ThenBy(row => row.ItemId).Take(1000).ToListAsync(cancellationToken);
            if (stale.Count == 0) break;
            db.ItemStateDaily.RemoveRange(stale);
            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();
            if (stale.Count < 1000) break;
        }
    }
}

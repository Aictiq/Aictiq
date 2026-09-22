using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Aictiq.Modules.Analytics.Domain;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Tenancy;

namespace Aictiq.Modules.Analytics.Workers;

/// <summary>Creates one idempotent state sample per non-removed item and organization-local
/// date.  A restart simply revisits the current day; the compound primary key prevents a
/// second row.  The five-minute cadence catches a missed 00:05 run without a scheduler.</summary>
public sealed class DailySnapshotService(IServiceScopeFactory scopes, TimeProvider clock,
    ILogger<DailySnapshotService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (Exception exception) { logger.LogError(exception, "Analytics daily snapshot run failed"); }
            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    internal async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var organizations = scope.ServiceProvider.GetRequiredService<IOrganizationTimeZoneSource>();
        foreach (var organization in await organizations.ListAsync(cancellationToken))
        {
            var zone = ResolveTimeZone(organization.TimeZone);
            var localNow = TimeZoneInfo.ConvertTime(clock.GetUtcNow(), zone);
            // The service may start at any time; only write today's sample once local
            // midnight has passed.  The next cadence after 00:05 provides normal timing.
            if (localNow.TimeOfDay < TimeSpan.FromMinutes(5)) continue;
            await SnapshotOrganizationAsync(scope.ServiceProvider, organization.OrganizationId,
                DateOnly.FromDateTime(localNow.DateTime), cancellationToken);
        }
    }

    internal static TimeZoneInfo ResolveTimeZone(string value)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(value); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.Utc; }
        catch (InvalidTimeZoneException) { return TimeZoneInfo.Utc; }
    }

    private async Task SnapshotOrganizationAsync(IServiceProvider services, Guid organizationId, DateOnly day,
        CancellationToken cancellationToken)
    {
        var tenant = services.GetRequiredService<ICurrentTenant>();
        using var tenantScope = tenant is AmbientCurrentTenant ambient ? ambient.Use(organizationId) : null;
        var source = services.GetRequiredService<IWorkItemSnapshotSource>();
        var db = services.GetRequiredService<AnalyticsDbContext>();
        Guid? after = null;
        while (true)
        {
            var page = await source.ListAsync(organizationId, after, 1000, cancellationToken);
            if (page.Count == 0) break;
            var ids = page.Select(item => item.Id).ToArray();
            var existing = await db.ItemStateDaily.Where(row => row.Day == day && ids.Contains(row.ItemId))
                .ToDictionaryAsync(row => row.ItemId, cancellationToken);
            foreach (var item in page)
            {
                if (existing.ContainsKey(item.Id)) continue;
                db.ItemStateDaily.Add(new ItemStateDaily
                {
                    OrganizationId = organizationId, ItemId = item.Id, Day = day, ProjectId = item.ProjectId,
                    StateId = item.StateId, SprintId = item.SprintId, Points = item.Points,
                    EstimateHours = item.EstimateHours, RemainingHours = item.RemainingHours,
                    CompletedHours = item.CompletedHours, CapturedAt = clock.GetUtcNow()
                });
            }
            await db.SaveChangesAsync(cancellationToken);
            after = page[^1].Id;
            if (page.Count < 1000) break;
        }
    }
}

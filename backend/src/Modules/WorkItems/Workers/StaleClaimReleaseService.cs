using Aictiq.Modules.WorkItems.Contracts;
using Aictiq.Modules.WorkItems.Domain;
using Aictiq.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aictiq.Modules.WorkItems.Workers;

/// <summary>Releases abandoned agent claims. Each tenant is opened explicitly because workers fail closed by default.</summary>
public sealed class StaleClaimReleaseService(IServiceScopeFactory scopes, IOptions<ClaimsOptions> options, TimeProvider clock, ILogger<StaleClaimReleaseService> logger) : BackgroundService
{
    private readonly ClaimsOptions _options = options.Value;
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested) { try { await RunOnceAsync(token); } catch (Exception ex) when (!token.IsCancellationRequested) { logger.LogWarning(ex, "Stale claim release sweep failed"); } try { await Task.Delay(_options.SweepInterval, clock, token); } catch (OperationCanceledException) { } }
    }
    public async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<WorkItemsDbContext>(); var tenant = scope.ServiceProvider.GetRequiredService<AmbientCurrentTenant>(); var cutoff = clock.GetUtcNow() - TimeSpan.FromMinutes(Math.Max(1, _options.StaleAfterMinutes));
        var rows = await db.Items.IgnoreQueryFilters().Where(x => x.ClaimedBy != null && x.ClaimHeartbeatAt < cutoff).OrderBy(x => x.ClaimHeartbeatAt).Take(100).ToListAsync(ct);
        foreach (var item in rows) { using var current = tenant.Use(item.OrganizationId); item.ReleaseStaleClaim(clock.GetUtcNow()); }
        if (rows.Count > 0) await db.SaveChangesAsync(ct);
    }
}

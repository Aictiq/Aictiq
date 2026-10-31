using Aictiq.Modules.WorkItems.Domain;
using Aictiq.Modules.WorkItems.Jira;
using Aictiq.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aictiq.Modules.WorkItems.Workers;

/// <summary>
/// Runs queued Jira imports one at a time. A job is claimed with a conditional update, so two
/// worker replicas never run the same one, and a running job that stopped making progress (its
/// worker died) is claimed again: issues it already imported are skipped by key.
/// </summary>
public sealed class JiraImportWorker(IServiceScopeFactory scopes, TimeProvider clock, IOptions<JiraImportOptions> options, ILogger<JiraImportWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try { await RunOnceAsync(token); } catch (Exception ex) when (!token.IsCancellationRequested) { logger.LogWarning(ex, "Jira import worker sweep failed"); }
            try { await Task.Delay(TimeSpan.FromSeconds(2), clock, token); } catch (OperationCanceledException) { break; }
        }
    }

    /// <returns>Whether a job was run.</returns>
    public async Task<bool> RunOnceAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<WorkItemsDbContext>();
        var now = clock.GetUtcNow(); var stale = now - options.Value.StaleAfter;
        var candidate = await db.JiraImportJobs.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.Status == JiraImportStatus.Pending || x.Status == JiraImportStatus.Running && x.HeartbeatAt < stale)
            .OrderBy(x => x.CreatedAt).Select(x => new { x.Id, x.OrganizationId, x.HeartbeatAt }).FirstOrDefaultAsync(ct);
        if (candidate is null) return false;
        var claimed = await db.JiraImportJobs.IgnoreQueryFilters()
            .Where(x => x.Id == candidate.Id && (x.Status == JiraImportStatus.Pending || x.Status == JiraImportStatus.Running && x.HeartbeatAt == candidate.HeartbeatAt))
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.Status, JiraImportStatus.Running).SetProperty(x => x.HeartbeatAt, now).SetProperty(x => x.StartedAt, x => x.StartedAt ?? now), ct);
        if (claimed == 0) return false;

        using var current = scope.ServiceProvider.GetRequiredService<AmbientCurrentTenant>().Use(candidate.OrganizationId);
        var job = await db.JiraImportJobs.SingleAsync(x => x.Id == candidate.Id, ct);
        logger.LogInformation("Jira import {JobId} started for project {ProjectId}", job.Id, job.ProjectId);
        await scope.ServiceProvider.GetRequiredService<JiraImporter>().RunAsync(job, ct);
        var ended = await db.JiraImportJobs.AsNoTracking().SingleAsync(x => x.Id == job.Id, CancellationToken.None);
        logger.LogInformation("Jira import {JobId} ended {Status}: {Created} created, {Skipped} skipped, {Failures} failures", ended.Id, ended.Status, ended.CreatedItems, ended.SkippedIssues, ended.Failures.Count);
        return true;
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Aictiq.Modules.WorkItems.Contracts;
using Aictiq.Modules.WorkItems.Domain;
using Aictiq.SharedKernel.Events;
using Aictiq.SharedKernel.Storage;

namespace Aictiq.Modules.WorkItems.Workers;

/// <summary>Removes uploads that were never committed. The object store is idempotent, so retries are safe.</summary>
public sealed class AttachmentCleanupService(IServiceScopeFactory scopes, IBlobStorage storage,
    IOptions<AttachmentsOptions> options, TimeProvider clock, ILogger<AttachmentCleanupService> logger) : BackgroundService
{
    private readonly AttachmentsOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Attachment orphan cleanup failed; retrying next sweep"); }
            try { await Task.Delay(_options.SweepInterval, clock, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Public for deterministic integration tests.</summary>
    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var cutoff = clock.GetUtcNow() - _options.PendingLifetime;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkItemsDbContext>();
        while (!cancellationToken.IsCancellationRequested)
        {
            // A worker has no current tenant. This is a deliberate global maintenance scan,
            // bounded by status/age and then each object key, never a tenant-facing query.
            var doomed = await db.Attachments.IgnoreQueryFilters()
                .Where(x => x.Status == AttachmentStatus.Pending && x.CreatedAt < cutoff)
                .OrderBy(x => x.CreatedAt).Take(Math.Max(1, _options.SweepBatchSize)).ToListAsync(cancellationToken);
            if (doomed.Count == 0) break;
            foreach (var attachment in doomed)
            {
                await storage.DeleteAsync(attachment.ObjectKey, cancellationToken);
                db.Attachments.Remove(attachment);
            }
            await db.SaveChangesAsync(cancellationToken);
            if (doomed.Count < _options.SweepBatchSize) break;
        }
    }
}

/// <summary>Outbox handler for committed metadata deletion; deleting a missing blob is success.</summary>
public sealed class AttachmentDeletedHandler(IBlobStorage storage) : IDomainEventHandler<AttachmentDeleted>
{
    public Task HandleAsync(AttachmentDeleted @event, CancellationToken cancellationToken) =>
        storage.DeleteAsync(@event.ObjectKey, cancellationToken);
}

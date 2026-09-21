using Microsoft.EntityFrameworkCore;
using Aictiq.Modules.Analytics.Domain;
using Aictiq.Modules.WorkItems.Contracts;
using Aictiq.SharedKernel.Events;
using Aictiq.SharedKernel.Tenancy;
using Microsoft.Extensions.Caching.Hybrid;
using Aictiq.Modules.Analytics.Endpoints;

namespace Aictiq.Modules.Analytics.Events;

/// <summary>Consumes WorkItems' durable stream.  The database keys, rather than in-memory
/// de-duplication, make replay after an outbox retry safe.</summary>
internal sealed class TransitionAnalyticsEventHandler(AnalyticsDbContext db, AmbientCurrentTenant tenant, HybridCache cache) : IDomainEventHandler<WorkItemTransitioned>
{
    public async Task HandleAsync(WorkItemTransitioned @event, CancellationToken cancellationToken)
    {
        // Workers have no request tenant: without this scope the save throws, and the
        // throw fails every other handler of the event with it (notifications, rules).
        using var scope = tenant.Use(@event.OrganizationId);
        db.ItemTransitions.Add(new ItemTransition
        {
            EventId = @event.EventId, OrganizationId = @event.OrganizationId, ProjectId = @event.ProjectId,
            ItemId = @event.ItemId, FromStateId = @event.FromStateId, ToStateId = @event.ToStateId,
            SprintId = @event.SprintId, ActorId = @event.ActorId, At = @event.OccurredAt
        });
        await SaveIdempotentlyAsync(db, cancellationToken);
        if (@event.SprintId is { } sprintId) await cache.RemoveByTagAsync(SprintMetricsEndpoints.SprintCacheTag(sprintId), cancellationToken);
    }

    internal static async Task SaveIdempotentlyAsync(AnalyticsDbContext db, CancellationToken cancellationToken)
    {
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException) { db.ChangeTracker.Clear(); }
    }
}

internal sealed class ScopeAnalyticsEventHandler(AnalyticsDbContext db, AmbientCurrentTenant tenant, HybridCache cache) : IDomainEventHandler<SprintScopeChanged>
{
    public async Task HandleAsync(SprintScopeChanged @event, CancellationToken cancellationToken)
    {
        using var scope = tenant.Use(@event.OrganizationId);
        db.SprintScopeLog.Add(new SprintScopeLog
        {
            EventId = @event.EventId, OrganizationId = @event.OrganizationId, SprintId = @event.SprintId,
            ItemId = @event.ItemId, Added = @event.Added, Points = @event.Points,
            RemainingHours = @event.RemainingHours, At = @event.OccurredAt
        });
        await TransitionAnalyticsEventHandler.SaveIdempotentlyAsync(db, cancellationToken);
        await cache.RemoveByTagAsync(SprintMetricsEndpoints.SprintCacheTag(@event.SprintId), cancellationToken);
    }
}

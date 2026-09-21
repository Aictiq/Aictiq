using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Events;
using Aictiq.SharedKernel.Outbox;
using Aictiq.SharedKernel.Persistence;
using Aictiq.SharedKernel.Storage;
using Aictiq.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Aictiq.Modules.WorkItems.Events;

/// <summary>
/// The objects of deleted items' attachments. The rows went with the items, so the event
/// carries their keys; deleting a missing object is success, so a replay is harmless.
/// </summary>
public sealed class WorkItemsDeletedHandler(IBlobStorage storage) : IDomainEventHandler<WorkItemsDeleted>
{
    public async Task HandleAsync(WorkItemsDeleted @event, CancellationToken cancellationToken)
    {
        foreach (var key in @event.ObjectKeys)
            await storage.DeleteAsync(key, cancellationToken);
    }
}

/// <summary>
/// Everything WorkItems keeps for a deleted project: its items with all they carry, its
/// workflows, labels, templates, views, sprints and boards, and every stored object under the
/// project's prefix — wiki attachments included, and uploads no row remembers.
///
/// Objects go first and rows second, so a failure part-way leaves rows to retry against
/// rather than objects nothing points at. Everything is idempotent: a replay deletes nothing.
/// </summary>
public sealed class ProjectDeletedHandler(WorkItemsDbContext db, ICurrentTenant currentTenant, IBlobStorage storage)
    : IDomainEventHandler<ProjectDeleted>
{
    public async Task HandleAsync(ProjectDeleted @event, CancellationToken cancellationToken)
    {
        using var tenant = currentTenant is AmbientCurrentTenant ambient ? ambient.Use(@event.OrganizationId) : null;
        await storage.DeletePrefixAsync($"org/{@event.OrganizationId}/project/{@event.ProjectId}/", cancellationToken);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var teamIds = @event.TeamIds.ToArray();
        var itemIds = await db.Items.Where(x => x.ProjectId == @event.ProjectId).Select(x => x.Id).ToListAsync(cancellationToken);
        var sprintIds = await db.Sprints.Where(x => teamIds.Contains(x.TeamId)).Select(x => x.Id).ToListAsync(cancellationToken);

        await db.Database.ExecuteSqlAsync($"SELECT work.purge_project({@event.ProjectId}, {teamIds})", cancellationToken);
        await AuditPurge.EntitiesAsync(db, @event.OrganizationId, AuditEntityTypes.WorkItem, itemIds, cancellationToken);
        // Analytics keys its scope log by sprint and cannot ask which project a sprint was in
        // once these rows are gone, so it is told here, in the transaction that forgets them.
        // A replay after this commits finds no sprints, so the follow-up is written once.
        if (sprintIds.Count > 0)
        {
            db.Set<OutboxMessage>().Add(OutboxMessage.From(new SprintsDeleted(@event.OrganizationId, @event.ProjectId, sprintIds)));
            await db.SaveChangesAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }
}

/// <summary>Everything WorkItems keeps for a deleted organization, objects first.</summary>
public sealed class OrganizationDeletedHandler(WorkItemsDbContext db, ICurrentTenant currentTenant, IBlobStorage storage)
    : IDomainEventHandler<OrganizationDeleted>
{
    public async Task HandleAsync(OrganizationDeleted @event, CancellationToken cancellationToken)
    {
        using var tenant = currentTenant is AmbientCurrentTenant ambient ? ambient.Use(@event.OrganizationId) : null;
        await storage.DeletePrefixAsync($"org/{@event.OrganizationId}/", cancellationToken);
        await db.Database.ExecuteSqlAsync($"SELECT work.purge_organization({@event.OrganizationId})", cancellationToken);
    }
}

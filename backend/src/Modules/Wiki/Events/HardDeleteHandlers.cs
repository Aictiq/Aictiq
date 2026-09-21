using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Events;
using Aictiq.SharedKernel.Persistence;
using Aictiq.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Aictiq.Modules.Wiki.Events;

/// <summary>A page that linked a deleted item no longer does; its revisions keep the text they had.</summary>
public sealed class WikiWorkItemsDeletedHandler(WikiDbContext db, ICurrentTenant currentTenant)
    : IDomainEventHandler<WorkItemsDeleted>
{
    public async Task HandleAsync(WorkItemsDeleted @event, CancellationToken cancellationToken)
    {
        using var tenant = currentTenant is AmbientCurrentTenant ambient ? ambient.Use(@event.OrganizationId) : null;
        var ids = @event.ItemIds.ToArray();
        await db.PageItemLinks.Where(x => ids.Contains(x.ItemId)).ExecuteDeleteAsync(cancellationToken);
    }
}

/// <summary>
/// Every page of a deleted project, with its revisions, permissions, item links and audit
/// trail. The pages' attachments are under the project's storage prefix, which WorkItems
/// removes for the same event.
/// </summary>
public sealed class WikiProjectDeletedHandler(WikiDbContext db, ICurrentTenant currentTenant)
    : IDomainEventHandler<ProjectDeleted>
{
    public async Task HandleAsync(ProjectDeleted @event, CancellationToken cancellationToken)
    {
        using var tenant = currentTenant is AmbientCurrentTenant ambient ? ambient.Use(@event.OrganizationId) : null;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var deleted = await db.Database.SqlQuery<Guid>($"""SELECT wiki.purge_project({@event.ProjectId}) AS "Value" """).ToListAsync(cancellationToken);
        await AuditPurge.EntitiesAsync(db, @event.OrganizationId, AuditEntityTypes.WikiPage, deleted, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}

/// <summary>Every wiki row of a deleted organization.</summary>
public sealed class WikiOrganizationDeletedHandler(WikiDbContext db, ICurrentTenant currentTenant)
    : IDomainEventHandler<OrganizationDeleted>
{
    public async Task HandleAsync(OrganizationDeleted @event, CancellationToken cancellationToken)
    {
        using var tenant = currentTenant is AmbientCurrentTenant ambient ? ambient.Use(@event.OrganizationId) : null;
        await db.Database.ExecuteSqlAsync($"SELECT wiki.purge_organization({@event.OrganizationId})", cancellationToken);
    }
}

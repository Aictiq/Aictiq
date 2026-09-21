using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Events;
using Aictiq.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Aictiq.Modules.Analytics.Events;

/// <summary>Deleted items leave no transitions, snapshots or scope changes behind.</summary>
internal sealed class AnalyticsWorkItemsDeletedHandler(AnalyticsDbContext db, AmbientCurrentTenant tenant)
    : IDomainEventHandler<WorkItemsDeleted>
{
    public async Task HandleAsync(WorkItemsDeleted @event, CancellationToken cancellationToken)
    {
        using var scope = tenant.Use(@event.OrganizationId);
        var ids = @event.ItemIds.ToArray();
        await db.ItemTransitions.Where(x => ids.Contains(x.ItemId)).ExecuteDeleteAsync(cancellationToken);
        await db.ItemStateDaily.Where(x => ids.Contains(x.ItemId)).ExecuteDeleteAsync(cancellationToken);
        await db.SprintScopeLog.Where(x => ids.Contains(x.ItemId)).ExecuteDeleteAsync(cancellationToken);
    }
}

/// <summary>A deleted project's dashboards and metrics. Its sprints' scope log arrives as <see cref="SprintsDeleted"/>.</summary>
internal sealed class AnalyticsProjectDeletedHandler(AnalyticsDbContext db, AmbientCurrentTenant tenant)
    : IDomainEventHandler<ProjectDeleted>
{
    public async Task HandleAsync(ProjectDeleted @event, CancellationToken cancellationToken)
    {
        using var scope = tenant.Use(@event.OrganizationId);
        await db.Dashboards.Where(x => x.ProjectId == @event.ProjectId).ExecuteDeleteAsync(cancellationToken);
        await db.ItemTransitions.Where(x => x.ProjectId == @event.ProjectId).ExecuteDeleteAsync(cancellationToken);
        await db.ItemStateDaily.Where(x => x.ProjectId == @event.ProjectId).ExecuteDeleteAsync(cancellationToken);
    }
}

internal sealed class AnalyticsSprintsDeletedHandler(AnalyticsDbContext db, AmbientCurrentTenant tenant)
    : IDomainEventHandler<SprintsDeleted>
{
    public async Task HandleAsync(SprintsDeleted @event, CancellationToken cancellationToken)
    {
        using var scope = tenant.Use(@event.OrganizationId);
        var ids = @event.SprintIds.ToArray();
        await db.SprintScopeLog.Where(x => ids.Contains(x.SprintId)).ExecuteDeleteAsync(cancellationToken);
    }
}

internal sealed class AnalyticsOrganizationDeletedHandler(AnalyticsDbContext db, AmbientCurrentTenant tenant)
    : IDomainEventHandler<OrganizationDeleted>
{
    public async Task HandleAsync(OrganizationDeleted @event, CancellationToken cancellationToken)
    {
        using var scope = tenant.Use(@event.OrganizationId);
        var org = @event.OrganizationId;
        await db.Dashboards.Where(x => x.OrganizationId == org).ExecuteDeleteAsync(cancellationToken);
        await db.ItemTransitions.Where(x => x.OrganizationId == org).ExecuteDeleteAsync(cancellationToken);
        await db.ItemStateDaily.Where(x => x.OrganizationId == org).ExecuteDeleteAsync(cancellationToken);
        await db.SprintScopeLog.Where(x => x.OrganizationId == org).ExecuteDeleteAsync(cancellationToken);
    }
}

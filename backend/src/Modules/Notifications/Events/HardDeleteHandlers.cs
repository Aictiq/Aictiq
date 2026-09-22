using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Events;
using Aictiq.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Aictiq.Modules.Notifications.Events;

/// <summary>An inbox entry about a deleted item would open onto a 404, so it goes with the item.</summary>
public sealed class NotificationsWorkItemsDeletedHandler(NotificationsDbContext db, AmbientCurrentTenant tenant)
    : IDomainEventHandler<WorkItemsDeleted>
{
    public async Task HandleAsync(WorkItemsDeleted @event, CancellationToken cancellationToken)
    {
        using var scope = tenant.Use(@event.OrganizationId);
        var ids = @event.ItemIds.ToArray();
        await db.Notifications.Where(x => x.ItemId != null && ids.Contains(x.ItemId.Value)).ExecuteDeleteAsync(cancellationToken);
    }
}

public sealed class NotificationsProjectDeletedHandler(NotificationsDbContext db, AmbientCurrentTenant tenant)
    : IDomainEventHandler<ProjectDeleted>
{
    public async Task HandleAsync(ProjectDeleted @event, CancellationToken cancellationToken)
    {
        using var scope = tenant.Use(@event.OrganizationId);
        await db.Notifications.Where(x => x.ProjectId == @event.ProjectId).ExecuteDeleteAsync(cancellationToken);
    }
}

/// <summary>
/// Every notification of a deleted organization, and the per-person settings of the agent
/// accounts deleted with it. A person's own preferences are theirs, not the organization's.
/// </summary>
public sealed class NotificationsOrganizationDeletedHandler(NotificationsDbContext db, AmbientCurrentTenant tenant)
    : IDomainEventHandler<OrganizationDeleted>
{
    public async Task HandleAsync(OrganizationDeleted @event, CancellationToken cancellationToken)
    {
        using (tenant.Use(@event.OrganizationId))
            await db.Notifications.Where(x => x.OrganizationId == @event.OrganizationId).ExecuteDeleteAsync(cancellationToken);

        var agents = @event.AgentIds.ToArray();
        if (agents.Length == 0) return;
        await db.Preferences.Where(x => agents.Contains(x.UserId)).ExecuteDeleteAsync(cancellationToken);
        await db.Digests.Where(x => agents.Contains(x.UserId)).ExecuteDeleteAsync(cancellationToken);
        await db.Presence.Where(x => agents.Contains(x.UserId)).ExecuteDeleteAsync(cancellationToken);
    }
}

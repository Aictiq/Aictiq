using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Events;
using Aictiq.SharedKernel.Persistence;
using Aictiq.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Aictiq.Modules.Integrations.Events;

/// <summary>
/// A deleted project's repository bindings and webhook subscriptions, with the subscriptions'
/// delivery log (payloads carry the project's item titles) and the audit trail of both.
/// Organization-wide subscriptions stay: they belong to the organization, not the project.
/// </summary>
public sealed class IntegrationsProjectDeletedHandler(IntegrationsDbContext db, ICurrentTenant currentTenant)
    : IDomainEventHandler<ProjectDeleted>
{
    public async Task HandleAsync(ProjectDeleted @event, CancellationToken cancellationToken)
    {
        using var tenant = currentTenant is AmbientCurrentTenant ambient ? ambient.Use(@event.OrganizationId) : null;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var subscriptionIds = await db.WebhookSubscriptions.Where(x => x.ProjectId == @event.ProjectId).Select(x => x.Id).ToListAsync(cancellationToken);
        var bindingIds = await db.RepoBindings.Where(x => x.ProjectId == @event.ProjectId).Select(x => x.Id).ToListAsync(cancellationToken);
        await db.WebhookDeliveries.Where(x => subscriptionIds.Contains(x.SubscriptionId)).ExecuteDeleteAsync(cancellationToken);
        await db.WebhookSubscriptions.Where(x => subscriptionIds.Contains(x.Id)).ExecuteDeleteAsync(cancellationToken);
        await db.RepoBindings.Where(x => bindingIds.Contains(x.Id)).ExecuteDeleteAsync(cancellationToken);
        await AuditPurge.EntitiesAsync(db, @event.OrganizationId, AuditEntityTypes.WebhookSubscription, subscriptionIds, cancellationToken);
        await AuditPurge.EntitiesAsync(db, @event.OrganizationId, AuditEntityTypes.RepoBinding, bindingIds, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}

/// <summary>
/// Every integration row of a deleted organization. The GitHub App stays installed on the
/// GitHub side — as with disconnecting, uninstalling it there is the account owner's call —
/// but Aictiq forgets the installation, so its webhooks resolve to no organization.
/// </summary>
public sealed class IntegrationsOrganizationDeletedHandler(IntegrationsDbContext db, ICurrentTenant currentTenant)
    : IDomainEventHandler<OrganizationDeleted>
{
    public async Task HandleAsync(OrganizationDeleted @event, CancellationToken cancellationToken)
    {
        using var tenant = currentTenant is AmbientCurrentTenant ambient ? ambient.Use(@event.OrganizationId) : null;
        var org = @event.OrganizationId;
        await db.WebhookDeliveries.Where(x => x.OrganizationId == org).ExecuteDeleteAsync(cancellationToken);
        await db.WebhookSubscriptions.Where(x => x.OrganizationId == org).ExecuteDeleteAsync(cancellationToken);
        await db.RepoBindings.Where(x => x.OrganizationId == org).ExecuteDeleteAsync(cancellationToken);
        await db.GitHubDeliveries.Where(x => x.OrganizationId == org).ExecuteDeleteAsync(cancellationToken);
        await db.GitHubInstallations.Where(x => x.OrganizationId == org).ExecuteDeleteAsync(cancellationToken);
    }
}

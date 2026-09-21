using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Events;

namespace Aictiq.Modules.Tenancy.Events;

/// <summary>
/// Applies what Billing says an organization is entitled to onto <c>organizations.plan</c>,
/// which Tenancy owns and every limit check reads.
///
/// The event is a nudge, not a value: the plan comes from
/// <see cref="IOrganizationBillingState.GetEntitledPlanAsync"/> at the moment of handling.
/// That is what makes replay and reordering harmless — every delivery converges on the
/// newest state, and a late one cannot restore a plan the organization has since left.
/// </summary>
/// <remarks>
/// Two handlers for one organization running at once (two Workers instances) could still
/// interleave read-Billing and write-Tenancy so that the older answer lands last. The
/// transaction-scoped advisory lock orders them: whoever writes last also read last.
/// The write itself is a tracked update rather than a bulk one so it reaches the audit log —
/// "who moved this organization to Team" is a question support will be asked.
/// </remarks>
public sealed class OrganizationPlanHandler(
    TenancyDbContext db,
    IOrganizationBillingState billing,
    TimeProvider clock,
    ILogger<OrganizationPlanHandler> logger) : IDomainEventHandler<OrganizationBillingChanged>
{
    public async Task HandleAsync(OrganizationBillingChanged domainEvent, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var lockKey = $"tenancy.plan:{domainEvent.OrganizationId}";
        await db.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))", cancellationToken);

        var plan = await billing.GetEntitledPlanAsync(domainEvent.OrganizationId, cancellationToken);
        if (plan is null)
        {
            return;
        }

        // Organizations are not tenant rows (they are what a tenant is), so no scope is
        // needed to find one; a deleted organization keeps whatever plan it had.
        var organization = await db.Organizations.SingleOrDefaultAsync(
            o => o.Id == domainEvent.OrganizationId, cancellationToken);
        if (organization is null || organization.Plan == plan)
        {
            return;
        }

        var previous = organization.Plan;
        organization.ChangePlan(plan, clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Organization {OrganizationId} moved from plan {Previous} to {Plan}",
            domainEvent.OrganizationId, previous, plan);
    }
}

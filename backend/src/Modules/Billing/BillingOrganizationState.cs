using Aictiq.Modules.Billing.Domain;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Aictiq.Modules.Billing;

/// <summary>
/// Billing's answer to <see cref="IOrganizationBillingState"/>. A self-hosted instance
/// answers without a query - this sits on every write path through
/// <c>RequireProjectWritable</c>, and an instance that never bills should not pay for asking.
///
/// The answer is read-only in two situations: a payment failed and its grace
/// period ran out, or a hosted organization's evaluation ended without a checkout. Reads,
/// downloads and exports survive both; an explicit checkout ends the second.
/// </summary>
public sealed class BillingOrganizationState(
    BillingDbContext db, AmbientCurrentTenant tenant, IOptions<BillingOptions> options, TimeProvider clock)
    : IOrganizationBillingState
{
    public async Task<bool> IsReadOnlyAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        if (!options.Value.IsSaas) return false;

        // Callers include Workers, which have no tenant of their own; the scope is restored
        // on the way out, so a request's tenant is left exactly as it was.
        using var scope = tenant.Use(organizationId);
        var subscription = await db.Subscriptions.AsNoTracking()
            .Select(x => new { x.GraceEndsAt, x.Status })
            .SingleOrDefaultAsync(cancellationToken);
        if (subscription is { } row)
        {
            // A subscription - live, lapsed, or cancelled back to Free - is the whole
            // answer. An organization that has ever paid is never sent back to its
            // evaluation: cancelling is a downgrade, not an expiry, and it must keep
            // behaving exactly as it did before evaluations existed.
            return row.GraceEndsAt is { } ends && ends <= clock.GetUtcNow() && row.Status.IsBilling();
        }

        // Never subscribed. An organization born with an evaluation keeps writing only
        // while that evaluation is alive; one created before evaluations existed - no row,
        // no entitlement - is untouched by this.
        var evaluationEndsAt = await db.Evaluations.AsNoTracking()
            .Select(x => (DateTimeOffset?)x.EndsAt)
            .SingleOrDefaultAsync(cancellationToken);
        return evaluationEndsAt is { } expiry && expiry <= clock.GetUtcNow();
    }

    public async Task<string?> GetEntitledPlanAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        if (!options.Value.IsSaas) return null;

        using var scope = tenant.Use(organizationId);
        var subscription = await db.Subscriptions.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (subscription is { Status: not SubscriptionStatus.None })
        {
            return subscription.EntitledPlan;
        }

        // The evaluation grants Hosted for as long as the organization has never carried a
        // real subscription - after expiry too. Expiry takes the writes away (above); it
        // does not silently shrink the allowances a team planned around, and nobody was
        // ever charged for the difference.
        var evaluated = await db.Evaluations.AsNoTracking().AnyAsync(cancellationToken);
        return evaluated ? PlanCodes.Hosted : subscription?.EntitledPlan;
    }
}

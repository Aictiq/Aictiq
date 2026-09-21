using Aictiq.Modules.Billing.Domain;
using Aictiq.Modules.Billing.Payments;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Events;
using Aictiq.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Aictiq.Modules.Billing.Events;

/// <summary>
/// A deleted organization stops paying the moment it stops existing: a live subscription is
/// cancelled at Stripe <em>before</em> the rows that remember it go, so a failed call retries
/// against rows that still name the subscription. Stripe's later webhooks about it resolve to
/// no organization and change nothing.
/// </summary>
public sealed class BillingOrganizationDeletedHandler(BillingDbContext db, AmbientCurrentTenant tenant, IStripeGateway stripe)
    : IDomainEventHandler<OrganizationDeleted>
{
    public async Task HandleAsync(OrganizationDeleted @event, CancellationToken cancellationToken)
    {
        using var scope = tenant.Use(@event.OrganizationId);
        var org = @event.OrganizationId;

        var live = await db.Subscriptions.AsNoTracking()
            .Where(x => x.OrganizationId == org && x.StripeSubscriptionId != null
                && x.Status != SubscriptionStatus.Canceled && x.Status != SubscriptionStatus.IncompleteExpired)
            .Select(x => x.StripeSubscriptionId!).ToListAsync(cancellationToken);
        foreach (var subscriptionId in live)
            await stripe.CancelNowAsync(subscriptionId, cancellationToken);

        await db.Subscriptions.Where(x => x.OrganizationId == org).ExecuteDeleteAsync(cancellationToken);
        await db.UsageSnapshots.Where(x => x.OrganizationId == org).ExecuteDeleteAsync(cancellationToken);
        await db.StripeEvents.Where(x => x.OrganizationId == org).ExecuteDeleteAsync(cancellationToken);
        await db.Evaluations.Where(x => x.OrganizationId == org).ExecuteDeleteAsync(cancellationToken);
    }
}

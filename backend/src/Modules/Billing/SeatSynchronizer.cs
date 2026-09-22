using Aictiq.Modules.Billing.Domain;
using Aictiq.Modules.Billing.Payments;
using Aictiq.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aictiq.Modules.Billing;

public enum SeatSyncOutcome
{
    /// <summary>Billing is off, or the organization has no subscription Stripe is charging.</summary>
    NotBilled,
    Unchanged,
    Updated,
}

/// <summary>
/// Makes Stripe's seat quantities match the member list. Run after every
/// membership change (off the outbox) and nightly for every subscription.
///
/// It never applies a delta. It counts the members as they are now, compares with what
/// Stripe was last told, and sends absolute quantities - so a redelivered event, a nightly
/// run racing a membership change, or two changes arriving out of order all converge on the
/// same, correct answer. That, rather than a de-duplication key, is what makes the outbox
/// handler idempotent: there is no effect that happening twice could double.
///
/// There is a second kind of subscription it must leave entirely alone: the
/// flat hosted plan charges once for the organization, whatever the membership does. For
/// those the synchronizer's only business is the founding offer's end - switching the
/// price when its discounted periods have all been collected, if the webhook could not.
/// </summary>
/// <remarks>
/// Two syncs for one organization must not interleave (count 5, count 6, send 6, send 5),
/// so each holds a transaction-scoped advisory lock for its organization from the count to
/// the write. The lock is held across the Stripe call on purpose; it is per organization,
/// and the alternative is a quantity that is wrong until the next nightly run.
/// </remarks>
public sealed class SeatSynchronizer(
    BillingDbContext db,
    AmbientCurrentTenant tenant,
    BillingUsage usage,
    BillingAvailability availability,
    IStripeGateway stripe,
    IOptions<StripeOptions> stripeOptions,
    TimeProvider clock,
    ILogger<SeatSynchronizer> logger)
{
    public async Task<SeatSyncOutcome> SyncAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        if (!availability.IsEnabled) return SeatSyncOutcome.NotBilled;

        using var scope = tenant.Use(organizationId);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var lockKey = $"billing.seats:{organizationId}";
        await db.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))", cancellationToken);

        var subscription = await db.Subscriptions.SingleOrDefaultAsync(cancellationToken);
        if (subscription is not { StripeSubscriptionId: { } subscriptionId } || !subscription.Status.IsBilling())
        {
            return SeatSyncOutcome.NotBilled;
        }

        var plan = await BillingPlans.FindAsync(db, subscription.Plan, cancellationToken);
        if (BillingUsage.IsFlat(plan))
        {
            return await ConvertFoundingIfDueAsync(subscription, subscriptionId, transaction, cancellationToken);
        }

        var counted = await usage.GetAsync(organizationId, includeStorage: false, cancellationToken);
        if (counted is null) return SeatSyncOutcome.NotBilled;

        var seats = BillingUsage.Seats(counted.Humans, counted.Agents, plan);
        if (seats.Humans == subscription.SeatsHuman && seats.Agents == subscription.SeatsAgent)
        {
            return SeatSyncOutcome.Unchanged;
        }

        var lines = BillingUsage.Lines(stripeOptions.Value, subscription.Plan, seats)
            ?? throw new InvalidOperationException(
                $"Stripe prices for plan '{subscription.Plan}' are not configured (Stripe:Prices:{subscription.Plan}_human / _agent).");

        await stripe.UpdateSubscriptionAsync(subscriptionId, subscription.Plan, lines, cancellationToken);

        var now = clock.GetUtcNow();
        logger.LogInformation(
            "Seats for organization {OrganizationId} synced to Stripe: {Humans} human(s) (was {PreviousHumans}), {Agents} billed agent(s) (was {PreviousAgents})",
            organizationId, seats.Humans, subscription.SeatsHuman, seats.Agents, subscription.SeatsAgent);
        subscription.SeatsHuman = seats.Humans;
        subscription.SeatsAgent = seats.Agents;
        subscription.SeatsSyncedAt = now;
        subscription.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return SeatSyncOutcome.Updated;
    }

    /// <summary>
    /// The flat subscription's one possible change: the founding offer has run its counted
    /// periods and the Stripe subscription still charges the discounted price. Idempotent
    /// the same way the seat sync is - <see cref="Subscription.FoundingConvertedAt"/> is
    /// written only after the gateway call succeeds, so a crash between the two retries
    /// the same absolute switch instead of inventing a second one.
    /// </summary>
    private async Task<SeatSyncOutcome> ConvertFoundingIfDueAsync(
        Subscription subscription, string subscriptionId, IDbContextTransaction transaction,
        CancellationToken cancellationToken)
    {
        if (!subscription.FoundingConversionDue) return SeatSyncOutcome.Unchanged;

        var lines = BillingUsage.FlatLines(stripeOptions.Value, subscription.Plan)
            ?? throw new InvalidOperationException(
                $"Stripe organization price for plan '{subscription.Plan}' is not configured (Stripe:Prices:{subscription.Plan}_organization).");
        await stripe.UpdateSubscriptionAsync(subscriptionId, subscription.Plan, lines, cancellationToken, proration: "none");

        var now = clock.GetUtcNow();
        subscription.FoundingConvertedAt = now;
        subscription.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation(
            "Founding offer for organization {OrganizationId} ended; the subscription now charges the plan price",
            subscription.OrganizationId);
        return SeatSyncOutcome.Updated;
    }

    /// <summary>
    /// Every organization with a subscription Stripe is charging - the nightly run's work
    /// list. Crosses every tenant by nature, so it is one of the deliberate, predicate-bound
    /// uses of IgnoreQueryFilters: it returns ids, and each is then synced inside its own
    /// tenant scope.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> ListBilledOrganizationsAsync(CancellationToken cancellationToken)
    {
        var billing = new[] { SubscriptionStatus.Active, SubscriptionStatus.Trialing, SubscriptionStatus.PastDue, SubscriptionStatus.Unpaid };
        return await db.Subscriptions.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.StripeSubscriptionId != null && billing.Contains(x.Status))
            .OrderBy(x => x.OrganizationId)
            .Select(x => x.OrganizationId)
            .ToListAsync(cancellationToken);
    }
}

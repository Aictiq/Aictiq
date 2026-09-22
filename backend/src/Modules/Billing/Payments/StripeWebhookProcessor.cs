using Aictiq.Modules.Billing.Domain;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace Aictiq.Modules.Billing.Payments;

public enum StripeWebhookOutcome
{
    /// <summary>Bad or missing signature, stale timestamp, or not an event at all. 400.</summary>
    Rejected,

    /// <summary>This event id was processed before. 200, and nothing happened.</summary>
    Duplicate,

    /// <summary>Recorded and applied.</summary>
    Applied,

    /// <summary>Recorded, but about nothing this instance acts on - or older than what it knows.</summary>
    Ignored,
}

/// <summary>
/// Turns a Stripe webhook into a change to <see cref="Subscription"/>.
///
/// Three rules carry the weight:
/// <list type="number">
/// <item><b>Nothing is trusted before the signature.</b> Stripe's own
/// <c>EventUtility.ValidateSignature</c> checks the HMAC and the timestamp tolerance before
/// a byte is parsed or a row is read, so an anonymous caller can cost this endpoint one
/// hash and nothing else.</item>
/// <item><b>An event happens once.</b> Its id is inserted into <c>billing.stripe_events</c>
/// with <c>ON CONFLICT DO NOTHING</c> in the same transaction as its effect. A redelivery
/// inserts nothing and changes nothing; two deliveries racing serialize on the primary key
/// and exactly one of them applies. The constraint is the guarantee, not a lookup.</item>
/// <item><b>An old event never overwrites a newer one.</b> Stripe does not promise order, so
/// each state-setting event is compared with <see cref="Subscription.LastEventAt"/> (Stripe's
/// own <c>created</c>), a cancelled subscription stays cancelled, and news about a
/// subscription the organization has since replaced is dropped. The row's version makes the
/// read-compare-write a compare-and-swap: a concurrent writer turns this one into a 409,
/// the transaction rolls back with its ledger row, and Stripe's retry sees the new state.</item>
/// </list>
/// </summary>
public sealed class StripeWebhookProcessor(
    BillingDbContext db,
    AmbientCurrentTenant tenant,
    IOrganizationLookup organizations,
    IStripeGateway gateway,
    IOptions<StripeOptions> stripe,
    IOptions<BillingOptions> billing,
    TimeProvider clock,
    ILogger<StripeWebhookProcessor> logger)
{
    public async Task<StripeWebhookOutcome> ProcessAsync(string payload, string? signature, CancellationToken cancellationToken)
    {
        var options = stripe.Value;
        if (string.IsNullOrWhiteSpace(signature) || string.IsNullOrWhiteSpace(options.WebhookSecret))
        {
            return StripeWebhookOutcome.Rejected;
        }

        try
        {
            global::Stripe.EventUtility.ValidateSignature(payload, signature, options.WebhookSecret, options.WebhookToleranceSeconds);
        }
        catch (global::Stripe.StripeException)
        {
            logger.LogWarning("Refused a Stripe webhook whose signature did not verify");
            return StripeWebhookOutcome.Rejected;
        }

        if (StripeWebhookEvent.TryParse(payload) is not { } stripeEvent)
        {
            return StripeWebhookOutcome.Rejected;
        }

        var organizationId = await ResolveOrganizationAsync(stripeEvent, cancellationToken);
        var now = clock.GetUtcNow();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var recorded = await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO billing.stripe_events (id, type, created_at, received_at, organization_id)
            VALUES (@id, @type, @created, @received, @organization)
            ON CONFLICT (id) DO NOTHING
            """,
            [
                new NpgsqlParameter("id", stripeEvent.Id),
                new NpgsqlParameter("type", stripeEvent.Type.Length > 100 ? stripeEvent.Type[..100] : stripeEvent.Type),
                new NpgsqlParameter("created", stripeEvent.Created),
                new NpgsqlParameter("received", now),
                new NpgsqlParameter("organization", NpgsqlDbType.Uuid) { Value = (object?)organizationId ?? DBNull.Value },
            ],
            cancellationToken);
        if (recorded == 0)
        {
            logger.LogInformation("Stripe event {EventId} ({Type}) was already processed", stripeEvent.Id, stripeEvent.Type);
            return StripeWebhookOutcome.Duplicate;
        }

        var applied = false;
        if (organizationId is { } organization)
        {
            using var scope = tenant.Use(organization);
            applied = await ApplyAsync(stripeEvent, now, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation("Stripe event {EventId} ({Type}) for organization {OrganizationId}: {Outcome}",
            stripeEvent.Id, stripeEvent.Type, organizationId, applied ? "applied" : "ignored");
        return applied ? StripeWebhookOutcome.Applied : StripeWebhookOutcome.Ignored;
    }

    private Task<bool> ApplyAsync(StripeWebhookEvent stripeEvent, DateTimeOffset now, CancellationToken cancellationToken) =>
        stripeEvent.Type switch
        {
            "checkout.session.completed" => CheckoutCompletedAsync(stripeEvent, now, cancellationToken),
            // .created is not in the ticket's list, but it is the same object in the same
            // shape, and handling it closes the gap in which a subscription exists and the
            // checkout event has not arrived yet.
            "customer.subscription.created" or "customer.subscription.updated" or "customer.subscription.deleted"
                => SubscriptionChangedAsync(stripeEvent, now, cancellationToken),
            "invoice.payment_failed" => PaymentFailedAsync(stripeEvent, now, cancellationToken),
            // One collected payment of the founding offer. invoice.paid is
            // deliberately not counted: Stripe sends both for the same invoice, and the
            // ledger's event ids would not notice the difference.
            "invoice.payment_succeeded" => FoundingInvoicePaidAsync(stripeEvent, now, cancellationToken),
            _ => Task.FromResult(false),
        };

    private async Task<bool> CheckoutCompletedAsync(StripeWebhookEvent stripeEvent, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (stripeEvent.Get("mode") != "subscription"
            || stripeEvent.Reference("customer") is not { } customerId
            || stripeEvent.Reference("subscription") is not { } subscriptionId)
        {
            return false;
        }

        var subscription = await db.Subscriptions.SingleOrDefaultAsync(cancellationToken);
        var plan = await KnownPlanAsync(stripeEvent.Metadata("plan"), cancellationToken) ?? subscription?.Plan;
        if (plan is null)
        {
            logger.LogWarning("Checkout {SessionId} completed without a plan Aictiq knows; ignoring it", stripeEvent.Get("id"));
            return false;
        }

        if (subscription is null)
        {
            subscription = NewSubscription(plan, now);
        }
        else if (subscription.StripeSubscriptionId == subscriptionId
                 && subscription.LastEventAt is { } last && last > stripeEvent.Created)
        {
            // A customer.subscription.* event about this very subscription already arrived
            // and is newer. It knows the status better than the checkout does; only fill in
            // what it could not have told us.
            subscription.StripeCustomerId ??= customerId;
            return true;
        }

        var before = Snapshot(subscription);
        var isNew = subscription.StripeSubscriptionId != subscriptionId;
        if (isNew && subscription.Status.IsBilling())
        {
            logger.LogWarning("Organization {OrganizationId} completed a checkout for {New} while {Old} was still billing; the new subscription replaces it",
                subscription.OrganizationId, subscriptionId, subscription.StripeSubscriptionId);
        }

        subscription.StripeCustomerId = customerId;
        subscription.StripeSubscriptionId = subscriptionId;
        subscription.Plan = plan;
        subscription.Status = stripeEvent.Get("payment_status") is "paid" or "no_payment_required"
            ? SubscriptionStatus.Active
            : SubscriptionStatus.Incomplete;
        subscription.CancelAtPeriodEnd = false;
        if (isNew)
        {
            // A fresh subscription starts with a clean slate: no grace period inherited from
            // the one it replaces, and no seat count until Stripe reports its items. The
            // founding offer is not part of the clean slate - it was granted once, at the
            // first checkout, and a re-subscription does not earn a second one.
            ClearGrace(subscription);
            subscription.SeatsHuman = 0;
            subscription.SeatsAgent = 0;
        }
        if (stripeEvent.Metadata("founding") == "true" && billing.Value.FoundingPrice is { } foundingPrice)
        {
            subscription.FoundingPrice ??= foundingPrice;
            subscription.FoundingPeriods ??= billing.Value.FoundingPeriods;
        }
        subscription.LastEventAt = stripeEvent.Created;
        subscription.UpdatedAt = now;
        AnnounceIfChanged(subscription, before);
        return true;
    }

    private async Task<bool> SubscriptionChangedAsync(StripeWebhookEvent stripeEvent, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (stripeEvent.Get("id") is not { } subscriptionId) return false;
        var status = stripeEvent.Type == "customer.subscription.deleted"
            ? SubscriptionStatus.Canceled
            : SubscriptionStatuses.Parse(stripeEvent.Get("status"));

        var options = stripe.Value;
        var items = stripeEvent.SubscriptionItems()
            .Select(item => (Price: options.Resolve(item.PriceId), item.Quantity))
            .Where(item => item.Price is not null)
            .ToList();
        var plan = await KnownPlanAsync(
            items.Where(i => i.Price!.Value.Kind == "human").Select(i => i.Price!.Value.Plan).FirstOrDefault()
                ?? stripeEvent.Metadata("plan"),
            cancellationToken);

        var subscription = await db.Subscriptions.SingleOrDefaultAsync(cancellationToken);
        if (subscription is null)
        {
            if (plan is null) return false;
            subscription = NewSubscription(plan, now);
        }
        else if (subscription.StripeSubscriptionId == subscriptionId)
        {
            if (subscription.LastEventAt is { } last && last > stripeEvent.Created)
            {
                return false; // Older than what this row already reflects.
            }
            if (subscription.Status == SubscriptionStatus.Canceled && status != SubscriptionStatus.Canceled)
            {
                return false; // Cancelled is terminal in Stripe; a late "active" cannot revive it.
            }
        }
        else if (subscription.StripeSubscriptionId is not null && subscription.Status.IsBilling() && !status.IsBilling())
        {
            return false; // News about a subscription this organization has since replaced.
        }

        var before = Snapshot(subscription);
        subscription.StripeSubscriptionId = subscriptionId;
        subscription.StripeCustomerId = stripeEvent.Reference("customer") ?? subscription.StripeCustomerId;
        subscription.Status = status;
        if (plan is not null) subscription.Plan = plan;
        (subscription.CurrentPeriodStart, subscription.CurrentPeriodEnd) = stripeEvent.Period();
        subscription.CancelAtPeriodEnd = status != SubscriptionStatus.Canceled && stripeEvent.Bool("cancel_at_period_end");
        if (items.Count > 0)
        {
            subscription.SeatsHuman = (int)Math.Min(int.MaxValue, items.Where(i => i.Price!.Value.Kind == "human").Sum(i => i.Quantity));
            subscription.SeatsAgent = (int)Math.Min(int.MaxValue, items.Where(i => i.Price!.Value.Kind == "agent").Sum(i => i.Quantity));
        }

        if (!status.IsBilling() || (status.IsInGoodStanding() && subscription.PaymentFailedAt <= stripeEvent.Created))
        {
            // Paid again, or no longer charging at all: either way the grace period is over
            // and nothing about it should make the organization read-only.
            ClearGrace(subscription);
        }
        else if ((status is SubscriptionStatus.PastDue or SubscriptionStatus.Unpaid) && subscription.PaymentFailedAt is null)
        {
            // invoice.payment_failed is what normally starts the clock; this is the backstop
            // for an endpoint that was not subscribed to it.
            StartGrace(subscription, stripeEvent.Created);
        }

        subscription.LastEventAt = stripeEvent.Created;
        subscription.UpdatedAt = now;
        AnnounceIfChanged(subscription, before);
        return true;
    }

    private async Task<bool> PaymentFailedAsync(StripeWebhookEvent stripeEvent, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var subscription = await db.Subscriptions.SingleOrDefaultAsync(cancellationToken);
        if (subscription is not { StripeSubscriptionId: { } current } || !subscription.Status.IsBilling())
        {
            return false; // Nothing is being charged, so there is nothing to lapse.
        }
        if (stripeEvent.InvoiceSubscription() is { } invoiced && invoiced != current)
        {
            return false; // An invoice for a subscription the organization has since replaced.
        }
        if (subscription.PaymentFailedAt is not null)
        {
            return false; // Stripe retrying and failing again does not restart the clock.
        }
        if (subscription.LastEventAt is { } last && last > stripeEvent.Created && subscription.Status.IsInGoodStanding())
        {
            return false; // A newer event already shows the account recovered.
        }

        StartGrace(subscription, stripeEvent.Created);
        subscription.UpdatedAt = now;
        logger.LogWarning("Payment failed for organization {OrganizationId}; read-only from {GraceEndsAt} unless it is fixed",
            subscription.OrganizationId, subscription.GraceEndsAt);
        return true;
    }

    /// <summary>
    /// One discounted founding period has been paid. The count is the only thing
    /// this event changes: the entitlement, the plan, the price on the row - all stay. On
    /// the last discounted period the Stripe subscription is switched to the plan's own
    /// price right away, so the invoice after the offer never charges the founding price;
    /// if that switch cannot be made now, the nightly reconciliation retries it
    /// (<see cref="Subscription.FoundingConversionDue"/>) and the worst case is one extra
    /// discounted invoice, never a lost entitlement.
    /// </summary>
    private async Task<bool> FoundingInvoicePaidAsync(StripeWebhookEvent stripeEvent, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var subscription = await db.Subscriptions.SingleOrDefaultAsync(cancellationToken);
        if (subscription is not { StripeSubscriptionId: { } current }
            || stripeEvent.InvoiceSubscription() is { } invoiced && invoiced != current)
        {
            return false; // Not our subscription, or nothing is being charged at all.
        }
        if (subscription.FoundingPrice is null || subscription.FoundingConvertedAt is not null)
        {
            return false; // The offer was never granted, or it has already run its course.
        }
        var periods = subscription.FoundingPeriods ?? billing.Value.FoundingPeriods;
        if (subscription.FoundingPeriodsBilled >= periods)
        {
            return false; // Over-counted protection; the conversion check below is the real door.
        }

        subscription.FoundingPeriodsBilled += 1;
        subscription.UpdatedAt = now;
        logger.LogInformation(
            "Founding offer for organization {OrganizationId}: {Billed} of {Periods} discounted period(s) collected",
            subscription.OrganizationId, subscription.FoundingPeriodsBilled, periods);

        if (subscription.FoundingPeriodsBilled >= periods)
        {
            await ConvertFoundingAsync(subscription, cancellationToken);
        }
        return true;
    }

    /// <summary>
    /// Switches the Stripe subscription to the plan's own price. Best effort inside the
    /// webhook: the counted period is kept either way, and <c>FoundingConvertedAt</c> is
    /// written only after a successful call, so the nightly pass converges on the same
    /// outcome. The change takes effect at the period boundary - no proration - because the
    /// offer's periods are whole months.
    /// </summary>
    private async Task ConvertFoundingAsync(Subscription subscription, CancellationToken cancellationToken)
    {
        try
        {
            var lines = BillingUsage.FlatLines(stripe.Value, subscription.Plan)
                ?? throw new InvalidOperationException(
                    $"Stripe organization price for plan '{subscription.Plan}' is not configured (Stripe:Prices:{subscription.Plan}_organization).");
            await gateway.UpdateSubscriptionAsync(subscription.StripeSubscriptionId!, subscription.Plan, lines,
                cancellationToken, proration: "none");
            subscription.FoundingConvertedAt = clock.GetUtcNow();
            logger.LogInformation(
                "Founding offer for organization {OrganizationId} ended; the subscription now charges the plan price",
                subscription.OrganizationId);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception,
                "Could not switch organization {OrganizationId} to the standard plan price after its founding offer; the nightly reconciliation will retry",
                subscription.OrganizationId);
        }
    }

    /// <summary>
    /// Which organization an event is about. Stripe's ids are the tenant here, the way an
    /// invitation token is: the webhook carries no slug and no principal. Each lookup across
    /// tenants is bound by a unique Stripe id; the metadata fallback names an organization
    /// Aictiq itself wrote into the checkout, and Stripe signed it.
    /// </summary>
    private async Task<Guid?> ResolveOrganizationAsync(StripeWebhookEvent stripeEvent, CancellationToken cancellationToken)
    {
        string? subscriptionId = null;
        Guid? claimed = null;
        switch (stripeEvent.Type)
        {
            case "checkout.session.completed":
                claimed = ParseGuid(stripeEvent.Get("client_reference_id")) ?? ParseGuid(stripeEvent.Metadata("organizationId"));
                subscriptionId = stripeEvent.Reference("subscription");
                break;
            case "customer.subscription.created" or "customer.subscription.updated" or "customer.subscription.deleted":
                subscriptionId = stripeEvent.Get("id");
                claimed = ParseGuid(stripeEvent.Metadata("organizationId"));
                break;
            case "invoice.payment_failed":
                subscriptionId = stripeEvent.InvoiceSubscription();
                break;
            case "invoice.payment_succeeded":
                subscriptionId = stripeEvent.InvoiceSubscription();
                break;
            default:
                return null;
        }

        var customerId = stripeEvent.Reference("customer");
        var known = subscriptionId is null ? null : await db.Subscriptions.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.StripeSubscriptionId == subscriptionId).Select(x => (Guid?)x.OrganizationId)
            .SingleOrDefaultAsync(cancellationToken);
        known ??= customerId is null ? null : await db.Subscriptions.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.StripeCustomerId == customerId).Select(x => (Guid?)x.OrganizationId)
            .SingleOrDefaultAsync(cancellationToken);
        if (known is not null) return known;

        return claimed is { } id && await organizations.FindByIdAsync(id, cancellationToken) is not null ? id : null;
    }

    private async Task<string?> KnownPlanAsync(string? code, CancellationToken cancellationToken) =>
        code is { Length: > 0 } && code != PlanCodes.SelfHosted
            && await db.Plans.AnyAsync(p => p.Code == code, cancellationToken)
            ? code
            : null;

    /// <summary>
    /// The organization is set here rather than left to SaveChanges' tenant stamping: the
    /// row announces itself (<see cref="Subscription.AnnounceChange"/>) before it is saved,
    /// and an event raised with an empty id would tell Tenancy about no organization at all.
    /// </summary>
    private Subscription NewSubscription(string plan, DateTimeOffset now)
    {
        var subscription = new Subscription
        {
            OrganizationId = tenant.OrganizationId ?? throw new InvalidOperationException("No organization in scope."),
            Plan = plan,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Subscriptions.Add(subscription);
        return subscription;
    }

    private void StartGrace(Subscription subscription, DateTimeOffset failedAt)
    {
        subscription.PaymentFailedAt = failedAt;
        subscription.GraceEndsAt = failedAt.AddDays(billing.Value.GracePeriodDays);
    }

    private static void ClearGrace(Subscription subscription)
    {
        subscription.PaymentFailedAt = null;
        subscription.GraceEndsAt = null;
    }

    private static (string? Plan, SubscriptionStatus Status) Snapshot(Subscription subscription) =>
        (subscription.EntitledPlan, subscription.Status);

    /// <summary>Only a change in what the organization is entitled to is news for Tenancy.</summary>
    private static void AnnounceIfChanged(Subscription subscription, (string? Plan, SubscriptionStatus Status) before)
    {
        if (Snapshot(subscription) != before) subscription.AnnounceChange();
    }

    private static Guid? ParseGuid(string? value) => Guid.TryParse(value, out var id) ? id : null;
}

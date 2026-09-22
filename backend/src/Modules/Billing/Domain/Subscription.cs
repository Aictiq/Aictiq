using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Domain;

namespace Aictiq.Modules.Billing.Domain;

/// <summary>
/// Stripe's subscription statuses, plus <see cref="None"/> for an organization that has a
/// row but never completed a checkout. Stored as a number like every other enum here; on
/// the wire it is the camelCase name (<c>pastDue</c>).
/// </summary>
public enum SubscriptionStatus : short
{
    None = 0,
    Incomplete = 1,
    IncompleteExpired = 2,
    Trialing = 3,
    Active = 4,
    PastDue = 5,
    Canceled = 6,
    Unpaid = 7,
    Paused = 8,
}

public static class SubscriptionStatuses
{
    public static SubscriptionStatus Parse(string? value) => value switch
    {
        "incomplete" => SubscriptionStatus.Incomplete,
        "incomplete_expired" => SubscriptionStatus.IncompleteExpired,
        "trialing" => SubscriptionStatus.Trialing,
        "active" => SubscriptionStatus.Active,
        "past_due" => SubscriptionStatus.PastDue,
        "canceled" => SubscriptionStatus.Canceled,
        "unpaid" => SubscriptionStatus.Unpaid,
        "paused" => SubscriptionStatus.Paused,
        _ => SubscriptionStatus.None,
    };

    /// <summary>
    /// A subscription Stripe is still charging, and whose seat quantities therefore matter.
    /// Past due and unpaid are included: the organization still holds its plan while Stripe
    /// retries, and the grace period - not the status - is what takes writes away.
    /// </summary>
    public static bool IsBilling(this SubscriptionStatus status) =>
        status is SubscriptionStatus.Active or SubscriptionStatus.Trialing
            or SubscriptionStatus.PastDue or SubscriptionStatus.Unpaid;

    /// <summary>Healthy: a payment problem, if there was one, is over.</summary>
    public static bool IsInGoodStanding(this SubscriptionStatus status) =>
        status is SubscriptionStatus.Active or SubscriptionStatus.Trialing;
}

/// <summary>
/// One organization's paid account: the Stripe customer, its current subscription, and
/// what Aictiq last told Stripe to charge for. At most one per organization
/// (<c>ux_subscriptions_organization_id</c>); a re-subscription after a cancellation
/// replaces the Stripe ids on the same row rather than adding another.
///
/// Stripe is the billing truth. This row is Aictiq's copy, kept current by webhooks, and it
/// is written only under two guards: the Stripe event's own timestamp (an older event never
/// overwrites a newer one) and the row version (two writers never both win).
/// </summary>
public sealed class Subscription : TenantEntity, IAudited
{
    /// <summary>The plan subscribed to - a <c>billing.plans</c> code.</summary>
    public required string Plan { get; set; }

    public string? StripeCustomerId { get; set; }
    public string? StripeSubscriptionId { get; set; }
    public SubscriptionStatus Status { get; set; }
    public DateTimeOffset? CurrentPeriodStart { get; set; }
    public DateTimeOffset? CurrentPeriodEnd { get; set; }
    public bool CancelAtPeriodEnd { get; set; }

    /// <summary>Human seats Stripe is charging for.</summary>
    public int SeatsHuman { get; set; }

    /// <summary>
    /// Agent seats Stripe is charging for: only the agents beyond the plan's included
    /// allowance, so zero on most organizations.
    /// </summary>
    public int SeatsAgent { get; set; }

    public DateTimeOffset? SeatsSyncedAt { get; set; }

    /// <summary>
    /// The founding offer: the discounted monthly price this subscription was
    /// sold under, how many discounted monthly billing periods it lasts, and how many have
    /// been collected. A null price means the organization never had the offer - and never
    /// will: the grant is made once, at checkout, server-side.
    ///
    /// The entitlement never changes. After the last discounted period the Stripe
    /// subscription is switched back to the plan's own price (a webhook does it at once;
    /// the nightly pass is the safety net), and these columns are only the Plan page's
    /// record of what happened.
    /// </summary>
    public decimal? FoundingPrice { get; set; }

    public int? FoundingPeriods { get; set; }

    public int FoundingPeriodsBilled { get; set; }

    public DateTimeOffset? FoundingConvertedAt { get; set; }

    /// <summary>
    /// The offer's discounted periods have all been collected and the subscription still
    /// charges the discounted price - the switch to the plan's own price is due.
    /// </summary>
    public bool FoundingConversionDue =>
        FoundingPrice is not null && FoundingConvertedAt is null
        && FoundingPeriods is { } periods && FoundingPeriodsBilled >= periods;

    /// <summary>
    /// When the first unrecovered payment failure happened, by Stripe's clock. Set once -
    /// Stripe's retries fail again without moving it, so retrying cannot stretch the grace
    /// period - and cleared when the subscription is back in good standing.
    /// </summary>
    public DateTimeOffset? PaymentFailedAt { get; set; }

    /// <summary>
    /// <see cref="PaymentFailedAt"/> plus the grace period. From this moment the organization
    /// is read-only; nothing has to run for that to happen, the comparison is the switch.
    /// </summary>
    public DateTimeOffset? GraceEndsAt { get; set; }

    /// <summary>
    /// Stripe's <c>created</c> timestamp on the newest event that set this row's state.
    /// Stripe does not promise delivery order, so every state-setting event is compared
    /// against it and an older one is dropped instead of applied.
    /// </summary>
    public DateTimeOffset? LastEventAt { get; set; }

    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }
    public uint Version { get; private set; }

    public bool IsReadOnly(DateTimeOffset now) =>
        GraceEndsAt is { } ends && ends <= now && Status.IsBilling();

    /// <summary>What the organization may use because of this row; null means "no opinion".</summary>
    public string? EntitledPlan => Status switch
    {
        SubscriptionStatus.None => null,
        _ when Status.IsBilling() => Plan,
        _ => PlanCodes.Free,
    };

    /// <summary>Tells the owner of <c>organizations.plan</c> to look again.</summary>
    public void AnnounceChange() => Raise(new OrganizationBillingChanged(
        OrganizationId != Guid.Empty
            ? OrganizationId
            : throw new InvalidOperationException("A subscription must know its organization before it announces a change.")));
}

/// <summary>
/// The idempotency ledger for Stripe webhooks. Stripe's event id is the primary key, and
/// the row is inserted in the same transaction as the event's effect - so a redelivery,
/// or two deliveries racing, produce one effect and one no-op, and the database is what
/// decides which.
/// </summary>
public sealed class StripeEvent
{
    public required string Id { get; init; }
    public required string Type { get; init; }

    /// <summary>Stripe's <c>created</c> - when it happened, not when it arrived.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset ReceivedAt { get; init; }

    /// <summary>Null for events about nothing this instance bills (another product on the account).</summary>
    public Guid? OrganizationId { get; init; }
}

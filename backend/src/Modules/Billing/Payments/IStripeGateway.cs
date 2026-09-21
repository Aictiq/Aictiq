namespace Aictiq.Modules.Billing.Payments;

/// <summary>One priced line on a subscription: a Stripe price and how many of it.</summary>
public sealed record StripeLine(string PriceId, long Quantity);

public sealed record StripeCheckoutRequest(
    Guid OrganizationId,
    string Plan,
    string? CustomerId,
    string? CustomerEmail,
    IReadOnlyList<StripeLine> Lines,
    string SuccessUrl,
    string CancelUrl,
    bool Founding = false);

/// <summary>
/// Everything Aictiq asks of Stripe, and nothing else. Small on purpose: it is the seam the
/// integration tests replace, so no test ever calls Stripe, and everything that decides
/// <em>what</em> to ask — seat counts, plan checks, event ordering — stays on this side of
/// it where it can be tested.
///
/// Webhooks are not here: they arrive rather than being asked for, and their signature is
/// checked with Stripe's own <c>EventUtility</c>, which needs no network.
/// </summary>
public interface IStripeGateway
{
    /// <summary>A hosted Checkout page that starts a subscription. Returns its URL.</summary>
    Task<string> CreateCheckoutSessionAsync(StripeCheckoutRequest request, CancellationToken cancellationToken);

    /// <summary>A Customer Portal session: payment method, invoices, cancellation. Returns its URL.</summary>
    Task<string> CreatePortalSessionAsync(string customerId, string returnUrl, CancellationToken cancellationToken);

    /// <summary>
    /// Makes the subscription charge exactly <paramref name="lines"/>, prorated: items whose
    /// price is listed get that quantity, items whose price is not are removed, and listed
    /// prices it lacks are added. Absolute rather than a delta, so asking twice is harmless.
    /// A <paramref name="proration"/> of <c>none</c> applies the change at the period
    /// boundary instead — the founding offer's end is a whole-month boundary, and a
    /// mid-cycle credit would only obscure what its periods cost.
    /// </summary>
    Task UpdateSubscriptionAsync(
        string subscriptionId, string plan, IReadOnlyList<StripeLine> lines, CancellationToken cancellationToken,
        string proration = "create_prorations");

    /// <summary>Moving to Free: the paid plan runs to the end of the period it was paid for.</summary>
    Task CancelAtPeriodEndAsync(string subscriptionId, CancellationToken cancellationToken);

    /// <summary>
    /// The organization was deleted: nothing is left to bill for, so the subscription ends
    /// now. A subscription Stripe no longer knows, or has already cancelled, is success.
    /// </summary>
    Task CancelNowAsync(string subscriptionId, CancellationToken cancellationToken);
}

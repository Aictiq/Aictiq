using Microsoft.Extensions.Options;
using Stripe;

namespace Aictiq.Modules.Billing.Payments;

/// <summary>
/// <see cref="IStripeGateway"/> on the official Stripe.net SDK. The client is built on first
/// use, never at startup: an instance without <c>Stripe:SecretKey</c> must boot, and the
/// endpoints check <see cref="StripeOptions.IsConfigured"/> before anything reaches here.
/// </summary>
public sealed class StripeGateway(IOptions<StripeOptions> options) : IStripeGateway
{
    private readonly Lazy<StripeClient> _client = new(() => new StripeClient(
        options.Value.SecretKey ?? throw new InvalidOperationException("Stripe:SecretKey is not configured.")));

    public async Task<string> CreateCheckoutSessionAsync(StripeCheckoutRequest request, CancellationToken cancellationToken)
    {
        var metadata = new Dictionary<string, string>
        {
            ["organizationId"] = request.OrganizationId.ToString(),
            ["plan"] = request.Plan,
        };
        if (request.Founding)
        {
            // The grant rides on the session: the webhook that completes the checkout
            // stamps the founding offer on the subscription row from configuration.
            metadata["founding"] = "true";
        }
        var session = await new global::Stripe.Checkout.SessionService(_client.Value).CreateAsync(
            new global::Stripe.Checkout.SessionCreateOptions
            {
                Mode = "subscription",
                // The organization rides on the session twice: client_reference_id for the
                // checkout.session.completed event, and subscription metadata for every
                // customer.subscription.* event that follows it.
                ClientReferenceId = request.OrganizationId.ToString(),
                Customer = request.CustomerId,
                CustomerEmail = request.CustomerId is null ? request.CustomerEmail : null,
                LineItems = request.Lines
                    .Select(line => new global::Stripe.Checkout.SessionLineItemOptions { Price = line.PriceId, Quantity = line.Quantity })
                    .ToList(),
                Metadata = metadata,
                SubscriptionData = new global::Stripe.Checkout.SessionSubscriptionDataOptions { Metadata = metadata },
                SuccessUrl = request.SuccessUrl,
                CancelUrl = request.CancelUrl,
            },
            cancellationToken: cancellationToken);
        return session.Url;
    }

    public async Task<string> CreatePortalSessionAsync(string customerId, string returnUrl, CancellationToken cancellationToken)
    {
        var session = await new global::Stripe.BillingPortal.SessionService(_client.Value).CreateAsync(
            new global::Stripe.BillingPortal.SessionCreateOptions { Customer = customerId, ReturnUrl = returnUrl },
            cancellationToken: cancellationToken);
        return session.Url;
    }

    public async Task UpdateSubscriptionAsync(
        string subscriptionId, string plan, IReadOnlyList<StripeLine> lines, CancellationToken cancellationToken,
        string proration = "create_prorations")
    {
        var service = new SubscriptionService(_client.Value);
        var subscription = await service.GetAsync(subscriptionId, cancellationToken: cancellationToken);
        var wanted = lines.Where(line => line.Quantity > 0).ToDictionary(line => line.PriceId, line => line.Quantity);

        var items = new List<SubscriptionItemOptions>();
        foreach (var item in subscription.Items.Data)
        {
            items.Add(wanted.Remove(item.Price.Id, out var quantity)
                ? new SubscriptionItemOptions { Id = item.Id, Quantity = quantity }
                : new SubscriptionItemOptions { Id = item.Id, Deleted = true });
        }
        items.AddRange(wanted.Select(line => new SubscriptionItemOptions { Price = line.Key, Quantity = line.Value }));

        await service.UpdateAsync(subscriptionId, new SubscriptionUpdateOptions
        {
            Items = items,
            ProrationBehavior = proration,
            // Changing plan un-schedules a pending cancellation: choosing Team is not also
            // choosing to leave at the end of the month.
            CancelAtPeriodEnd = false,
            Metadata = new Dictionary<string, string> { ["plan"] = plan },
        }, cancellationToken: cancellationToken);
    }

    public Task CancelAtPeriodEndAsync(string subscriptionId, CancellationToken cancellationToken) =>
        new SubscriptionService(_client.Value).UpdateAsync(subscriptionId,
            new SubscriptionUpdateOptions { CancelAtPeriodEnd = true }, cancellationToken: cancellationToken);

    public async Task CancelNowAsync(string subscriptionId, CancellationToken cancellationToken)
    {
        try
        {
            await new SubscriptionService(_client.Value).CancelAsync(subscriptionId, cancellationToken: cancellationToken);
        }
        catch (StripeException ex) when (ex.StripeError?.Code == "resource_missing"
            || ex.StripeError?.Message?.Contains("canceled", StringComparison.OrdinalIgnoreCase) == true)
        {
            // Already gone or already cancelled: the outcome asked for is the one Stripe has.
        }
    }
}

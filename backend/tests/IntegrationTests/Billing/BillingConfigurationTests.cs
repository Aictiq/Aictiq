using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.Billing.Endpoints;
using Aictiq.Modules.Tenancy.Endpoints;

namespace Aictiq.IntegrationTests.Billing;

/// <summary>
/// Billing is optional in the strict sense: a self-hosted instance and a SaaS
/// instance without Stripe keys both boot, both answer the billing endpoints cleanly, and
/// neither is ever made read-only by a subscription row. And the one anonymous endpoint
/// billing adds is reachable the way Stripe reaches it.
/// </summary>
[Trait("Category", "Billing")]
[Collection("postgres")]
public sealed class BillingConfigurationTests(PostgresFixture postgres, GarageFixture garage)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task a_self_hosted_instance_bills_nothing_and_is_never_read_only()
    {
        await using var host = await BillingTestHost.CreateAsync(postgres, garage, "billing_selfhosted", mode: "self_hosted");
        var (owner, orgId, _) = await host.CreateOrganizationAsync("acme", Ct);

        var subscription = (await owner.GetFromJsonAsync<SubscriptionView>(
            "/api/v1/orgs/acme/billing/subscription", ApiTestContext.Json, Ct))!;
        Assert.Equal("self_hosted", subscription.Mode);
        Assert.False(subscription.Enabled);
        Assert.Equal("self_hosted", subscription.Plan);

        var checkout = await owner.PostAsJsonAsync("/api/v1/orgs/acme/billing/checkout",
            new CheckoutRequest("starter"), ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.Conflict, checkout.StatusCode);
        Assert.Equal("https://aictiq.com/problems/billing-unavailable",
            (await checkout.Content.ReadFromJsonAsync<ProblemDetails>(Ct))!.Type);
        var payload = StripeEvents.CheckoutCompleted("evt_x", orgId, "cus_x", "sub_x", "starter", DateTimeOffset.UtcNow);
        Assert.Equal(HttpStatusCode.NotFound, (await host.PostWebhookAsync(payload, Ct)).StatusCode);

        // Even a lapsed subscription row - say, from before the instance left SaaS - closes nothing.
        await host.ExecuteAsync(
            """
            INSERT INTO billing.subscriptions (id, organization_id, plan, stripe_customer_id, stripe_subscription_id, status,
                cancel_at_period_end, seats_human, seats_agent, payment_failed_at, grace_ends_at, created_at, updated_at)
            VALUES (gen_random_uuid(), @org, 'starter', 'cus_x', 'sub_x', 5, false, 1, 0,
                now() - interval '30 days', now() - interval '16 days', now(), now())
            """, Ct, ("org", orgId));
        var project = await owner.PostAsJsonAsync("/api/v1/orgs/acme/projects",
            new CreateProjectRequest("Website", "WEB", null, null, null, null), ApiTestContext.Json, Ct);
        var created = (await project.Content.ReadFromJsonAsync<ProjectView>(ApiTestContext.Json, Ct))!;
        var rename = await owner.PatchAsJsonAsync("/api/v1/orgs/acme/projects/WEB",
            new UpdateProjectRequest("Renamed", null, null, null, null, created.Version), ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.OK, rename.StatusCode);
    }

    [Fact]
    public async Task a_saas_instance_without_stripe_keys_boots_and_says_billing_is_unavailable()
    {
        await using var host = await BillingTestHost.CreateAsync(postgres, garage, "billing_nokeys", stripeConfigured: false);
        var (owner, orgId, _) = await host.CreateOrganizationAsync("acme", Ct);

        var subscription = (await owner.GetFromJsonAsync<SubscriptionView>(
            "/api/v1/orgs/acme/billing/subscription", ApiTestContext.Json, Ct))!;
        Assert.Equal("saas", subscription.Mode);
        Assert.False(subscription.Enabled);
        Assert.Equal("free", subscription.Plan);
        Assert.All(subscription.Plans, plan => Assert.False(plan.Purchasable));

        var checkout = await owner.PostAsJsonAsync("/api/v1/orgs/acme/billing/checkout",
            new CheckoutRequest("starter"), ApiTestContext.Json, Ct);
        var portal = await owner.PostAsync("/api/v1/orgs/acme/billing/portal", null, Ct);
        Assert.Equal(HttpStatusCode.Conflict, checkout.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, portal.StatusCode);
        Assert.Equal("https://aictiq.com/problems/billing-unavailable",
            (await portal.Content.ReadFromJsonAsync<ProblemDetails>(Ct))!.Type);

        var payload = StripeEvents.CheckoutCompleted("evt_x", orgId, "cus_x", "sub_x", "starter", DateTimeOffset.UtcNow);
        Assert.Equal(HttpStatusCode.NotFound, (await host.PostWebhookAsync(payload, Ct)).StatusCode);
        Assert.Empty(host.Stripe.Checkouts);
    }

    [Fact]
    public async Task the_webhook_is_not_throttled_by_the_per_ip_limiter_and_needs_no_csrf_header()
    {
        // A budget far below what the test sends, so a limiter that applied would show.
        await using var host = await BillingTestHost.CreateAsync(postgres, garage, "billing_ratelimit",
            configure: settings => settings["RateLimiting:GlobalPermitLimitPerMinute"] = "5");

        for (var i = 0; i < 12; i++)
        {
            var payload = StripeEvents.Subscription("customer.subscription.updated", $"evt_burst_{i}", "sub_unknown",
                "cus_unknown", "active", DateTimeOffset.UtcNow);
            // PostWebhookAsync sends no X-Aictiq-Request header and no credentials, as Stripe does.
            var response = await host.PostWebhookAsync(payload, Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // The limiter is live for everything else on the same address.
        using var anonymous = host.Context.Anonymous();
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 8; i++) statuses.Add((await anonymous.GetAsync("/api/v1/orgs", Ct)).StatusCode);
        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
    }
}

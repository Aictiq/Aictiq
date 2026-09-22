using System.Net;
using System.Net.Http.Json;
using System.Text;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.Billing;
using Aictiq.Modules.Billing.Domain;
using Aictiq.Modules.Billing.Endpoints;
using Aictiq.Modules.Tenancy.Endpoints;
using Aictiq.Modules.WorkItems.Domain;
using Aictiq.Modules.WorkItems.Endpoints;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Aictiq.IntegrationTests.Billing;

/// <summary>
/// The hosted launch offer: the evaluation a new organization is born with, what
/// it entitles, what expiry takes away (and what it emphatically does not), and the flat
/// $49 subscription that ends it. Stripe is <see cref="FakeStripeGateway"/> throughout, so
/// "expiry never charges a card" is an assertion about recorded calls rather than a hope.
/// </summary>
[Trait("Category", "Billing")]
[Collection("postgres")]
public sealed class HostedOfferTests(PostgresFixture postgres, GarageFixture garage) : IAsyncLifetime
{
    private const string Slug = "hosted-co";
    private const string Customer = "cus_test_hosted";
    private const string SubscriptionId = "sub_test_hosted";

    private BillingTestHost _host = null!;
    private HttpClient _owner = null!;
    private Guid _orgId;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _host = await BillingTestHost.CreateAsync(postgres, garage, "hosted_offer");
        (_owner, _orgId, _) = await _host.CreateOrganizationAsync(Slug, Ct);
        // The evaluation is minted by an integration-event handler, which runs in Workers.
        await _host.DrainOutboxAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        _owner.Dispose();
        await _host.DisposeAsync();
    }

    // ------------------------------------------------------------------- the evaluation

    [Fact]
    public async Task a_new_organization_gets_exactly_one_thirty_day_evaluation_and_nothing_moves_it()
    {
        Assert.Equal(1, await _host.ScalarAsync("SELECT count(*) FROM billing.evaluations", Ct));
        Assert.Equal(_orgId, await GuidAsync("SELECT organization_id FROM billing.evaluations"));
        Assert.Equal(30 * 86400, await _host.ScalarAsync(
            "SELECT extract(epoch FROM (ends_at - started_at))::bigint FROM billing.evaluations", Ct));
        var endsAt = await EndsAtAsync();

        // The outbox is at-least-once: the same OrganizationCreated redelivered, twice over,
        // must produce one window - the unique index on the organization is what says so.
        await _host.ExecuteAsync(
            "UPDATE shared.outbox_messages SET processed_at = NULL WHERE type LIKE '%OrganizationCreated'", Ct);
        await _host.DrainOutboxAsync(Ct);
        await _host.DrainOutboxAsync(Ct);

        Assert.Equal(1, await _host.ScalarAsync("SELECT count(*) FROM billing.evaluations", Ct));
        Assert.Equal(endsAt, await EndsAtAsync());

        // Neither inviting someone nor asking for a plan is a reason to restart the clock.
        var invited = await _owner.PostAsJsonAsync($"/api/v1/orgs/{Slug}/invitations",
            new CreateInvitationRequest("newcomer@test.local", OrgRole.Member, null, null), ApiTestContext.Json, Ct);
        Assert.True(invited.IsSuccessStatusCode, await invited.Content.ReadAsStringAsync(Ct));
        Assert.Equal(HttpStatusCode.OK, (await CheckoutAsync("hosted")).StatusCode);
        await _host.DrainOutboxAsync(Ct);

        Assert.Equal(1, await _host.ScalarAsync("SELECT count(*) FROM billing.evaluations", Ct));
        Assert.Equal(endsAt, await EndsAtAsync());
    }

    [Fact]
    public async Task during_the_evaluation_the_organization_is_entitled_to_hosted_rather_than_free()
    {
        var subscription = await SubscriptionAsync();
        Assert.Equal("hosted", subscription.Plan);
        Assert.Null(subscription.SubscribedPlan);
        Assert.False(subscription.ReadOnly);
        Assert.NotNull(subscription.Evaluation);
        Assert.False(subscription.Evaluation!.Expired);
        Assert.Equal(30, (int)Math.Round((subscription.Evaluation.EndsAt - subscription.Evaluation.StartedAt).TotalDays));

        // The allowances the Plan page draws, both through the plan list and through the
        // Owner's summary, which is what the quota checks read.
        var hosted = Assert.Single(subscription.Plans, plan => plan.Code == PlanCodes.Hosted);
        Assert.True(hosted.Purchasable);
        Assert.Equal(49m, hosted.OrganizationPrice);
        AssertHostedLimits(hosted.Limits);
        var summary = await SummaryAsync();
        Assert.Equal("saas", summary.Mode);
        Assert.Equal("hosted", summary.Plan);
        AssertHostedLimits(summary.Limits);

        // And they are real, not decoration: the old Free cap was three projects.
        foreach (var key in new[] { "ONE", "TWO", "THR", "FOU" })
        {
            var created = await _owner.PostAsJsonAsync($"/api/v1/orgs/{Slug}/projects",
                new CreateProjectRequest($"Project {key}", key, null, null, null, null), ApiTestContext.Json, Ct);
            Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
        }
        Assert.Equal(4, (await SummaryAsync()).Usage.Projects);
    }

    [Fact]
    public async Task expiry_stops_writes_charges_nothing_and_leaves_reads_exports_and_checkout_working()
    {
        var project = await CreateProjectAsync("Website", "WEB");
        var item = await CreateItemAsync("WEB", "Still readable after expiry");

        await ExpireAsync();

        // Reads, on three different surfaces.
        Assert.Equal(HttpStatusCode.OK, (await _owner.GetAsync($"/api/v1/orgs/{Slug}/items/{item.Key}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _owner.GetAsync($"/api/v1/orgs/{Slug}/projects", Ct)).StatusCode);
        var export = await _owner.GetAsync($"/api/v1/orgs/{Slug}/projects/WEB/export/csv", Ct);
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        // The CSV carries externalRef and title, not the item key.
        Assert.Contains(item.Title, await export.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        // An ordinary write is not.
        var rename = await _owner.PatchAsJsonAsync($"/api/v1/orgs/{Slug}/projects/WEB",
            new UpdateProjectRequest("Renamed", null, null, null, null, project.Version), ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.Conflict, rename.StatusCode);
        Assert.Equal(ProblemTypes.OrganizationReadOnly,
            (await rename.Content.ReadFromJsonAsync<ProblemDetails>(Ct))!.Type);

        var expired = await SubscriptionAsync();
        Assert.True(expired.ReadOnly);
        Assert.True(expired.Evaluation!.Expired);
        // Still entitled to the Hosted allowances: expiry removes writes, not allowances,
        // and nobody was charged for the difference.
        Assert.Equal("hosted", expired.Plan);

        // Nothing was billed for any of that - no checkout session, no subscription update.
        Assert.Empty(_host.Stripe.Checkouts);
        Assert.Empty(_host.Stripe.Updates);
        Assert.Empty(_host.Stripe.Cancellations);

        // And the one write that must still work does: paying.
        Assert.Equal(HttpStatusCode.OK, (await CheckoutAsync("hosted")).StatusCode);
        Assert.Single(_host.Stripe.Checkouts);
    }

    [Fact]
    public async Task paying_after_expiry_restores_writes()
    {
        var project = await CreateProjectAsync("Website", "WEB");
        await ExpireAsync();
        Assert.Equal(HttpStatusCode.Conflict, (await RenameAsync("WEB", "Blocked", project.Version)).StatusCode);

        await PayAsync();

        var restored = await SubscriptionAsync();
        Assert.False(restored.ReadOnly);
        Assert.Equal("hosted", restored.Plan);
        Assert.Equal("hosted", restored.SubscribedPlan);
        Assert.Equal(HttpStatusCode.OK, (await RenameAsync("WEB", "Writable again", project.Version)).StatusCode);
        // The evaluation row survives - it is history, not an entitlement to revoke - and
        // the paid subscription is what answers now.
        Assert.Equal(1, await _host.ScalarAsync("SELECT count(*) FROM billing.evaluations", Ct));
    }

    [Fact]
    public async Task the_browser_cannot_ask_for_the_founding_price()
    {
        // The offer is not running on this instance, and the request body has no say in it:
        // an extra "founding": true is simply not part of the contract.
        using var content = new StringContent("""{"plan":"hosted","founding":true}""", Encoding.UTF8, "application/json");
        var response = await _owner.PostAsync($"/api/v1/orgs/{Slug}/billing/checkout", content, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var request = Assert.Single(_host.Stripe.Checkouts);
        Assert.False(request.Founding);
        Assert.Equal([(BillingTestHost.HostedOrganization, 1L)], request.Lines.Select(l => (l.PriceId, l.Quantity)).ToArray());
    }

    // --------------------------------------------------------------------------- helpers

    internal static void AssertHostedLimits(PlanLimits limits)
    {
        Assert.Null(limits.SeatsHuman);
        Assert.Null(limits.SeatsAgent);
        Assert.Null(limits.Projects);
        Assert.Equal(10_737_418_240L, limits.StorageBytes);
        Assert.Equal(90, limits.RunLogDays);
        Assert.Equal(365, limits.AnalyticsDays);
        Assert.True(limits.HasFeature("webhooks"));
        Assert.True(limits.HasFeature("page_permissions"));
        Assert.True(limits.HasFeature("audit_export"));
    }

    /// <summary>
    /// Thirty-one days on: the window is behind us. <c>started_at</c> moves with it because
    /// <c>ck_evaluations_window</c> will not represent a window that ends before it began.
    /// </summary>
    private Task ExpireAsync() => _host.ExecuteAsync(
        "UPDATE billing.evaluations SET started_at = now() - interval '31 days', ends_at = now() - interval '1 day'", Ct);

    private async Task PayAsync()
    {
        var paid = await _host.PostWebhookAsync(StripeEvents.CheckoutCompleted(
            $"evt_pay_{Guid.NewGuid():N}", _orgId, Customer, SubscriptionId, "hosted", DateTimeOffset.UtcNow), Ct);
        Assert.Equal(HttpStatusCode.OK, paid.StatusCode);
        await _host.DrainOutboxAsync(Ct);
    }

    private Task<HttpResponseMessage> CheckoutAsync(string plan) =>
        _owner.PostAsJsonAsync($"/api/v1/orgs/{Slug}/billing/checkout", new CheckoutRequest(plan), ApiTestContext.Json, Ct);

    private Task<HttpResponseMessage> RenameAsync(string key, string name, uint version) =>
        _owner.PatchAsJsonAsync($"/api/v1/orgs/{Slug}/projects/{key}",
            new UpdateProjectRequest(name, null, null, null, null, version), ApiTestContext.Json, Ct);

    private async Task<ProjectView> CreateProjectAsync(string name, string key)
    {
        var response = await _owner.PostAsJsonAsync($"/api/v1/orgs/{Slug}/projects",
            new CreateProjectRequest(name, key, null, null, null, null), ApiTestContext.Json, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<ProjectView>(ApiTestContext.Json, Ct))!;
    }

    private async Task<WorkItemView> CreateItemAsync(string projectKey, string title)
    {
        var response = await _owner.PostAsJsonAsync($"/api/v1/orgs/{Slug}/projects/{projectKey}/items/",
            new CreateWorkItemRequest(WorkItemType.Bug, title, null, null, null, null, null, null, null, null, null, null, null, null),
            ApiTestContext.Json, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<WorkItemView>(ApiTestContext.Json, Ct))!;
    }

    private async Task<SubscriptionDto> SubscriptionAsync()
    {
        var response = await _owner.GetAsync($"/api/v1/orgs/{Slug}/billing/subscription", Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<SubscriptionDto>(ApiTestContext.Json, Ct))!;
    }

    private async Task<SummaryDto> SummaryAsync()
    {
        var response = await _owner.GetAsync($"/api/v1/orgs/{Slug}/billing/", Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<SummaryDto>(ApiTestContext.Json, Ct))!;
    }

    private async Task<long> EndsAtAsync() =>
        await _host.ScalarAsync("SELECT (extract(epoch FROM ends_at) * 1000)::bigint FROM billing.evaluations", Ct);

    private async Task<Guid> GuidAsync(string sql)
    {
        await using var connection = new Npgsql.NpgsqlConnection(_host.Context.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new Npgsql.NpgsqlCommand(sql, connection);
        return (Guid)(await command.ExecuteScalarAsync(Ct))!;
    }

    /// <summary>The wire shape of <c>GET /billing/subscription</c>, status as the string a client sees.</summary>
    internal sealed record SubscriptionDto(
        string Mode, bool Enabled, string Plan, string? SubscribedPlan, string Status, DateTimeOffset? CurrentPeriodEnd,
        bool CancelAtPeriodEnd, SeatsView Seats, DateTimeOffset? PaymentFailedAt, DateTimeOffset? GraceEndsAt,
        bool ReadOnly, bool HasBillingAccount, EvaluationView? Evaluation, FoundingOfferView? Founding,
        IReadOnlyList<PlanOptionView> Plans);

    internal sealed record SummaryDto(string Mode, string Plan, PlanLimits Limits, EvaluationView? Evaluation, UsageDto Usage);

    internal sealed record UsageDto(int Humans, int Agents, int Projects, long StorageBytes);
}

/// <summary>
/// A self-hosted instance: the same build, no commercial anything. No evaluation is minted,
/// the Plan page has nothing to sell, and no quota applies.
/// </summary>
[Trait("Category", "Billing")]
[Collection("postgres")]
public sealed class SelfHostedOfferTests(PostgresFixture postgres, GarageFixture garage) : IAsyncLifetime
{
    private const string Slug = "self-hosted-co";

    private BillingTestHost _host = null!;
    private HttpClient _owner = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _host = await BillingTestHost.CreateAsync(postgres, garage, "self_hosted_offer", mode: "self_hosted");
        (_owner, _, _) = await _host.CreateOrganizationAsync(Slug, Ct);
        await _host.DrainOutboxAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        _owner.Dispose();
        await _host.DisposeAsync();
    }

    [Fact]
    public async Task a_self_hosted_instance_mints_no_evaluation_shows_no_billing_and_has_no_quotas()
    {
        Assert.Equal(0, await _host.ScalarAsync("SELECT count(*) FROM billing.evaluations", Ct));

        var response = await _owner.GetAsync($"/api/v1/orgs/{Slug}/billing/subscription", Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        var subscription = (await response.Content.ReadFromJsonAsync<HostedOfferTests.SubscriptionDto>(ApiTestContext.Json, Ct))!;
        Assert.Equal("self_hosted", subscription.Mode);
        Assert.False(subscription.Enabled);
        Assert.Equal(PlanCodes.SelfHosted, subscription.Plan);
        Assert.Empty(subscription.Plans);
        Assert.Null(subscription.Evaluation);
        Assert.Null(subscription.Founding);
        Assert.False(subscription.ReadOnly);

        // Well past every commercial cap, including the old Free three-project one.
        foreach (var key in new[] { "ONE", "TWO", "THR", "FOU", "FIV" })
        {
            var created = await _owner.PostAsJsonAsync($"/api/v1/orgs/{Slug}/projects",
                new CreateProjectRequest($"Project {key}", key, null, null, null, null), ApiTestContext.Json, Ct);
            Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
        }
        for (var index = 0; index < 4; index++)
        {
            var agent = await _owner.PostAsJsonAsync($"/api/v1/orgs/{Slug}/agents",
                new CreateAgentRequest($"agent-{index}", null), ApiTestContext.Json, Ct);
            Assert.True(agent.StatusCode == HttpStatusCode.Created, await agent.Content.ReadAsStringAsync(Ct));
        }

        // Nothing to buy, and nothing that pretends otherwise.
        var checkout = await _owner.PostAsJsonAsync($"/api/v1/orgs/{Slug}/billing/checkout",
            new CheckoutRequest("hosted"), ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.Conflict, checkout.StatusCode);
        Assert.Equal(ProblemTypes.BillingUnavailable,
            (await checkout.Content.ReadFromJsonAsync<ProblemDetails>(Ct))!.Type);
        Assert.Empty(_host.Stripe.Checkouts);
    }
}

/// <summary>
/// The founding offer: $29 for twelve monthly periods, granted by the server at
/// checkout and never by the browser, then back to the plan's own price with no change to
/// what the organization may do.
/// </summary>
[Trait("Category", "Billing")]
[Collection("postgres")]
public sealed class FoundingOfferTests(PostgresFixture postgres, GarageFixture garage) : IAsyncLifetime
{
    private const string Slug = "founding-co";
    private const string Customer = "cus_test_founding";
    private const string SubscriptionId = "sub_test_founding";

    private BillingTestHost _host = null!;
    private HttpClient _owner = null!;
    private Guid _orgId;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _host = await BillingTestHost.CreateAsync(postgres, garage, "founding_offer",
            configure: settings => settings["Billing:FoundingPrice"] = "29");
        (_owner, _orgId, _) = await _host.CreateOrganizationAsync(Slug, Ct);
        await _host.DrainOutboxAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        _owner.Dispose();
        await _host.DisposeAsync();
    }

    [Fact]
    public async Task the_founding_price_bills_twelve_periods_and_then_the_plan_price_without_losing_the_entitlement()
    {
        var checkout = await CheckoutAsync("hosted");
        Assert.Equal(HttpStatusCode.OK, checkout.StatusCode);
        var request = Assert.Single(_host.Stripe.Checkouts);
        Assert.True(request.Founding);
        Assert.Equal("hosted", request.Plan);
        Assert.Equal([(BillingTestHost.HostedFounding, 1L)], request.Lines.Select(l => (l.PriceId, l.Quantity)).ToArray());

        // Stripe comes back with Aictiq's own founding flag in the session metadata.
        await DeliverAsync(StripeEvents.CheckoutCompleted("evt_founding_checkout", _orgId, Customer, SubscriptionId,
            "hosted", DateTimeOffset.UtcNow.AddYears(-1), founding: true));
        await _host.DrainOutboxAsync(Ct);

        Assert.Equal(2900, await ScalarAsync("SELECT (founding_price * 100)::bigint FROM billing.subscriptions"));
        Assert.Equal(12, await ScalarAsync("SELECT founding_periods FROM billing.subscriptions"));
        Assert.Equal(0, await ScalarAsync("SELECT founding_periods_billed FROM billing.subscriptions"));
        Assert.Equal(0, await ScalarAsync("SELECT count(*) FROM billing.subscriptions WHERE founding_converted_at IS NOT NULL"));
        await AssertEntitledToHostedAsync(periodsBilled: 0, converted: false);

        // A second checkout while the offer is running does not re-grant it, and the plan
        // the organization is already on is refused outright.
        var again = await CheckoutAsync("hosted");
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Equal(1, await ScalarAsync("SELECT count(*) FROM billing.subscriptions"));
        Assert.Equal(0, await ScalarAsync("SELECT founding_periods_billed FROM billing.subscriptions"));

        var start = DateTimeOffset.UtcNow.AddMonths(-11);
        for (var period = 1; period <= 12; period++)
        {
            var eventId = $"evt_invoice_{period:00}";
            await DeliverAsync(StripeEvents.InvoicePaid(eventId, SubscriptionId, Customer, start.AddDays(period)));
            Assert.Equal(period, await ScalarAsync("SELECT founding_periods_billed FROM billing.subscriptions"));

            // Stripe redelivers; the ledger's primary key is what makes that free.
            await DeliverAsync(StripeEvents.InvoicePaid(eventId, SubscriptionId, Customer, start.AddDays(period)));
            Assert.Equal(period, await ScalarAsync("SELECT founding_periods_billed FROM billing.subscriptions"));

            if (period < 12)
            {
                Assert.Empty(_host.Stripe.Updates);
                await AssertEntitledToHostedAsync(period, converted: false);
            }
        }

        // The twelfth collected period switches the subscription to the plan's own price,
        // at the period boundary rather than prorated.
        var (subscriptionId, plan, lines, proration) = Assert.Single(_host.Stripe.Updates);
        Assert.Equal(SubscriptionId, subscriptionId);
        Assert.Equal("hosted", plan);
        Assert.Equal([(BillingTestHost.HostedOrganization, 1L)], lines.Select(l => (l.PriceId, l.Quantity)).ToArray());
        Assert.Equal("none", proration);
        Assert.Equal(1, await ScalarAsync("SELECT count(*) FROM billing.subscriptions WHERE founding_converted_at IS NOT NULL"));

        // A thirteenth invoice counts nothing more and converts nothing twice.
        await DeliverAsync(StripeEvents.InvoicePaid("evt_invoice_13", SubscriptionId, Customer, DateTimeOffset.UtcNow));
        Assert.Equal(12, await ScalarAsync("SELECT founding_periods_billed FROM billing.subscriptions"));
        Assert.Single(_host.Stripe.Updates);

        await AssertEntitledToHostedAsync(periodsBilled: 12, converted: true);
    }

    [Fact]
    public async Task a_converted_subscription_cannot_buy_the_offer_a_second_time()
    {
        await DeliverAsync(StripeEvents.CheckoutCompleted("evt_founding_once", _orgId, Customer, SubscriptionId,
            "hosted", DateTimeOffset.UtcNow.AddYears(-1), founding: true));
        await _host.DrainOutboxAsync(Ct);
        await _host.ExecuteAsync(
            "UPDATE billing.subscriptions SET founding_periods_billed = 12, founding_converted_at = now()", Ct);
        _host.Stripe.Updates.Clear();

        // Cancel at period end, then change your mind: the subscription is still billing,
        // so checkout changes it in place - at the plan price, never the founding one.
        Assert.Equal(HttpStatusCode.OK, (await CheckoutAsync("free")).StatusCode);
        await _host.ExecuteAsync("UPDATE billing.subscriptions SET cancel_at_period_end = true", Ct);
        var back = await CheckoutAsync("hosted");

        Assert.Equal(HttpStatusCode.OK, back.StatusCode);
        Assert.Empty(_host.Stripe.Checkouts);
        var (_, _, lines, _) = Assert.Single(_host.Stripe.Updates);
        Assert.Equal([(BillingTestHost.HostedOrganization, 1L)], lines.Select(l => (l.PriceId, l.Quantity)).ToArray());
        Assert.Equal(12, await ScalarAsync("SELECT founding_periods_billed FROM billing.subscriptions"));
        Assert.Equal(2900, await ScalarAsync("SELECT (founding_price * 100)::bigint FROM billing.subscriptions"));
        Assert.Equal(1, await ScalarAsync("SELECT count(*) FROM billing.subscriptions WHERE founding_converted_at IS NOT NULL"));
    }

    [Fact]
    public async Task the_nightly_pass_performs_the_conversion_the_webhook_could_not_and_then_does_nothing()
    {
        await DeliverAsync(StripeEvents.CheckoutCompleted("evt_founding_nightly", _orgId, Customer, SubscriptionId,
            "hosted", DateTimeOffset.UtcNow.AddYears(-1), founding: true));
        await DeliverAsync(StripeEvents.Subscription("customer.subscription.updated", "evt_founding_items",
            SubscriptionId, Customer, "active", DateTimeOffset.UtcNow.AddYears(-1).AddDays(1), plan: "hosted",
            items: [(BillingTestHost.HostedFounding, 1)]));
        await _host.DrainOutboxAsync(Ct);
        _host.Stripe.Updates.Clear();

        // Every discounted period collected, the switch never made: exactly what a webhook
        // that could not reach Stripe leaves behind.
        await _host.ExecuteAsync(
            "UPDATE billing.subscriptions SET founding_periods_billed = 12, founding_converted_at = NULL", Ct);

        Assert.Equal(SeatSyncOutcome.Updated, await SyncAsync());

        var (subscriptionId, plan, lines, proration) = Assert.Single(_host.Stripe.Updates);
        Assert.Equal(SubscriptionId, subscriptionId);
        Assert.Equal("hosted", plan);
        Assert.Equal([(BillingTestHost.HostedOrganization, 1L)], lines.Select(l => (l.PriceId, l.Quantity)).ToArray());
        Assert.Equal("none", proration);
        Assert.Equal(1, await ScalarAsync("SELECT count(*) FROM billing.subscriptions WHERE founding_converted_at IS NOT NULL"));

        // Converged: running it again asks Stripe for nothing.
        Assert.Equal(SeatSyncOutcome.Unchanged, await SyncAsync());
        Assert.Single(_host.Stripe.Updates);
        Assert.Equal(0, await ScalarAsync("SELECT seats_human FROM billing.subscriptions"));
    }

    private async Task<SeatSyncOutcome> SyncAsync()
    {
        using var scope = _host.Context.Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<SeatSynchronizer>().SyncAsync(_orgId, Ct);
    }

    private async Task AssertEntitledToHostedAsync(int periodsBilled, bool converted)
    {
        var response = await _owner.GetAsync($"/api/v1/orgs/{Slug}/billing/subscription", Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        var subscription = (await response.Content.ReadFromJsonAsync<HostedOfferTests.SubscriptionDto>(ApiTestContext.Json, Ct))!;
        Assert.Equal("hosted", subscription.Plan);
        Assert.Equal("hosted", subscription.SubscribedPlan);
        Assert.False(subscription.ReadOnly);
        HostedOfferTests.AssertHostedLimits(Assert.Single(subscription.Plans, p => p.Code == PlanCodes.Hosted).Limits);
        var founding = Assert.IsType<FoundingOfferView>(subscription.Founding);
        Assert.Equal(29m, founding.Price);
        Assert.Equal(12, founding.Periods);
        Assert.Equal(periodsBilled, founding.PeriodsBilled);
        Assert.Equal(49m, founding.RenewalPrice);
        Assert.Equal(converted, founding.ConvertedAt is not null);
    }

    private Task<HttpResponseMessage> CheckoutAsync(string plan) =>
        _owner.PostAsJsonAsync($"/api/v1/orgs/{Slug}/billing/checkout", new CheckoutRequest(plan), ApiTestContext.Json, Ct);

    private async Task DeliverAsync(string payload)
    {
        var response = await _host.PostWebhookAsync(payload, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private Task<long> ScalarAsync(string sql) => _host.ScalarAsync(sql, Ct);
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.Billing;
using Aictiq.Modules.Billing.Endpoints;
using Aictiq.Modules.Tenancy.Endpoints;
using Aictiq.SharedKernel.Authorization;

namespace Aictiq.IntegrationTests.Billing;

/// <summary>
/// Stripe subscriptions end to end over HTTP, with Stripe itself replaced by
/// <see cref="FakeStripeGateway"/>: signed webhooks in, recorded API calls out, and the
/// outbox drained the way Workers drain it.
/// </summary>
[Trait("Category", "Billing")]
[Collection("postgres")]
public sealed class StripeBillingTests(PostgresFixture postgres, GarageFixture garage) : IAsyncLifetime
{
    private BillingTestHost _host = null!;
    private HttpClient _owner = null!;
    private Guid _orgId;
    private string _ownerId = null!;
    private const string Slug = "acme";
    private const string Customer = "cus_test_acme";
    private const string SubscriptionId = "sub_test_acme";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _host = await BillingTestHost.CreateAsync(postgres, garage, "billing");
        (_owner, _orgId, _ownerId) = await _host.CreateOrganizationAsync(Slug, Ct);
    }

    public async ValueTask DisposeAsync()
    {
        _owner.Dispose();
        await _host.DisposeAsync();
    }

    // ------------------------------------------------------------------------- webhooks

    [Fact]
    public async Task a_webhook_with_a_bad_or_missing_signature_is_refused_and_records_nothing()
    {
        var payload = StripeEvents.CheckoutCompleted("evt_forged", _orgId, Customer, SubscriptionId, "starter", DateTimeOffset.UtcNow);

        var wrongSecret = await _host.PostWebhookAsync(payload, Ct, BillingTestHost.Sign(payload, "whsec_someone_else"));
        var tampered = await _host.PostWebhookAsync(payload.Replace("starter", "team"), Ct, BillingTestHost.Sign(payload));
        var stale = await _host.PostWebhookAsync(payload, Ct, BillingTestHost.Sign(payload, at: DateTimeOffset.UtcNow.AddHours(-1)));
        var missing = await _host.PostWebhookAsync(payload, Ct, signature: "");

        Assert.All([wrongSecret, tampered, stale, missing], r => Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode));
        Assert.Equal(0, await _host.ScalarAsync("SELECT count(*) FROM billing.stripe_events", Ct));
        Assert.Equal(0, await _host.ScalarAsync("SELECT count(*) FROM billing.subscriptions", Ct));
    }

    [Fact]
    public async Task the_same_event_delivered_twice_has_exactly_one_effect()
    {
        var payload = StripeEvents.CheckoutCompleted("evt_checkout_1", _orgId, Customer, SubscriptionId, "starter", DateTimeOffset.UtcNow);

        var first = await _host.PostWebhookAsync(payload, Ct);
        var second = await _host.PostWebhookAsync(payload, Ct);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.False((await first.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("duplicate").GetBoolean());
        Assert.True((await second.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("duplicate").GetBoolean());
        Assert.Equal(1, await _host.ScalarAsync("SELECT count(*) FROM billing.stripe_events WHERE id = 'evt_checkout_1'", Ct));
        Assert.Equal(1, await _host.ScalarAsync("SELECT count(*) FROM billing.subscriptions", Ct));
        // One effect means one announcement, too: Tenancy is told once.
        Assert.Equal(1, await _host.ScalarAsync(
            "SELECT count(*) FROM shared.outbox_messages WHERE type LIKE '%OrganizationBillingChanged'", Ct));
    }

    [Fact]
    public async Task concurrent_deliveries_of_one_event_serialize_on_its_id()
    {
        var payload = StripeEvents.CheckoutCompleted("evt_race", _orgId, Customer, SubscriptionId, "starter", DateTimeOffset.UtcNow);

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => _host.PostWebhookAsync(payload, Ct)));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var duplicates = await Task.WhenAll(responses.Select(async r =>
            (await r.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("duplicate").GetBoolean()));
        Assert.Single(duplicates, d => !d);
        Assert.Equal(1, await _host.ScalarAsync(
            "SELECT count(*) FROM shared.outbox_messages WHERE type LIKE '%OrganizationBillingChanged'", Ct));
    }

    [Fact]
    public async Task a_completed_checkout_moves_the_organization_onto_the_plan_it_paid_for()
    {
        Assert.Equal("self_hosted", (await GetOrganizationAsync()).Plan); // the column default…
        Assert.Equal("free", (await GetSubscriptionAsync()).Plan);        // …which SaaS treats as Free

        await DeliverAsync(StripeEvents.CheckoutCompleted("evt_c", _orgId, Customer, SubscriptionId, "starter", DateTimeOffset.UtcNow));
        await _host.DrainOutboxAsync(Ct);

        Assert.Equal("starter", (await GetOrganizationAsync()).Plan);
        var subscription = await GetSubscriptionAsync();
        Assert.Equal("starter", subscription.Plan);
        Assert.Equal("starter", subscription.SubscribedPlan);
        Assert.Equal("active", subscription.Status);
        Assert.True(subscription.HasBillingAccount);
    }

    [Fact]
    public async Task an_older_event_never_overwrites_a_newer_one_and_a_cancellation_is_final()
    {
        var now = DateTimeOffset.UtcNow;
        await DeliverAsync(StripeEvents.CheckoutCompleted("evt_1", _orgId, Customer, SubscriptionId, "starter", now.AddMinutes(-10)));
        await DeliverAsync(StripeEvents.Subscription("customer.subscription.updated", "evt_3", SubscriptionId, Customer,
            "active", now, items: [(BillingTestHost.TeamHuman, 2)]));
        // Delivered late: happened before evt_3, so it must not undo it.
        await DeliverAsync(StripeEvents.Subscription("customer.subscription.updated", "evt_2", SubscriptionId, Customer,
            "past_due", now.AddMinutes(-5), items: [(BillingTestHost.StarterHuman, 1)]));

        var afterReorder = await GetSubscriptionAsync();
        Assert.Equal("team", afterReorder.SubscribedPlan);
        Assert.Equal("active", afterReorder.Status);
        Assert.Equal(2, afterReorder.Seats.Human);
        Assert.Null(afterReorder.PaymentFailedAt);

        await DeliverAsync(StripeEvents.Subscription("customer.subscription.deleted", "evt_4", SubscriptionId, Customer,
            "canceled", now.AddMinutes(1), items: [(BillingTestHost.TeamHuman, 2)]));
        // A straggler with a later timestamp still cannot revive a cancelled subscription.
        await DeliverAsync(StripeEvents.Subscription("customer.subscription.updated", "evt_5", SubscriptionId, Customer,
            "active", now.AddMinutes(2), items: [(BillingTestHost.TeamHuman, 2)]));
        await _host.DrainOutboxAsync(Ct);

        var cancelled = await GetSubscriptionAsync();
        Assert.Equal("canceled", cancelled.Status);
        Assert.Null(cancelled.SubscribedPlan);
        Assert.Equal("free", (await GetOrganizationAsync()).Plan);
    }

    // ------------------------------------------------------------------------- seat sync

    [Fact]
    public async Task seat_sync_bills_humans_and_only_the_agents_beyond_the_included_allowance()
    {
        await MoveToStarterAsync();

        // One human on Starter includes three agents; Starter caps agents at five.
        for (var i = 1; i <= 5; i++)
        {
            var created = await _owner.PostAsJsonAsync($"/api/v1/orgs/{Slug}/agents",
                new CreateAgentRequest($"agent-{i}", null), ApiTestContext.Json, Ct);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }
        await _host.DrainOutboxAsync(Ct);

        var (subscriptionId, plan, lines, _) = _host.Stripe.Updates.Last();
        Assert.Equal(SubscriptionId, subscriptionId);
        Assert.Equal("starter", plan);
        Assert.Equal(
            [(BillingTestHost.StarterHuman, 1L), (BillingTestHost.StarterAgent, 2L)],
            lines.Select(l => (l.PriceId, l.Quantity)).ToArray());
        var seats = (await GetSubscriptionAsync()).Seats;
        Assert.Equal(new SeatsView(1, 2), seats);

        // Replaying every event changes nothing: the sync is absolute, not a delta.
        var updates = _host.Stripe.Updates.Count;
        await _host.ExecuteAsync("UPDATE shared.outbox_messages SET processed_at = NULL", Ct);
        await _host.DrainOutboxAsync(Ct);
        Assert.Equal(updates, _host.Stripe.Updates.Count);
    }

    [Fact]
    public async Task a_member_leaving_takes_their_seat_off_the_bill()
    {
        await MoveToStarterAsync();
        var bob = await _host.Context.RegisterAsync("bob@test.local", "Bob", "Brooks");
        await _host.ExecuteAsync(
            "INSERT INTO tenancy.organization_members (organization_id, user_id, role, joined_at) VALUES (@org, @user, 2, now())",
            Ct, ("org", _orgId), ("user", bob.User.Id));
        using (var scope = _host.Context.Factory.Services.CreateScope())
        {
            // The nightly run's unit of work, catching a member the per-change sync never heard about.
            await scope.ServiceProvider.GetRequiredService<Aictiq.Modules.Billing.SeatSynchronizer>().SyncAsync(_orgId, Ct);
        }
        Assert.Equal(2, (await GetSubscriptionAsync()).Seats.Human);

        var removed = await _owner.DeleteAsync($"/api/v1/orgs/{Slug}/members/{bob.User.Id}", Ct);
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        await _host.DrainOutboxAsync(Ct);

        Assert.Equal(1, (await GetSubscriptionAsync()).Seats.Human);
        Assert.Equal(1L, _host.Stripe.Updates.Last().Lines.Single().Quantity);
    }

    // ----------------------------------------------------------- grace period, read-only

    [Fact]
    public async Task a_failed_payment_leaves_the_organization_writable_until_the_grace_period_ends()
    {
        await MoveToStarterAsync();
        var project = await CreateProjectAsync("Website", "WEB");

        await DeliverAsync(StripeEvents.PaymentFailed("evt_fail_now", SubscriptionId, Customer, DateTimeOffset.UtcNow));

        var inGrace = await GetSubscriptionAsync();
        Assert.False(inGrace.ReadOnly);
        Assert.NotNull(inGrace.GraceEndsAt);
        Assert.InRange(inGrace.GraceEndsAt!.Value, DateTimeOffset.UtcNow.AddDays(13.9), DateTimeOffset.UtcNow.AddDays(14.1));
        Assert.Equal(HttpStatusCode.OK, (await RenameAsync("WEB", "Still writable", project.Version)).StatusCode);
    }

    [Fact]
    public async Task after_the_grace_period_every_project_write_is_refused_through_require_project_writable()
    {
        await MoveToStarterAsync();
        var project = await CreateProjectAsync("Website", "WEB");

        // Stripe's clock decides when the grace period started, not when the event arrived.
        await DeliverAsync(StripeEvents.PaymentFailed("evt_fail_old", SubscriptionId, Customer, DateTimeOffset.UtcNow.AddDays(-15)));
        // Stripe retrying and failing again later does not restart the clock.
        await DeliverAsync(StripeEvents.PaymentFailed("evt_fail_retry", SubscriptionId, Customer, DateTimeOffset.UtcNow.AddDays(-1)));

        Assert.True((await GetSubscriptionAsync()).ReadOnly);

        var rename = await RenameAsync("WEB", "Renamed", project.Version);
        Assert.Equal(HttpStatusCode.Conflict, rename.StatusCode);
        Assert.Equal("https://aictiq.com/problems/org-read-only",
            (await rename.Content.ReadFromJsonAsync<ProblemDetails>(Ct))!.Type);
        var label = await _owner.PostAsJsonAsync($"/api/v1/orgs/{Slug}/projects/WEB/labels/",
            new { name = "bug", color = "red" }, ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.Conflict, label.StatusCode);

        // Read-only, not locked out: reads work, and so does paying.
        Assert.Equal(HttpStatusCode.OK, (await _owner.GetAsync($"/api/v1/orgs/{Slug}/projects/WEB", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _owner.PostAsync($"/api/v1/orgs/{Slug}/billing/portal", null, Ct)).StatusCode);

        // The retry that succeeds brings the subscription back to active; writes resume.
        await DeliverAsync(StripeEvents.Subscription("customer.subscription.updated", "evt_recovered", SubscriptionId, Customer,
            "active", DateTimeOffset.UtcNow));
        Assert.False((await GetSubscriptionAsync()).ReadOnly);
        Assert.Equal(HttpStatusCode.OK, (await RenameAsync("WEB", "Renamed", project.Version)).StatusCode);
    }

    [Fact]
    public async Task a_late_payment_failure_cannot_close_an_account_that_has_since_recovered()
    {
        await MoveToStarterAsync();
        await DeliverAsync(StripeEvents.Subscription("customer.subscription.updated", "evt_active", SubscriptionId, Customer,
            "active", DateTimeOffset.UtcNow));
        await DeliverAsync(StripeEvents.PaymentFailed("evt_old_fail", SubscriptionId, Customer, DateTimeOffset.UtcNow.AddDays(-20)));

        var subscription = await GetSubscriptionAsync();
        Assert.Null(subscription.PaymentFailedAt);
        Assert.False(subscription.ReadOnly);
    }

    // ---------------------------------------------------------- checkout, portal, downgrade

    [Fact]
    public async Task checkout_sells_one_flat_organization_line_and_only_an_owner_may_ask()
    {
        var response = await CheckoutAsync(_owner, "hosted");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var redirect = (await response.Content.ReadFromJsonAsync<BillingRedirectView>(ApiTestContext.Json, Ct))!;
        Assert.StartsWith("https://checkout.stripe.test/", redirect.Url);
        var request = Assert.Single(_host.Stripe.Checkouts);
        Assert.Equal(_orgId, request.OrganizationId);
        Assert.Equal("hosted", request.Plan);
        Assert.Equal($"owner-{Slug}@test.local", request.CustomerEmail);
        // One line, quantity one, whatever the membership is.
        Assert.Equal([(BillingTestHost.HostedOrganization, 1L)], request.Lines.Select(l => (l.PriceId, l.Quantity)).ToArray());
        Assert.False(request.Founding);
        Assert.EndsWith($"/o/{Slug}/settings/billing?checkout=success", request.SuccessUrl);

        var admin = await _host.Context.RegisterAsync("adam@test.local", "Adam", "Admin");
        await _host.ExecuteAsync(
            "INSERT INTO tenancy.organization_members (organization_id, user_id, role, joined_at) VALUES (@org, @user, 1, now())",
            Ct, ("org", _orgId), ("user", admin.User.Id));
        using var adminClient = _host.Context.ClientFor(admin);
        Assert.Equal(HttpStatusCode.Forbidden, (await CheckoutAsync(adminClient, "hosted")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await CheckoutAsync(_owner, "platinum")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await CheckoutAsync(_owner, "self_hosted")).StatusCode);
        // The legacy seat plans are closed to new subscriptions, priced or not.
        Assert.Equal(HttpStatusCode.BadRequest, (await CheckoutAsync(_owner, "starter")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await CheckoutAsync(_owner, "team")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await CheckoutAsync(_owner, "enterprise")).StatusCode);
    }

    [Fact]
    public async Task a_flat_subscriptions_price_does_not_move_when_the_membership_does()
    {
        await MoveToHostedAsync();

        // Five more people and an agent: on a seat plan every one of these would have
        // re-quantified the subscription. On the flat plan none of them touches Stripe.
        for (var index = 0; index < 5; index++)
        {
            var member = await _host.Context.RegisterAsync($"member{index}@test.local", "Mia", $"Member{index}");
            await _host.ExecuteAsync(
                "INSERT INTO tenancy.organization_members (organization_id, user_id, role, joined_at) VALUES (@org, @user, 2, now())",
                Ct, ("org", _orgId), ("user", member.User.Id));
        }
        // An agent identity is a member too, and on a seat plan a sixth agent beyond the
        // included allowance would have added a line. Here it adds nothing.
        var agent = await _owner.PostAsJsonAsync($"/api/v1/orgs/{Slug}/agents",
            new CreateAgentRequest("builder", null), ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.Created, agent.StatusCode);
        await _host.DrainOutboxAsync(Ct);
        using (var scope = _host.Context.Factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<SeatSynchronizer>().SyncAsync(_orgId, Ct);
        }

        Assert.Empty(_host.Stripe.Updates);
        var subscription = await GetSubscriptionAsync();
        Assert.Equal("hosted", subscription.Plan);
        Assert.Equal(0, subscription.Seats.Human);
        Assert.Equal(0, subscription.Seats.Agent);
        // The counting still happens - it is just not what the bill is made of.
        var summary = await _owner.GetFromJsonAsync<JsonElement>($"/api/v1/orgs/{Slug}/billing/", Ct);
        Assert.Equal(6, summary.GetProperty("usage").GetProperty("humans").GetInt32());
        Assert.Equal(1, summary.GetProperty("usage").GetProperty("agents").GetInt32());
    }

    [Fact]
    public async Task a_legacy_plan_can_still_be_refreshed_by_the_subscription_that_already_carries_it()
    {
        await MoveToStarterAsync();
        // The seat sync that follows the move is not what this test is about.
        _host.Stripe.Updates.Clear();

        // The plan this organization already carries stays reachable - that is what "do
        // not break existing references" means, and it is the path a legacy customer who
        // cancelled and changed their mind comes back through. Every *other* legacy plan
        // is closed outright, so nothing here sells one to somebody new.
        var other = await CheckoutAsync(_owner, "team");
        Assert.Equal(HttpStatusCode.BadRequest, other.StatusCode);
        Assert.Contains("no longer open to new subscriptions", await Message(other), StringComparison.Ordinal);
        Assert.Empty(_host.Stripe.Updates);

        (await CheckoutAsync(_owner, "free")).EnsureSuccessStatusCode();
        await _host.ExecuteAsync("UPDATE billing.subscriptions SET cancel_at_period_end = true", Ct);

        var resumed = await CheckoutAsync(_owner, "starter");

        Assert.Equal(HttpStatusCode.OK, resumed.StatusCode);
        var (_, plan, lines, proration) = _host.Stripe.Updates.Last();
        Assert.Equal("starter", plan);
        // Their own price, at their own seat count - the price they were sold, unchanged.
        Assert.Equal([(BillingTestHost.StarterHuman, 1L)], lines.Select(l => (l.PriceId, l.Quantity)).ToArray());
        Assert.Equal("create_prorations", proration);
    }

    /// <summary>The first validation message of a 400, whichever field carries it.</summary>
    private async Task<string> Message(HttpResponseMessage response)
    {
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return problem.RootElement.GetProperty("errors").EnumerateObject()
            .SelectMany(field => field.Value.EnumerateArray().Select(value => value.GetString()!))
            .First();
    }

    [Fact]
    public async Task the_portal_needs_a_billing_account_and_then_returns_stripes_url()
    {
        Assert.Equal(HttpStatusCode.Conflict, (await _owner.PostAsync($"/api/v1/orgs/{Slug}/billing/portal", null, Ct)).StatusCode);

        await MoveToStarterAsync();
        var response = await _owner.PostAsync($"/api/v1/orgs/{Slug}/billing/portal", null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal($"https://billing.stripe.test/p/session/{Customer}",
            (await response.Content.ReadFromJsonAsync<BillingRedirectView>(ApiTestContext.Json, Ct))!.Url);
    }

    [Fact]
    public async Task a_downgrade_is_refused_while_usage_exceeds_the_target_plan_and_says_what_to_remove()
    {
        await MoveToStarterAsync();
        foreach (var key in new[] { "ONE", "TWO", "THR", "FOU" }) await CreateProjectAsync($"Project {key}", key);

        var blocked = await CheckoutAsync(_owner, "free");

        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        var problem = await blocked.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("https://aictiq.com/problems/plan-downgrade-blocked", problem.GetProperty("type").GetString());
        var exceeded = Assert.Single(problem.GetProperty("exceeded").EnumerateArray());
        Assert.Equal("projects", exceeded.GetProperty("limit").GetString());
        Assert.Equal(4, exceeded.GetProperty("used").GetInt64());
        Assert.Equal(3, exceeded.GetProperty("allowed").GetInt64());
        Assert.Empty(_host.Stripe.Cancellations);

        // Archived projects do not count; with one retired the move is allowed.
        (await _owner.PostAsync($"/api/v1/orgs/{Slug}/projects/FOU/archive", null, Ct)).EnsureSuccessStatusCode();
        var allowed = await CheckoutAsync(_owner, "free");
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.True((await allowed.Content.ReadFromJsonAsync<BillingRedirectView>(ApiTestContext.Json, Ct))!.Changed);
        Assert.Equal(SubscriptionId, Assert.Single(_host.Stripe.Cancellations));
    }

    [Fact]
    public async Task moving_a_legacy_subscription_to_hosted_is_an_update_not_a_second_checkout()
    {
        await MoveToStarterAsync();

        var response = await CheckoutAsync(_owner, "hosted");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(_host.Stripe.Checkouts);
        var (_, plan, lines, proration) = _host.Stripe.Updates.Last();
        Assert.Equal("hosted", plan);
        Assert.Equal([(BillingTestHost.HostedOrganization, 1L)], lines.Select(l => (l.PriceId, l.Quantity)).ToArray());
        Assert.Equal("create_prorations", proration);
    }

    [Fact]
    public async Task every_member_can_read_the_subscription_for_the_banner_but_outsiders_cannot()
    {
        var guest = await _host.Context.RegisterAsync("gus@test.local", "Gus", "Guest");
        await _host.ExecuteAsync(
            "INSERT INTO tenancy.organization_members (organization_id, user_id, role, joined_at, can_operate_factory) VALUES (@org, @user, 3, now(), false)",
            Ct, ("org", _orgId), ("user", guest.User.Id));
        using var guestClient = _host.Context.ClientFor(guest);
        using var outsider = _host.Context.ClientFor(await _host.Context.RegisterAsync("mallory@test.local"));

        Assert.Equal(HttpStatusCode.OK, (await guestClient.GetAsync($"/api/v1/orgs/{Slug}/billing/subscription", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"/api/v1/orgs/{Slug}/billing/subscription", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await guestClient.PostAsJsonAsync($"/api/v1/orgs/{Slug}/billing/checkout",
            new CheckoutRequest("starter"), ApiTestContext.Json, Ct)).StatusCode);
    }

    // --------------------------------------------------------------------------- helpers

    private async Task DeliverAsync(string payload)
    {
        var response = await _host.PostWebhookAsync(payload, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task MoveToHostedAsync()
    {
        await DeliverAsync(StripeEvents.CheckoutCompleted($"evt_hosted_{Guid.NewGuid():N}", _orgId, Customer, SubscriptionId,
            "hosted", DateTimeOffset.UtcNow.AddDays(-60)));
        await DeliverAsync(StripeEvents.Subscription("customer.subscription.updated", $"evt_hosted_items_{Guid.NewGuid():N}",
            SubscriptionId, Customer, "active", DateTimeOffset.UtcNow.AddDays(-59), plan: "hosted",
            items: [(BillingTestHost.HostedOrganization, 1)]));
        await _host.DrainOutboxAsync(Ct);
        Assert.Equal("hosted", (await GetOrganizationAsync()).Plan);
        _host.Stripe.Updates.Clear();
    }

    private async Task MoveToStarterAsync()
    {
        // Dated well in the past, so the backdated events the grace-period tests send are
        // newer than it - the ordering guard would otherwise (rightly) drop them.
        await DeliverAsync(StripeEvents.CheckoutCompleted($"evt_move_{Guid.NewGuid():N}", _orgId, Customer, SubscriptionId,
            "starter", DateTimeOffset.UtcNow.AddDays(-60)));
        await _host.DrainOutboxAsync(Ct);
        Assert.Equal("starter", (await GetOrganizationAsync()).Plan);
    }

    private Task<HttpResponseMessage> CheckoutAsync(HttpClient client, string plan) =>
        client.PostAsJsonAsync($"/api/v1/orgs/{Slug}/billing/checkout", new CheckoutRequest(plan), ApiTestContext.Json, Ct);

    private async Task<OrganizationView> GetOrganizationAsync() =>
        (await _owner.GetFromJsonAsync<OrganizationView>($"/api/v1/orgs/{Slug}", ApiTestContext.Json, Ct))!;

    private async Task<SubscriptionDto> GetSubscriptionAsync()
    {
        var response = await _owner.GetAsync($"/api/v1/orgs/{Slug}/billing/subscription", Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<SubscriptionDto>(ApiTestContext.Json, Ct))!;
    }

    private async Task<ProjectView> CreateProjectAsync(string name, string key)
    {
        var response = await _owner.PostAsJsonAsync($"/api/v1/orgs/{Slug}/projects",
            new CreateProjectRequest(name, key, null, null, null, null), ApiTestContext.Json, Ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProjectView>(ApiTestContext.Json, Ct))!;
    }

    private Task<HttpResponseMessage> RenameAsync(string key, string name, uint version) =>
        _owner.PatchAsJsonAsync($"/api/v1/orgs/{Slug}/projects/{key}",
            new UpdateProjectRequest(name, null, null, null, null, version), ApiTestContext.Json, Ct);

    /// <summary>The wire shape, with the status as the string a client sees.</summary>
    private sealed record SubscriptionDto(
        string Mode, bool Enabled, string Plan, string? SubscribedPlan, string Status, DateTimeOffset? CurrentPeriodEnd,
        bool CancelAtPeriodEnd, SeatsView Seats, DateTimeOffset? PaymentFailedAt, DateTimeOffset? GraceEndsAt,
        bool ReadOnly, bool HasBillingAccount, EvaluationView? Evaluation, FoundingOfferView? Founding,
        IReadOnlyList<PlanOptionView> Plans);
}

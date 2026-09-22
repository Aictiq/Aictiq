using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.Billing.Payments;
using Aictiq.Modules.Tenancy;
using Aictiq.Modules.Tenancy.Endpoints;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Outbox;

namespace Aictiq.IntegrationTests.Billing;

/// <summary>
/// Stands in for Stripe's API. Nothing in the suite may reach Stripe; everything that
/// decides <em>what</em> to ask it is on the Aictiq side of <see cref="IStripeGateway"/>
/// and is asserted through what this records.
/// </summary>
public sealed class FakeStripeGateway : IStripeGateway
{
    public ConcurrentQueue<StripeCheckoutRequest> Checkouts { get; } = new();
    public ConcurrentQueue<(string SubscriptionId, string Plan, IReadOnlyList<StripeLine> Lines, string Proration)> Updates { get; } = new();
    public ConcurrentQueue<string> Cancellations { get; } = new();
    public ConcurrentQueue<string> Portals { get; } = new();

    public Task<string> CreateCheckoutSessionAsync(StripeCheckoutRequest request, CancellationToken cancellationToken)
    {
        Checkouts.Enqueue(request);
        return Task.FromResult($"https://checkout.stripe.test/c/pay/cs_test_{Checkouts.Count}");
    }

    public Task<string> CreatePortalSessionAsync(string customerId, string returnUrl, CancellationToken cancellationToken)
    {
        Portals.Enqueue(customerId);
        return Task.FromResult($"https://billing.stripe.test/p/session/{customerId}");
    }

    public Task UpdateSubscriptionAsync(
        string subscriptionId, string plan, IReadOnlyList<StripeLine> lines, CancellationToken cancellationToken,
        string proration = "create_prorations")
    {
        Updates.Enqueue((subscriptionId, plan, lines, proration));
        return Task.CompletedTask;
    }

    public Task CancelAtPeriodEndAsync(string subscriptionId, CancellationToken cancellationToken)
    {
        Cancellations.Enqueue(subscriptionId);
        return Task.CompletedTask;
    }

    public ConcurrentQueue<string> ImmediateCancellations { get; } = new();

    public Task CancelNowAsync(string subscriptionId, CancellationToken cancellationToken)
    {
        ImmediateCancellations.Enqueue(subscriptionId);
        return Task.CompletedTask;
    }
}

/// <summary>
/// The API booted in SaaS mode with Stripe "configured" against <see cref="FakeStripeGateway"/>,
/// plus the three things billing tests keep needing: a signed webhook, a drained outbox,
/// and an organization with an Owner.
/// </summary>
public sealed class BillingTestHost : IAsyncDisposable
{
    public const string WebhookSecret = "whsec_test_aictiq_billing_0123456789";
    public const string StarterHuman = "price_starter_human";
    public const string StarterAgent = "price_starter_agent";
    public const string TeamHuman = "price_team_human";

    /// <summary>The flat hosted line, and the founding offer's discounted one.</summary>
    public const string HostedOrganization = "price_hosted_organization";
    public const string HostedFounding = "price_hosted_founding";

    private BillingTestHost(ApiTestContext context, FakeStripeGateway stripe)
    {
        Context = context;
        Stripe = stripe;
    }

    public ApiTestContext Context { get; }
    public FakeStripeGateway Stripe { get; }

    /// <param name="mode">Billing:Mode.</param>
    /// <param name="stripeConfigured">False leaves Stripe:SecretKey and Stripe:WebhookSecret unset.</param>
    public static async Task<BillingTestHost> CreateAsync(
        PostgresFixture postgres, GarageFixture garage, string dbPrefix,
        string mode = "saas", bool stripeConfigured = true, Action<IDictionary<string, string?>>? configure = null)
    {
        var stripe = new FakeStripeGateway();
        var context = await ApiTestContext.CreateAsync(postgres, garage, dbPrefix,
            settings =>
            {
                settings["Billing:Mode"] = mode;
                if (stripeConfigured)
                {
                    settings["Stripe:SecretKey"] = "sk_test_never_used_by_the_fake";
                    settings["Stripe:WebhookSecret"] = WebhookSecret;
                }
                settings["Stripe:Prices:starter_human"] = StarterHuman;
                settings["Stripe:Prices:starter_agent"] = StarterAgent;
                settings["Stripe:Prices:team_human"] = TeamHuman;
                settings["Stripe:Prices:hosted_organization"] = HostedOrganization;
                settings["Stripe:Prices:hosted_founding"] = HostedFounding;
                configure?.Invoke(settings);
            },
            services =>
            {
                services.RemoveAll<IStripeGateway>();
                services.AddSingleton<IStripeGateway>(stripe);
            });
        return new BillingTestHost(context, stripe);
    }

    public async Task<(HttpClient Owner, Guid OrganizationId, string OwnerId)> CreateOrganizationAsync(
        string slug, CancellationToken ct)
    {
        var owner = await Context.RegisterAsync($"owner-{slug}@test.local", "Olive", "Owner");
        var client = Context.ClientFor(owner);
        var response = await client.PostAsJsonAsync("/api/v1/orgs",
            new CreateOrganizationRequest(slug, slug, null, null), ApiTestContext.Json, ct);
        response.EnsureSuccessStatusCode();
        var organization = (await response.Content.ReadFromJsonAsync<OrganizationView>(ApiTestContext.Json, ct))!;
        return (client, organization.Id, owner.User.Id);
    }

    /// <summary>Stripe's signature scheme, written out rather than borrowed from the SDK: <c>t=…,v1=HMAC-SHA256(t + "." + payload)</c>.</summary>
    public static string Sign(string payload, string secret = WebhookSecret, DateTimeOffset? at = null)
    {
        var timestamp = (at ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        var signature = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{payload}"));
        return $"t={timestamp},v1={Convert.ToHexStringLower(signature)}";
    }

    /// <summary>Exactly what Stripe sends: anonymous, no CSRF header, raw JSON body.</summary>
    public async Task<HttpResponseMessage> PostWebhookAsync(string payload, CancellationToken ct, string? signature = null)
    {
        using var client = Context.Anonymous();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/stripe")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Stripe-Signature", signature ?? Sign(payload));
        return await client.SendAsync(request, ct);
    }

    /// <summary>
    /// Runs the Workers' outbox consumer over the API's own container - the handlers Tenancy
    /// and Billing register with their modules are the ones Workers run.
    /// </summary>
    public async Task DrainOutboxAsync(CancellationToken ct)
    {
        var services = Context.Factory.Services;
        var processor = new OutboxProcessor(
            services.GetRequiredService<NpgsqlDataSource>(),
            services.GetRequiredService<IServiceScopeFactory>(),
            new IntegrationEventTypeRegistry([typeof(OrganizationBillingChanged).Assembly, typeof(TenancyDbContext).Assembly]),
            NullLogger<OutboxProcessor>.Instance);
        while (await processor.ProcessPendingAsync(ct) > 0 && await PendingAsync(ct) > 0)
        {
        }

        // Events from modules outside this registry (WorkItems, Wiki…) are expected to stay
        // undeliverable here; a failure in a handler under test is not, and must say why.
        await using var connection = new NpgsqlConnection(Context.ConnectionString);
        await connection.OpenAsync(ct);
        await using var failed = new NpgsqlCommand(
            "SELECT type || ': ' || last_error FROM shared.outbox_messages WHERE processed_at IS NULL AND last_error NOT LIKE 'Unknown event type%'",
            connection);
        var errors = new List<string>();
        await using (var reader = await failed.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct)) errors.Add(reader.GetString(0));
        }
        if (errors.Count > 0) throw new InvalidOperationException("Outbox handlers failed: " + string.Join(" | ", errors));
    }

    public async Task<long> ScalarAsync(string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(Context.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
    }

    public async Task ExecuteAsync(string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(Context.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync(ct);
    }

    private Task<long> PendingAsync(CancellationToken ct) => ScalarAsync(
        "SELECT count(*) FROM shared.outbox_messages WHERE processed_at IS NULL AND dead_lettered_at IS NULL AND attempts = 0", ct);

    public ValueTask DisposeAsync() => Context.DisposeAsync();
}

/// <summary>Stripe event payloads, in the shape Stripe sends them (API 2025-03+: periods on the items).</summary>
public static class StripeEvents
{
    public static long Unix(DateTimeOffset at) => at.ToUnixTimeSeconds();

    /// <param name="founding">
    /// The <c>founding=true</c> session metadata <c>StripeGateway</c> writes when
    /// Aictiq asked for the founding price. It is Aictiq's own flag coming back signed -
    /// the browser never put it there.
    /// </param>
    public static string CheckoutCompleted(string eventId, Guid organizationId, string customer, string subscription,
        string plan, DateTimeOffset created, string paymentStatus = "paid", bool founding = false) =>
        Envelope(eventId, "checkout.session.completed", created, new
        {
            id = $"cs_test_{eventId}",
            @object = "checkout.session",
            mode = "subscription",
            client_reference_id = organizationId.ToString(),
            customer,
            subscription,
            payment_status = paymentStatus,
            metadata = new { organizationId = organizationId.ToString(), plan, founding = founding ? "true" : "false" },
        });

    public static string Subscription(string type, string eventId, string subscription, string customer, string status,
        DateTimeOffset created, Guid? organizationId = null, string plan = "starter",
        IReadOnlyList<(string Price, long Quantity)>? items = null, bool cancelAtPeriodEnd = false) =>
        Envelope(eventId, type, created, new
        {
            id = subscription,
            @object = "subscription",
            customer,
            status,
            cancel_at_period_end = cancelAtPeriodEnd,
            metadata = organizationId is null
                ? (object)new { plan }
                : new { organizationId = organizationId.ToString(), plan },
            items = new
            {
                @object = "list",
                data = (items ?? [(BillingTestHost.StarterHuman, 1)]).Select((item, index) => new
                {
                    id = $"si_{index}",
                    @object = "subscription_item",
                    price = new { id = item.Price, @object = "price" },
                    quantity = item.Quantity,
                    current_period_start = Unix(created),
                    current_period_end = Unix(created.AddMonths(1)),
                }).ToArray(),
            },
        });

    public static string PaymentFailed(string eventId, string subscription, string customer, DateTimeOffset created) =>
        Envelope(eventId, "invoice.payment_failed", created, new
        {
            id = $"in_{eventId}",
            @object = "invoice",
            customer,
            parent = new { type = "subscription_details", subscription_details = new { subscription } },
        });

    /// <summary>
    /// One collected billing period. Same envelope as <see cref="PaymentFailed"/> -
    /// Stripe's 2025 invoice carries its subscription under
    /// <c>parent.subscription_details.subscription</c> - with the type that counts a
    /// founding period instead of starting a grace period.
    /// </summary>
    public static string InvoicePaid(string eventId, string subscription, string customer, DateTimeOffset created) =>
        Envelope(eventId, "invoice.payment_succeeded", created, new
        {
            id = $"in_{eventId}",
            @object = "invoice",
            customer,
            parent = new { type = "subscription_details", subscription_details = new { subscription } },
        });

    private static string Envelope(string id, string type, DateTimeOffset created, object data) =>
        JsonSerializer.Serialize(new
        {
            id,
            @object = "event",
            api_version = "2025-08-27.basil",
            created = Unix(created),
            livemode = false,
            type,
            data = new { @object = data },
        });
}

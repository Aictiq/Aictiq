using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.Integrations;
using Aictiq.Modules.Integrations.Domain;
using Aictiq.Modules.Integrations.Workers;
using Aictiq.Modules.WorkItems.Contracts;
using Aictiq.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aictiq.IntegrationTests.Integrations;

/// <summary>
/// Webhook fan-out runs in Workers with no tenant in scope, so the organization has to
/// come from the event. Before the fix an organization-wide subscription (no project)
/// received every organization's events on the instance.
/// </summary>
[Trait("Category", "Integrations")]
public sealed class WebhookFanoutTests(PostgresFixture postgres, GarageFixture garage) : IAsyncLifetime
{
    private ApiTestContext _context = null!;
    private readonly Guid _subscriberOrg = Guid.NewGuid();
    private readonly Guid _otherOrg = Guid.NewGuid();
    private Guid _subscriptionId;

    public async ValueTask InitializeAsync()
    {
        _context = await ApiTestContext.CreateAsync(postgres, garage, "webhook_fanout");
        await using var scope = _context.Factory.Services.CreateAsyncScope();
        using var tenant = scope.ServiceProvider.GetRequiredService<AmbientCurrentTenant>().Use(_subscriberOrg);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationsDbContext>();
        var subscription = new WebhookSubscription
        {
            OrganizationId = _subscriberOrg, ProjectId = null, Url = "https://hooks.example.com/aictiq",
            SecretHash = "hash", SecretProtected = "protected", Events = ["item.created", "item.updated"],
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.WebhookSubscriptions.Add(subscription);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        _subscriptionId = subscription.Id;
    }

    public async ValueTask DisposeAsync() => await _context.DisposeAsync();

    [Fact]
    public async Task an_organization_wide_subscription_only_receives_its_own_organizations_events()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = _context.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IntegrationsDbContext>();
        var handler = new WebhookFanoutHandler(db, scope.ServiceProvider.GetRequiredService<AmbientCurrentTenant>(), TimeProvider.System);

        await handler.HandleAsync(new ItemChanged(_otherOrg, Guid.NewGuid(), Guid.NewGuid(), "OTHER-1", "someone", ["created"]), ct);
        await handler.HandleAsync(new ItemChanged(_subscriberOrg, Guid.NewGuid(), Guid.NewGuid(), "MINE-1", "someone", ["created"]), ct);

        var deliveries = await db.WebhookDeliveries.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.SubscriptionId == _subscriptionId).ToListAsync(ct);
        var delivery = Assert.Single(deliveries);
        Assert.Contains("MINE-1", delivery.Payload);
        Assert.DoesNotContain("OTHER-1", delivery.Payload);
    }

    [Fact]
    public async Task an_event_that_names_no_organization_is_delivered_to_nobody()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = _context.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IntegrationsDbContext>();
        var handler = new WebhookFanoutHandler(db, scope.ServiceProvider.GetRequiredService<AmbientCurrentTenant>(), TimeProvider.System);

        // An outbox row written before OrganizationId existed deserializes with Guid.Empty.
        await handler.HandleAsync(new ItemChanged(Guid.Empty, Guid.NewGuid(), Guid.NewGuid(), "OLD-1", "someone", ["created"]), ct);

        Assert.False(await db.WebhookDeliveries.IgnoreQueryFilters().AnyAsync(x => x.SubscriptionId == _subscriptionId, ct));
    }
}

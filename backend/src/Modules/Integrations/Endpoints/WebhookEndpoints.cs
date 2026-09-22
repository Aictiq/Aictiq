using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Aictiq.Modules.Integrations.Domain;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Tenancy;

namespace Aictiq.Modules.Integrations.Endpoints;

public sealed record CreateWebhookRequest(string? Url, IReadOnlyList<string>? Events, Guid? ProjectId);
public sealed record UpdateWebhookRequest(string? Url, IReadOnlyList<string>? Events, bool? Active, Guid? ProjectId);
public sealed record WebhookView(Guid Id, Guid? ProjectId, string Url, IReadOnlyList<string> Events, bool Active,
    int ConsecutiveFailures, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record WebhookCreatedView(WebhookView Subscription, string Secret);
public sealed record WebhookDeliveryView(Guid Id, Guid EventId, string Event, int Attempt, string Status,
    int? StatusCode, string? ResponseExcerpt, string? LastError, DateTimeOffset CreatedAt, DateTimeOffset? DeliveredAt, DateTimeOffset NextAttemptAt);

public static class WebhookEndpoints
{
    internal static readonly HashSet<string> SupportedEvents = new(StringComparer.Ordinal)
    {
        "item.created", "item.updated", "item.transitioned", "item.commented",
        "sprint.started", "sprint.completed", "wiki.page.updated", "agent.claimed"
    };

    public static IEndpointRouteBuilder MapWebhookEndpoints(this IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/orgs/{orgSlug}/webhooks").WithTags("Webhooks").RequireAuthorization();
        group.MapGet("/", List).RequireOrgRole(OrgRole.Admin).RequireScope(Scopes.Read);
        group.MapPost("/", Create).RequireOrgRole(OrgRole.Admin).RequireScope(Scopes.Admin);
        group.MapGet("/{id:guid}", Get).RequireOrgRole(OrgRole.Admin).RequireScope(Scopes.Read);
        group.MapPatch("/{id:guid}", Update).RequireOrgRole(OrgRole.Admin).RequireScope(Scopes.Admin);
        group.MapDelete("/{id:guid}", Delete).RequireOrgRole(OrgRole.Admin).RequireScope(Scopes.Admin);
        group.MapPost("/{id:guid}/test", Test).RequireOrgRole(OrgRole.Admin).RequireScope(Scopes.Admin);
        group.MapGet("/{id:guid}/deliveries", Deliveries).RequireOrgRole(OrgRole.Admin).RequireScope(Scopes.Read);
        group.MapPost("/{id:guid}/deliveries/{deliveryId:guid}/redeliver", Redeliver).RequireOrgRole(OrgRole.Admin).RequireScope(Scopes.Admin);
        return api;
    }

    private static async Task<IResult> List(IntegrationsDbContext db, CancellationToken ct) =>
        Results.Ok(await db.WebhookSubscriptions.AsNoTracking().OrderBy(x => x.CreatedAt).Select(x => View(x)).ToListAsync(ct));

    private static async Task<IResult> Get(Guid id, IntegrationsDbContext db, CancellationToken ct)
    {
        var subscription = await db.WebhookSubscriptions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        return subscription is null ? Results.NotFound() : Results.Ok(View(subscription));
    }

    private static async Task<IResult> Create(CreateWebhookRequest request, IntegrationsDbContext db, ICurrentTenant tenant,
        WebhookSecretProtector secrets, IOptions<WebhooksOptions> options, TimeProvider clock, CancellationToken ct)
    {
        var errors = await ValidateAsync(request.Url, request.Events, options.Value, ct);
        if (errors.Count > 0) return Results.ValidationProblem(errors);
        var secret = WebhookSecretProtector.CreateSecret();
        var now = clock.GetUtcNow();
        var subscription = new WebhookSubscription
        {
            OrganizationId = tenant.OrganizationId!.Value, ProjectId = request.ProjectId, Url = request.Url!.Trim(),
            Events = request.Events!.Distinct(StringComparer.Ordinal).Order().ToArray(), SecretHash = WebhookSecretProtector.Hash(secret),
            SecretProtected = secrets.Protect(secret), CreatedAt = now, UpdatedAt = now,
        };
        db.WebhookSubscriptions.Add(subscription);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/v1/orgs/{{orgSlug}}/webhooks/{subscription.Id}", new WebhookCreatedView(View(subscription), secret));
    }

    private static async Task<IResult> Update(Guid id, UpdateWebhookRequest request, IntegrationsDbContext db,
        IOptions<WebhooksOptions> options, TimeProvider clock, CancellationToken ct)
    {
        var subscription = await db.WebhookSubscriptions.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (subscription is null) return Results.NotFound();
        var errors = await ValidateAsync(request.Url ?? subscription.Url, request.Events ?? subscription.Events, options.Value, ct);
        if (errors.Count > 0) return Results.ValidationProblem(errors);
        subscription.Url = (request.Url ?? subscription.Url).Trim();
        subscription.Events = (request.Events ?? subscription.Events).Distinct(StringComparer.Ordinal).Order().ToArray();
        subscription.ProjectId = request.ProjectId ?? subscription.ProjectId;
        if (request.Active is { } active) { subscription.Active = active; if (active) subscription.ConsecutiveFailures = 0; }
        subscription.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return Results.Ok(View(subscription));
    }

    private static async Task<IResult> Delete(Guid id, IntegrationsDbContext db, CancellationToken ct)
    {
        var subscription = await db.WebhookSubscriptions.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (subscription is null) return Results.NotFound();
        db.WebhookSubscriptions.Remove(subscription);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> Test(Guid id, IntegrationsDbContext db, ICurrentTenant tenant, TimeProvider clock, CancellationToken ct)
    {
        var subscription = await db.WebhookSubscriptions.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (subscription is null) return Results.NotFound();
        var delivery = new WebhookDelivery
        {
            OrganizationId = tenant.OrganizationId!.Value, SubscriptionId = id, EventId = Guid.NewGuid(), EventName = "webhook.test",
            Payload = System.Text.Json.JsonSerializer.Serialize(new { id = Guid.NewGuid(), @event = "webhook.test", occurredAt = clock.GetUtcNow(), data = new { message = "This is a Aictiq webhook test." } }),
            CreatedAt = clock.GetUtcNow(), NextAttemptAt = clock.GetUtcNow()
        };
        db.WebhookDeliveries.Add(delivery);
        await db.SaveChangesAsync(ct);
        return Results.Accepted($"/api/v1/orgs/{{orgSlug}}/webhooks/{id}/deliveries/{delivery.Id}", DeliveryView(delivery));
    }

    private static async Task<IResult> Deliveries(Guid id, IntegrationsDbContext db, int page = 1, int pageSize = 50, CancellationToken ct = default)
    {
        if (!await db.WebhookSubscriptions.AnyAsync(x => x.Id == id, ct)) return Results.NotFound();
        var take = Math.Clamp(pageSize, 1, 100);
        return Results.Ok(await db.WebhookDeliveries.AsNoTracking().Where(x => x.SubscriptionId == id)
            .OrderByDescending(x => x.CreatedAt).Skip(Math.Max(page - 1, 0) * take).Take(take).Select(x => DeliveryView(x)).ToListAsync(ct));
    }

    private static async Task<IResult> Redeliver(Guid id, Guid deliveryId, IntegrationsDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var original = await db.WebhookDeliveries.SingleOrDefaultAsync(x => x.Id == deliveryId && x.SubscriptionId == id, ct);
        if (original is null) return Results.NotFound();
        // A new event id preserves the unique subscription/event idempotency guard while making
        // this a visible, independently traceable operator-requested delivery.
        var replay = new WebhookDelivery { OrganizationId = original.OrganizationId, SubscriptionId = id, EventId = Guid.NewGuid(), EventName = original.EventName,
            Payload = original.Payload, CreatedAt = clock.GetUtcNow(), NextAttemptAt = clock.GetUtcNow() };
        db.WebhookDeliveries.Add(replay); await db.SaveChangesAsync(ct);
        return Results.Accepted($"/api/v1/orgs/{{orgSlug}}/webhooks/{id}/deliveries/{replay.Id}", DeliveryView(replay));
    }

    private static async Task<Dictionary<string, string[]>> ValidateAsync(string? url, IReadOnlyList<string>? events, WebhooksOptions options, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (events is not { Count: > 0 } || events.Count > SupportedEvents.Count || events.Any(x => !SupportedEvents.Contains(x)))
            errors["events"] = [$"Choose one or more supported events: {string.Join(", ", SupportedEvents.Order())}."];
        if (url is null || url.Length > 2048) errors["url"] = ["A webhook URL of at most 2048 characters is required."];
        else if (await WebhookUrlGuard.ValidateAsync(url.Trim(), options.AllowPrivateNetworks, ct) is { } problem) errors["url"] = [problem];
        return errors;
    }
    private static WebhookView View(WebhookSubscription x) => new(x.Id, x.ProjectId, x.Url, x.Events, x.Active, x.ConsecutiveFailures, x.CreatedAt, x.UpdatedAt);
    private static WebhookDeliveryView DeliveryView(WebhookDelivery x) => new(x.Id, x.EventId, x.EventName, x.Attempt, x.Status, x.StatusCode, x.ResponseExcerpt, x.LastError, x.CreatedAt, x.DeliveredAt, x.NextAttemptAt);
}

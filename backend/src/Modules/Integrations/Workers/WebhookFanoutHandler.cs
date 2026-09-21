using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Aictiq.Modules.Integrations.Domain;
using Aictiq.SharedKernel.Domain;
using Aictiq.SharedKernel.Events;
using Aictiq.SharedKernel.Tenancy;

namespace Aictiq.Modules.Integrations.Workers;

/// <summary>Converts durable domain events into a per-subscription delivery queue.</summary>
public sealed class WebhookFanoutHandler(IntegrationsDbContext db, ICurrentTenant currentTenant, TimeProvider clock)
    : IDomainEventHandler<IIntegrationEvent>
{
    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web);

    public async Task HandleAsync(IIntegrationEvent domainEvent, CancellationToken cancellationToken)
    {
        var eventName = EventName(domainEvent);
        if (eventName is null) return;
        var eventId = ((DomainEvent)domainEvent).EventId;
        var projectId = ProjectId(domainEvent);
        // There is no ambient tenant while the outbox drains, so the filter is off and the
        // organization has to come from the event itself. An event that does not say whose
        // it is fans out to nobody: without this predicate an organization-wide subscription
        // (ProjectId null) would receive every other organization's events on the instance.
        var organizationId = OrganizationId(domainEvent);
        if (organizationId is null) return;
        // The deliveries are tenant rows, so the write needs the event's organization in
        // scope — and with it in scope the query filter agrees with the explicit predicate.
        using var scope = currentTenant is AmbientCurrentTenant ambient ? ambient.Use(organizationId.Value) : null;
        var subscriptions = await db.WebhookSubscriptions.Where(x => x.OrganizationId == organizationId
            && x.Active && x.Events.Contains(eventName) && (x.ProjectId == null || x.ProjectId == projectId)).ToListAsync(cancellationToken);
        if (subscriptions.Count == 0) return;
        // Serialized as its runtime type: the parameter is typed as the interface, and
        // System.Text.Json writes the declared type's members — which for IIntegrationEvent is
        // the event id and timestamp and nothing about the item.
        var body = JsonSerializer.Serialize(new
        {
            id = eventId,
            @event = eventName,
            occurredAt = domainEvent.OccurredAt,
            data = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(domainEvent, domainEvent.GetType(), PayloadJson))
        }, PayloadJson);
        var now = clock.GetUtcNow();
        foreach (var subscription in subscriptions)
            db.WebhookDeliveries.Add(new WebhookDelivery
            {
                OrganizationId = subscription.OrganizationId, SubscriptionId = subscription.Id, EventId = eventId,
                EventName = eventName, Payload = body, CreatedAt = now, NextAttemptAt = now
            });
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException) { /* Unique(subscription,event) makes an outbox replay a no-op. */ }
    }

    private static string? EventName(IIntegrationEvent value)
    {
        // Names, rather than producer CLR references, preserve the module boundary: this
        // module observes events from WorkItems/Wiki/Identity without importing them.
        var name = value.GetType().Name;
        if (name == "ItemChanged")
        {
            var fields = value.GetType().GetProperty("ChangedFields")?.GetValue(value) as IEnumerable<string>;
            return fields?.Contains("created") == true ? "item.created" : "item.updated";
        }
        return name switch
        {
            "WorkItemTransitioned" => "item.transitioned",
            "CommentAdded" => "item.commented",
            "SprintStarted" => "sprint.started",
            "SprintCompleted" => "sprint.completed",
            "WikiPageUpdated" => "wiki.page.updated",
            "AgentClaimed" => "agent.claimed",
            _ => null
        };
    }

    private static Guid? ProjectId(IIntegrationEvent value) => GuidProperty(value, "ProjectId");

    private static Guid? OrganizationId(IIntegrationEvent value) =>
        GuidProperty(value, "OrganizationId") is { } id && id != Guid.Empty ? id : null;

    private static Guid? GuidProperty(IIntegrationEvent value, string name)
    {
        var property = value.GetType().GetProperty(name);
        return property?.GetValue(value) is Guid id ? id : null;
    }
}

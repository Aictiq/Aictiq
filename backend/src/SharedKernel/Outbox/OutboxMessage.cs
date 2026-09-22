using System.Text.Json;
using Aictiq.SharedKernel.Domain;

namespace Aictiq.SharedKernel.Outbox;

public sealed class OutboxMessage
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public required string Type { get; init; }
    public required string Payload { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
    public DateTimeOffset? ProcessedAt { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }

    /// <summary>
    /// Set once the message has failed <see cref="OutboxProcessor"/>'s attempt budget.
    /// Dead-lettered rows are never retried; they are surfaced by the outbox health
    /// check and the outbox.messages.dead_lettered counter so they can't rot unnoticed.
    /// </summary>
    public DateTimeOffset? DeadLetteredAt { get; set; }

    /// <summary>
    /// The row's id is the event's own id when it carries one, so the identity a handler
    /// reads out of the payload is the same identity the outbox row has. An event raised
    /// twice therefore collides on the primary key instead of being delivered twice.
    /// </summary>
    public static OutboxMessage From(IIntegrationEvent integrationEvent)
    {
        var id = integrationEvent is DomainEvent { EventId: var eventId } && eventId != Guid.Empty
            ? eventId
            : Guid.CreateVersion7();

        return new OutboxMessage
        {
            Id = id,
            Type = integrationEvent.GetType().FullName!,
            Payload = JsonSerializer.Serialize(integrationEvent, integrationEvent.GetType()),
            OccurredAt = integrationEvent.OccurredAt
        };
    }
}

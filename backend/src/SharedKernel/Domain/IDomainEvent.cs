namespace Aictiq.SharedKernel.Domain;

/// <summary>
/// In-process notification, dispatched post-commit within the process that saved it.
/// </summary>
public interface IDomainEvent
{
    DateTimeOffset OccurredAt { get; }
}

/// <summary>
/// Durable event: written to the shared outbox in the same transaction as the data
/// change and processed exclusively by the Workers process. NOT dispatched in-process -
/// an event that needs both semantics is modeled as two events.
/// </summary>
public interface IIntegrationEvent : IDomainEvent;

public abstract record DomainEvent : IDomainEvent
{
    /// <summary>
    /// The event's identity, assigned where it is raised and carried in the outbox
    /// payload. It becomes the outbox row's primary key (<see cref="Outbox.OutboxMessage.From"/>),
    /// so a handler that must be idempotent has a stable key to write against - which is
    /// how the idempotency ends up being a database constraint rather than a check in the
    /// handler. Delivery is at least once; the key is what makes the replay harmless.
    /// </summary>
    public Guid EventId { get; init; } = Guid.CreateVersion7();

    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
}

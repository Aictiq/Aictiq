using System.ComponentModel.DataAnnotations.Schema;

namespace Aictiq.SharedKernel.Domain;

public interface IHasDomainEvents
{
    IReadOnlyCollection<IDomainEvent> DomainEvents { get; }
    void ClearDomainEvents();
}

/// <summary>
/// Marker: every entity whose mutations must land in audit.audit_log (field-level diff,
/// written by <see cref="Persistence.AuditingInterceptor"/> in the same transaction).
/// </summary>
public interface IAudited;

public abstract class EntityBase : IHasDomainEvents
{
    private readonly List<IDomainEvent> _domainEvents = [];

    // UUIDv7: time-ordered (B-tree locality at 1M+ rows) and non-guessable.
    public Guid Id { get; init; } = Guid.CreateVersion7();

    [NotMapped]
    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents;

    protected void Raise(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);

    public void ClearDomainEvents() => _domainEvents.Clear();
}

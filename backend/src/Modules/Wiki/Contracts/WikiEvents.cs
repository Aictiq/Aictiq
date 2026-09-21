using Aictiq.SharedKernel.Domain;

namespace Aictiq.Modules.Wiki.Contracts;

/// <summary>Durable page change feed used by search, links, and webhook consumers.</summary>
public sealed record WikiPageUpdated(Guid OrganizationId, Guid ProjectId, Guid PageId, string ActorId, string Change)
    : DomainEvent, IIntegrationEvent;

public sealed record WikiPageLinkedItems(Guid OrganizationId, Guid ProjectId, Guid PageId, Guid RevisionId, IReadOnlyList<Guid> ItemIds)
    : DomainEvent, IIntegrationEvent;

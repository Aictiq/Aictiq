using Aictiq.SharedKernel.Domain;

namespace Aictiq.Modules.WorkItems.Contracts;

/// <summary>Durable transition feed for analytics, notifications, and integrations.</summary>
public sealed record WorkItemTransitioned(
    Guid OrganizationId, Guid ProjectId, Guid ItemId, string Key,
    Guid FromStateId, Guid ToStateId, string ActorId, Guid? SprintId = null) : DomainEvent, IIntegrationEvent;

/// <summary>Post-commit item update pushed to project subscribers.</summary>
/// <remarks>Carries the organization because it is an integration event: webhook fan-out
/// must be able to say whose event this is without a tenant in scope.</remarks>
public sealed record ItemChanged(Guid OrganizationId, Guid ProjectId, Guid ItemId, string Key, string ActorId,
    IReadOnlyList<string> ChangedFields) : DomainEvent, IIntegrationEvent;

/// <summary>Post-commit comment notification, kept separate from its durable notification input.</summary>
public sealed record RealtimeCommentAdded(Guid ProjectId, Guid ItemId, Guid CommentId, string AuthorId) : DomainEvent;

/// <summary>Post-commit board ordering notification.</summary>
public sealed record BoardMoved(Guid ProjectId, Guid ItemId, string Key, string ActorId) : DomainEvent;

/// <summary>Post-commit sprint notification. Team/project resolution happens at the endpoint.</summary>
public sealed record SprintChanged(Guid ProjectId, Guid SprintId, Guid TeamId, string ActorId) : DomainEvent;

/// <summary>Durable notification input; recipients are resolved by Notifications/watchers.</summary>
public sealed record CommentAdded(
    Guid OrganizationId, Guid ProjectId, Guid ItemId, Guid CommentId,
    string AuthorId, IReadOnlyList<string> MentionedUserIds) : DomainEvent, IIntegrationEvent;

/// <summary>The metadata transaction has removed an attachment; blob deletion is retried by the outbox.</summary>
public sealed record AttachmentDeleted(string ObjectKey) : DomainEvent, IIntegrationEvent;

/// <summary>One durable summary for a completed bulk action, regardless of its partial failures.</summary>
public sealed record ItemsBulkUpdated(
    Guid OrganizationId, Guid ProjectId, IReadOnlyList<string> Keys, string ActorId) : DomainEvent, IIntegrationEvent;

public sealed record SprintScopeChanged(Guid OrganizationId, Guid SprintId, Guid ItemId, bool Added, decimal? Points = null, decimal? RemainingHours = null) : DomainEvent, IIntegrationEvent;
public sealed record SprintStarted(Guid OrganizationId, Guid SprintId, Guid TeamId) : DomainEvent, IIntegrationEvent;
public sealed record SprintCompleted(Guid OrganizationId, Guid SprintId, Guid TeamId) : DomainEvent, IIntegrationEvent;
public sealed record ClaimReleased(Guid OrganizationId, Guid ProjectId, Guid ItemId, string Key) : DomainEvent, IIntegrationEvent;

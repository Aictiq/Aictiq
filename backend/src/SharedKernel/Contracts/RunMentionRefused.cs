using Aictiq.SharedKernel.Domain;

namespace Aictiq.SharedKernel.Contracts;

/// <summary>
/// A comment asked an agent to work and no run could start for it - the agent was disabled,
/// the project has no playbook, the item stayed claimed by someone else. The WorkItems handler
/// answers in the comment's thread as the agent, so the person who asked is not left waiting
/// on a run that will never come. Idempotent on <see cref="DomainEvent.EventId"/> like
/// <see cref="RunFinished"/>.
/// </summary>
public sealed record RunMentionRefused(
    Guid OrganizationId,
    Guid ProjectId,
    Guid ItemId,
    Guid CommentId,
    string AgentId,
    string Reason) : DomainEvent, IIntegrationEvent;

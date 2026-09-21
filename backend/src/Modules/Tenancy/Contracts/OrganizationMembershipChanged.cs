using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Domain;

namespace Aictiq.Modules.Tenancy.Contracts;

/// <summary>
/// Integration event: someone's role in an organization changed, or they are no longer a
/// member of it.
///
/// It carries the previous role as well as the new one because the interesting consumers
/// are about the transition, not the state: revoking an agent's tokens when it loses
/// Admin, telling someone they were promoted, recomputing a seat count. A consumer that
/// only reads the new value would have to keep its own copy of the old one to tell those
/// apart.
/// </summary>
/// <param name="Role">Null when the membership was removed — including a self-leave.</param>
public sealed record OrganizationMembershipChanged(
    Guid OrganizationId, string UserId, OrgRole? Role, OrgRole PreviousRole, string ChangedBy)
    : DomainEvent, IIntegrationEvent;

using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Domain;

namespace Aictiq.Modules.Tenancy.Contracts;

/// <summary>
/// Integration event: a membership row was created — the organization's founder, an
/// accepted invitation, or a new agent. Written to the outbox in the same transaction as
/// the row, so a consumer never counts a join that rolled back.
///
/// <see cref="InvitationAccepted"/> is about the invitation; this is about the seat. Agents
/// join without an invitation, and a consumer counting seats (Billing) must hear
/// about them too. Removals and role changes are <see cref="OrganizationMembershipChanged"/>.
/// </summary>
public sealed record OrganizationMemberAdded(Guid OrganizationId, string UserId, OrgRole Role)
    : DomainEvent, IIntegrationEvent;

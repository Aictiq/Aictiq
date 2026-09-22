using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Domain;

namespace Aictiq.Modules.Tenancy.Contracts;

/// <summary>
/// Integration event: someone joined by using an invitation link. Written to the outbox in
/// the same transaction as their membership row, so a consumer never learns of a join that
/// rolled back.
///
/// Carries the address the invitation was sent to as well as the account that used it,
/// because they are allowed to differ and a consumer that only saw the account could not
/// tell that they did.
/// </summary>
public sealed record InvitationAccepted(
    Guid OrganizationId, Guid InvitationId, string Email, string UserId, OrgRole Role)
    : DomainEvent, IIntegrationEvent;

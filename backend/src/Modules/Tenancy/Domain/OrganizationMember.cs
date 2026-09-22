using Aictiq.Modules.Tenancy.Contracts;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Domain;

namespace Aictiq.Modules.Tenancy.Domain;

/// <summary>
/// Someone's membership of one organization, and the role it grants them.
///
/// This is the table every authorization decision in Aictiq ultimately reads: the tenant
/// middleware asks it whether the caller may act in the organization named by the URL,
/// and <c>RequireOrgRole</c> asks it how much they may do. It derives from
/// <see cref="TenantEntity"/> like any other org-scoped table - the deliberate exception
/// being the membership lookups themselves, which must run *before* a tenant exists and
/// therefore bypass the filter explicitly.
/// </summary>
public sealed class OrganizationMember : TenantEntity, IAudited
{
    public required string UserId { get; init; }
    public OrgRole Role { get; set; }
    public DateTimeOffset JoinedAt { get; init; }

    /// <summary>
    /// The stored half of "may this person operate the factory". Read it through
    /// <see cref="OperatesFactory"/>, never directly: for an Owner or Admin the role is the
    /// answer whatever this says. Always false for a Guest: the check constraint refuses
    /// anything else, and <see cref="ChangeRole"/> clears it on demotion.
    /// </summary>
    public bool CanOperateFactory { get; set; } = true;

    public bool OperatesFactory => MembershipRules.CanOperateFactory(Role, CanOperateFactory);

    public static OrganizationMember Create(
        Guid organizationId, string userId, OrgRole role, DateTimeOffset now, bool canOperateFactory = true)
    {
        var member = new OrganizationMember
        {
            OrganizationId = organizationId,
            UserId = userId,
            Role = role,
            JoinedAt = now,
            CanOperateFactory = role != OrgRole.Guest && canOperateFactory,
        };

        // Every way into an organization comes through here, so this is the one place a
        // seat count can learn that one was taken.
        member.Raise(new OrganizationMemberAdded(organizationId, userId, role) { OccurredAt = now });
        return member;
    }

    /// <summary>
    /// An absolute assignment, not a delta - which is why it carries no version token.
    /// Two administrators setting a role at the same moment both mean what they say and
    /// the later one wins; the invariant that actually needs protecting, that an
    /// organization keeps an Owner, is enforced by <c>tenancy.ensure_org_has_owner()</c>
    /// under a row lock, where a version check could not see the other transaction.
    /// </summary>
    public void ChangeRole(OrgRole role, string changedBy, DateTimeOffset now)
    {
        if (role == Role)
        {
            return;
        }

        var previous = Role;
        Role = role;
        if (role == OrgRole.Guest)
        {
            // In the same save as the role, or the check constraint refuses the row. Promoting
            // a Guest back does not restore it: operating the factory is granted, not inherited.
            CanOperateFactory = false;
        }
        Raise(new OrganizationMembershipChanged(OrganizationId, UserId, role, previous, changedBy)
        {
            OccurredAt = now,
        });
    }

    /// <summary>
    /// Announces the removal. Call it before deleting the row: the event is written to the
    /// outbox in the same transaction as the delete, so a consumer can never be told about
    /// a departure that then rolled back - the last-Owner trigger rolls exactly this back.
    /// </summary>
    public void MarkRemoved(string removedBy, DateTimeOffset now) =>
        Raise(new OrganizationMembershipChanged(OrganizationId, UserId, null, Role, removedBy)
        {
            OccurredAt = now,
        });
}

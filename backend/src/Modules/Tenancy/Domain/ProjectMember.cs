using Aictiq.Modules.Tenancy.Contracts;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Domain;

namespace Aictiq.Modules.Tenancy.Domain;

/// <summary>
/// Someone named explicitly on a project.
///
/// Explicit membership is only half the answer to "what may this person do here" - the
/// other half is what their organization role grants them implicitly. See
/// <see cref="ProjectAccessRules"/>, which is the only place the two are combined.
/// </summary>
public sealed class ProjectMember : TenantEntity, IAudited
{
    public Guid ProjectId { get; init; }
    public required string UserId { get; init; }
    public ProjectRole Role { get; set; }
    public DateTimeOffset AddedAt { get; init; }

    public static ProjectMember Create(
        Guid organizationId, Guid projectId, string userId, ProjectRole role,
        string addedBy, DateTimeOffset now)
    {
        var member = new ProjectMember
        {
            OrganizationId = organizationId,
            ProjectId = projectId,
            UserId = userId,
            Role = role,
            AddedAt = now,
        };

        // Raised here rather than by the caller, so that adding someone can never be the
        // one membership change nothing downstream hears about.
        member.Raise(new ProjectMembershipChanged(
            organizationId, projectId, userId, role, null, addedBy) { OccurredAt = now });

        return member;
    }

    /// <summary>
    /// An absolute assignment, like an organization role and for the same reason: two
    /// administrators setting it at the same moment both mean what they say, and there is
    /// no invariant here for a version token to protect - a project can be left with no
    /// explicit Admin, because the organization's Owners are implicitly its admins.
    /// </summary>
    public void ChangeRole(ProjectRole role, string changedBy, DateTimeOffset now)
    {
        if (role == Role)
        {
            return;
        }

        var previous = Role;
        Role = role;
        Raise(new ProjectMembershipChanged(OrganizationId, ProjectId, UserId, role, previous, changedBy)
        {
            OccurredAt = now,
        });
    }

    /// <summary>Raise before the delete, so the event and the row leave in one transaction.</summary>
    public void MarkRemoved(string removedBy, DateTimeOffset now) =>
        Raise(new ProjectMembershipChanged(OrganizationId, ProjectId, UserId, null, Role, removedBy)
        {
            OccurredAt = now,
        });
}

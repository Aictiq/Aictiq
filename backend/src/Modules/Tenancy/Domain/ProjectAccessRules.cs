using Aictiq.SharedKernel.Authorization;

namespace Aictiq.Modules.Tenancy.Domain;

/// <summary>
/// What someone may do on a project, in one place.
///
/// Two ladders meet here and the combination is easy to get subtly wrong, which is why it
/// is a pure function with its own tests rather than a condition spread across endpoints:
///
/// <list type="bullet">
/// <item>An organization <b>Owner or Admin is implicitly a project Admin</b> everywhere.
/// They administer the organization; a project they cannot enter would be a place inside
/// their own organization they could not fix.</item>
/// <item>An <b>organization-visible</b> project implicitly admits every member as a
/// Member, and every organization Guest as a project Guest.</item>
/// <item>An <b>explicit</b> project membership is added to that, never subtracted from it:
/// the effective role is whichever of the two is more privileged. Adding someone as a
/// project Guest must not be a way to demote an organization Admin.</item>
/// <item>An organization <b>Guest is never more than a project Guest</b>, whatever a
/// project membership says. Guests are typically contractors or customers, and the ceiling
/// is the whole point of the role — it must not be liftable one project at a time.</item>
/// </list>
///
/// Null means the project is invisible to them, which the authorization filters turn into
/// a <b>404</b> rather than a 403.
/// </summary>
public static class ProjectAccessRules
{
    public static ProjectRole? Effective(
        OrgRole? organizationRole, ProjectVisibility visibility, ProjectRole? explicitRole)
    {
        if (organizationRole is not { } organization)
        {
            // Not a member of the organization at all. An explicit project membership
            // cannot survive that — leaving the organization removes every way in.
            return null;
        }

        var implicitRole = organization switch
        {
            OrgRole.Owner or OrgRole.Admin => ProjectRole.Admin,
            OrgRole.Member => visibility == ProjectVisibility.Organization ? ProjectRole.Member : null,
            _ => visibility == ProjectVisibility.Organization ? ProjectRole.Guest : (ProjectRole?)null,
        };

        var effective = Best(implicitRole, explicitRole);
        if (effective is null)
        {
            return null;
        }

        return organization == OrgRole.Guest ? ProjectRole.Guest : effective;
    }

    /// <summary>Whether a project role may be handed out by someone holding <paramref name="actor"/>.</summary>
    public static bool CanAssign(ProjectRole actor, ProjectRole desired) =>
        // Project Admin is the top of this ladder and there is no last-Admin invariant to
        // protect — an organization Owner is implicitly an Admin of every project, so a
        // project can never be left unadministered.
        actor == ProjectRole.Admin && Enum.IsDefined(desired);

    /// <summary>The more privileged of two roles; null only when both are null.</summary>
    private static ProjectRole? Best(ProjectRole? left, ProjectRole? right) =>
        (left, right) switch
        {
            (null, null) => null,
            (null, { } only) => only,
            ({ } only, null) => only,
            // Roles are ranked with the most privileged at zero.
            var (a, b) => (ProjectRole)Math.Min((int)a!.Value, (int)b!.Value),
        };
}

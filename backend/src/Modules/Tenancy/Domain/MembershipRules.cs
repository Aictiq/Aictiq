using Aictiq.SharedKernel.Authorization;

namespace Aictiq.Modules.Tenancy.Domain;

/// <summary>
/// Who may change whose role, in one place.
///
/// One rule rather than a table of pairs: an administrator may act on people
/// <b>strictly below them</b>, and set roles strictly below them. That gives the two
/// behaviours the product asks for - only an Owner grants or revokes Owner, and Admins
/// manage Members and Guests - without an Admin being able to demote a peer, promote
/// themselves, or remove the Owner who appointed them. Owner is the top of the ladder and
/// therefore the exception: it may act on anyone, itself included, and the database's
/// last-Owner trigger is what stops the ladder being sawn off.
/// </summary>
public static class MembershipRules
{
    /// <summary>Whether <paramref name="actor"/> may move someone at <paramref name="target"/> to <paramref name="desired"/>.</summary>
    public static bool CanAssign(OrgRole actor, OrgRole target, OrgRole desired) =>
        actor == OrgRole.Owner || (actor == OrgRole.Admin && IsBelowAdmin(target) && IsBelowAdmin(desired));

    /// <summary>
    /// Whether <paramref name="actor"/> may invite a stranger at <paramref name="desired"/>.
    ///
    /// Same ladder as <see cref="CanAssign"/>, with one addition: <b>Owner is never an
    /// invitable role</b>, not even by an Owner. Ownership is handed over deliberately,
    /// to someone already in the organization, by a role change that the last-Owner
    /// trigger can see - not mailed to an address that may not have an account behind it
    /// yet, to be claimed a week later by whoever opens that inbox.
    /// </summary>
    public static bool CanInvite(OrgRole actor, OrgRole desired) =>
        desired != OrgRole.Owner
        && (actor == OrgRole.Owner || (actor == OrgRole.Admin && IsBelowAdmin(desired)));

    /// <summary>Whether <paramref name="actor"/> may remove someone at <paramref name="target"/>. Leaving yourself is not this question.</summary>
    public static bool CanRemove(OrgRole actor, OrgRole target) =>
        actor == OrgRole.Owner || (actor == OrgRole.Admin && IsBelowAdmin(target));

    /// <summary>
    /// Whether members' email addresses may be listed. Guests are typically contractors or
    /// customers: they can see who they are working with without leaving with the team's
    /// address book.
    /// </summary>
    public static bool CanSeeEmails(OrgRole actor) => actor.Satisfies(OrgRole.Member);

    /// <summary>
    /// Whether someone may start, cancel or watch AI work: the one answer every factory door
    /// asks. A flag rather than a rank, like a team lead. A stakeholder is a Member
    /// who can see the board, add items and comment, and does not drive the agents.
    ///
    /// Owners and Admins always operate: they register the runners and write the playbooks,
    /// so a stored <c>false</c> on them means nothing. A Guest never does: the ceiling is the
    /// point of the role, and <c>ck_organization_members_guest_not_operator</c> says so in the
    /// database. Only for a Member is the stored flag the answer.
    /// </summary>
    public static bool CanOperateFactory(OrgRole role, bool storedFlag) => role switch
    {
        OrgRole.Owner or OrgRole.Admin => true,
        OrgRole.Member => storedFlag,
        _ => false,
    };

    /// <summary>Only a Member's flag is a choice; for every other role the role decides.</summary>
    public static bool IsFactoryFlagChoosable(OrgRole role) => role == OrgRole.Member;

    /// <summary>
    /// Setting it is managing someone below Admin, so it follows the same ladder as
    /// <see cref="CanRemove"/>: an Owner or an Admin, on a Member.
    /// </summary>
    public static bool CanSetFactoryOperator(OrgRole actor, OrgRole target) =>
        IsFactoryFlagChoosable(target) && (actor == OrgRole.Owner || actor == OrgRole.Admin);

    private static bool IsBelowAdmin(OrgRole role) => !role.Satisfies(OrgRole.Admin);
}

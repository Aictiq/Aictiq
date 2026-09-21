using Aictiq.Modules.Tenancy.Domain;
using Aictiq.SharedKernel.Authorization;

namespace Aictiq.IntegrationTests.Tenancy;

/// <summary>
/// The role matrix, as a pure function and therefore without a database.
///
/// Two ladders meet in <see cref="ProjectAccessRules.Effective"/> and the combination is
/// where the subtle mistakes live: an implicit role that silently outranks an explicit
/// one, an explicit membership that quietly demotes an organization admin, a Guest whose
/// ceiling can be lifted one project at a time. The endpoint tests prove the wiring; this
/// proves the rule.
/// </summary>
[Trait("Category", "Tenancy")]
public sealed class ProjectAccessRulesTests
{
    [Theory]
    // An organization Owner or Admin administers every project, private ones included:
    // a place inside their own organization they could not enter would be a place they
    // could not fix.
    [InlineData(OrgRole.Owner, ProjectVisibility.Private, null, ProjectRole.Admin)]
    [InlineData(OrgRole.Admin, ProjectVisibility.Private, null, ProjectRole.Admin)]
    [InlineData(OrgRole.Owner, ProjectVisibility.Organization, null, ProjectRole.Admin)]

    // An organization-visible project admits every member, at the rank their org role implies.
    [InlineData(OrgRole.Member, ProjectVisibility.Organization, null, ProjectRole.Member)]
    [InlineData(OrgRole.Guest, ProjectVisibility.Organization, null, ProjectRole.Guest)]

    // A private one admits nobody who is not named on it.
    [InlineData(OrgRole.Member, ProjectVisibility.Private, null, null)]
    [InlineData(OrgRole.Guest, ProjectVisibility.Private, null, null)]

    // …and being named on it is what lets them in, at the role they were given.
    [InlineData(OrgRole.Member, ProjectVisibility.Private, ProjectRole.Admin, ProjectRole.Admin)]
    [InlineData(OrgRole.Member, ProjectVisibility.Private, ProjectRole.Guest, ProjectRole.Guest)]

    // The explicit role adds, never subtracts: adding an organization Admin to a project
    // as a Guest must not be a way to demote them out of the organization's own hierarchy.
    [InlineData(OrgRole.Admin, ProjectVisibility.Organization, ProjectRole.Guest, ProjectRole.Admin)]
    [InlineData(OrgRole.Member, ProjectVisibility.Organization, ProjectRole.Admin, ProjectRole.Admin)]
    [InlineData(OrgRole.Member, ProjectVisibility.Organization, ProjectRole.Guest, ProjectRole.Member)]

    // An organization Guest is a guest everywhere. The ceiling is the point of the role,
    // so it must not be liftable one project at a time.
    [InlineData(OrgRole.Guest, ProjectVisibility.Organization, ProjectRole.Admin, ProjectRole.Guest)]
    [InlineData(OrgRole.Guest, ProjectVisibility.Private, ProjectRole.Admin, ProjectRole.Guest)]
    public void the_effective_role_is_the_better_of_the_two_ladders(
        OrgRole organizationRole, ProjectVisibility visibility, ProjectRole? explicitRole, ProjectRole? expected)
    {
        Assert.Equal(expected, ProjectAccessRules.Effective(organizationRole, visibility, explicitRole));
    }

    [Theory]
    [InlineData(ProjectVisibility.Organization)]
    [InlineData(ProjectVisibility.Private)]
    public void leaving_the_organization_closes_every_project_in_it(ProjectVisibility visibility)
    {
        // Even an explicit project membership: the row may outlive the organization
        // membership by a moment, and it must not be a way back in.
        Assert.Null(ProjectAccessRules.Effective(null, visibility, ProjectRole.Admin));
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Aictiq.Modules.Tenancy.Domain;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Tenancy;

namespace Aictiq.Modules.Tenancy.Access;

/// <summary>
/// Membership and role answers for the authorization filters.
///
/// Everything here is cached, and that is a correctness concern rather than a performance
/// one: <c>TenantResolutionMiddleware</c> and <c>RequireProjectRole</c> read these on
/// every organization- and project-scoped request, so a stale entry is the difference
/// between revoked and not. Writers invalidate through <see cref="TenancyCache"/>, which
/// NOTIFYs every instance; the five-minute expiry is only the backstop for a lost message.
/// </summary>
public sealed class TenancyProjectAccess(TenancyDbContext db, HybridCache cache, AmbientCurrentTenant tenant) : IProjectAccess
{
    /// <summary>
    /// Runs a cached lookup with the tenant scoped to the organization it is about.
    ///
    /// HybridCache runs a factory off the caller's execution context, and the caller's
    /// identity is an AsyncLocal (<c>HttpContext</c>): inside the factory the RLS session
    /// sees no user. A caller that already established this tenant does not notice; one
    /// that has none - the realtime hub, <c>/me/tokens</c> - reads zero rows and caches
    /// "not a member" for five minutes, 404ing every route in the organization. Each lookup
    /// here names the organization, the user or the project in its own predicate, so this
    /// is what <c>TenantResolutionMiddleware</c> does before its membership check and
    /// widens nothing.
    /// </summary>
    private async Task<T> InOrganizationAsync<T>(Guid organizationId, Func<Task<T>> lookup)
    {
        using (tenant.Use(organizationId))
        {
            return await lookup();
        }
    }

    /// <summary>The project row as the role rules need it, without a second query.</summary>
    private sealed record ProjectFacts(Guid Id, Guid OrganizationId, string Key, string Name,
        ProjectVisibility Visibility, bool IsArchived);

    public async Task<OrgRole?> GetOrgRoleAsync(
        string userId, Guid organizationId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(userId))
        {
            return null;
        }

        // Cached as a nullable so "not a member" is remembered too. A probe for an
        // organization someone does not belong to is the cheap attack; making it the one
        // request that always hits the database would be an odd choice.
        return await InOrganizationAsync(organizationId, async () => await cache.GetOrCreateAsync(
            TenancyCache.MemberRoleKey(userId, organizationId),
            (db, userId, organizationId),
            static async (state, ct) => await state.db.Members
                // Deliberate, and one of very few in the codebase: this query *decides*
                // the tenant, so it necessarily runs before one is in scope. With the
                // filter applied it would match nothing and every member would be told
                // they are not one.
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(m => m.UserId == state.userId
                    && m.OrganizationId == state.organizationId)
                .Select(m => (OrgRole?)m.Role)
                .FirstOrDefaultAsync(ct),
            TenancyCache.Entry,
            tags: [TenancyCache.OrgTag(organizationId)],
            cancellationToken: cancellationToken));
    }

    public async Task<bool> CanOperateFactoryAsync(
        string userId, Guid organizationId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(userId))
        {
            return false;
        }

        // The role and the stored flag in one read, and the rule applied here rather than in
        // the query: MembershipRules is the only place that knows an Admin's stored false
        // means nothing. The same deliberate IgnoreQueryFilters as GetOrgRoleAsync, bounded
        // by the user and organization predicate.
        var member = await InOrganizationAsync(organizationId, async () => await cache.GetOrCreateAsync(
            TenancyCache.FactoryOperatorKey(userId, organizationId),
            (db, userId, organizationId),
            static async (state, ct) => await state.db.Members
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(m => m.UserId == state.userId && m.OrganizationId == state.organizationId)
                .Select(m => new FactoryFacts(m.Role, m.CanOperateFactory))
                .FirstOrDefaultAsync(ct),
            TenancyCache.Entry,
            tags: [TenancyCache.OrgTag(organizationId), TenancyCache.MembersTag(organizationId)],
            cancellationToken: cancellationToken));

        return member is not null && MembershipRules.CanOperateFactory(member.Role, member.CanOperateFactory);
    }

    private sealed record FactoryFacts(OrgRole Role, bool CanOperateFactory);

    public async Task<ProjectRef?> FindProjectAsync(
        Guid organizationId, string projectKey, CancellationToken cancellationToken = default)
    {
        var facts = await FindFactsAsync(organizationId, projectKey, cancellationToken);
        return facts is null ? null : new ProjectRef(facts.Id, facts.Key, facts.Name, facts.IsArchived);
    }

    public async Task<ProjectRef?> GetProjectAsync(
        Guid projectId, CancellationToken cancellationToken = default)
    {
        var project = await GetFactsAsync(projectId, cancellationToken);
        return project is null ? null : new ProjectRef(project.Id, project.Key, project.Name, project.IsArchived);
    }

    public async Task<ProjectRef?> FindVisibleProjectAsync(
        string userId, string projectKey, Guid? organizationId = null, string? organizationSlug = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(projectKey)) return null;

        // A hub has no organization route value, so this looks past the tenant filter - and
        // is therefore bounded by the caller's own memberships, not by the key alone. Keys
        // are unique per organization: taking "the one project with this key" instance-wide
        // let anyone create a same-key project in their own organization and make another
        // team's project unresolvable. Candidates are only organizations the caller is in;
        // each is then held to the normal effective-role rule.
        var key = projectKey.Trim().ToUpperInvariant();
        var slug = string.IsNullOrWhiteSpace(organizationSlug) ? null : organizationSlug.Trim().ToLowerInvariant();
        var candidates = await db.Projects.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.Key == key && p.ArchivedAt == null
                && (organizationId == null || p.OrganizationId == organizationId)
                && (slug == null || db.Organizations.Any(o => o.Id == p.OrganizationId && o.Slug == slug))
                && db.Members.IgnoreQueryFilters().Any(m => m.OrganizationId == p.OrganizationId && m.UserId == userId))
            .Select(p => new ProjectFacts(p.Id, p.OrganizationId, p.Key, p.Name, p.Visibility, p.ArchivedAt != null))
            .ToListAsync(cancellationToken);

        ProjectRef? found = null;
        foreach (var project in candidates)
        {
            var organizationRole = await GetOrgRoleAsync(userId, project.OrganizationId, cancellationToken);
            var explicitRole = await db.ProjectMembers.IgnoreQueryFilters().AsNoTracking()
                .Where(member => member.ProjectId == project.Id && member.UserId == userId)
                .Select(member => (ProjectRole?)member.Role)
                .FirstOrDefaultAsync(cancellationToken);
            if (ProjectAccessRules.Effective(organizationRole, project.Visibility, explicitRole) is null) continue;
            // Two visible projects share the key: refuse rather than pick a tenant.
            if (found is not null) return null;
            found = new ProjectRef(project.Id, project.Key, project.Name, project.IsArchived);
        }
        return found;
    }

    public async Task<ProjectRole?> GetProjectRoleAsync(
        string userId, Guid projectId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(userId))
        {
            return null;
        }

        var project = await GetFactsAsync(projectId, cancellationToken);
        if (project is null)
        {
            return null;
        }

        var organizationRole = await GetOrgRoleAsync(userId, project.OrganizationId, cancellationToken);

        var explicitRole = await InOrganizationAsync(project.OrganizationId, async () => await cache.GetOrCreateAsync(
            TenancyCache.ProjectRoleKey(userId, projectId),
            (db, userId, projectId),
            static async (state, ct) => await state.db.ProjectMembers
                .AsNoTracking()
                .Where(m => m.ProjectId == state.projectId && m.UserId == state.userId)
                .Select(m => (ProjectRole?)m.Role)
                .FirstOrDefaultAsync(ct),
            TenancyCache.Entry,
            // Three tags because three different writes change the answer: adding or
            // removing someone here, changing the project's visibility, and changing the
            // caller's organization role - which is what grants the implicit half.
            tags:
            [
                TenancyCache.ProjectMembersTag(projectId),
                TenancyCache.ProjectTag(projectId),
                TenancyCache.MembersTag(project.OrganizationId),
            ],
            cancellationToken: cancellationToken));

        return ProjectAccessRules.Effective(organizationRole, project.Visibility, explicitRole);
    }

    public async Task<IReadOnlyList<Guid>> ListVisibleProjectIdsAsync(
        string userId, Guid organizationId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(userId))
        {
            return [];
        }

        var organizationRole = await GetOrgRoleAsync(userId, organizationId, cancellationToken);
        if (organizationRole is null)
        {
            return [];
        }

        // Two reads and then the same rule the single-project check uses, rather than a
        // second expression of it: `ProjectAccessRules.Effective` is where the two ladders
        // meet, and a query that re-encoded them here would drift from it.
        var projects = await db.Projects.AsNoTracking()
            .Where(p => p.OrganizationId == organizationId)
            .Select(p => new { p.Id, p.Visibility })
            .ToListAsync(cancellationToken);
        var explicitRoles = await db.ProjectMembers.AsNoTracking()
            .Where(m => m.UserId == userId && db.Projects.Any(p => p.Id == m.ProjectId && p.OrganizationId == organizationId))
            .ToDictionaryAsync(m => m.ProjectId, m => (ProjectRole?)m.Role, cancellationToken);

        return [.. projects
            .Where(p => ProjectAccessRules.Effective(organizationRole, p.Visibility, explicitRoles.GetValueOrDefault(p.Id)) is not null)
            .Select(p => p.Id)];
    }

    public async Task<IReadOnlyList<string>> ListProjectMemberIdsAsync(
        Guid projectId, CancellationToken cancellationToken = default)
    {
        var project = await GetFactsAsync(projectId, cancellationToken);
        if (project is null)
        {
            return [];
        }

        // Everyone who can *see* the project, not everyone named on it: an
        // organization-visible project is visible to the whole organization, and a mention
        // picker that offered only the explicit members would hide most of the team.
        return await InOrganizationAsync(project.OrganizationId, async () => await cache.GetOrCreateAsync(
            TenancyCache.ProjectAudienceKey(projectId),
            (db, project),
            static async (state, ct) =>
            {
                var explicitIds = await state.db.ProjectMembers
                    .AsNoTracking()
                    .Where(m => m.ProjectId == state.project.Id)
                    .Select(m => m.UserId)
                    .ToListAsync(ct);

                var implicitIds = state.project.Visibility == ProjectVisibility.Organization
                    ? await state.db.Members.AsNoTracking().Select(m => m.UserId).ToListAsync(ct)
                    // A private project still admits the organization's Owners and Admins.
                    : await state.db.Members.AsNoTracking()
                        .Where(m => m.Role == OrgRole.Owner || m.Role == OrgRole.Admin)
                        .Select(m => m.UserId)
                        .ToListAsync(ct);

                return (IReadOnlyList<string>)[.. explicitIds.Union(implicitIds, StringComparer.Ordinal)];
            },
            TenancyCache.Entry,
            tags:
            [
                TenancyCache.ProjectMembersTag(projectId),
                TenancyCache.ProjectTag(projectId),
                TenancyCache.MembersTag(project.OrganizationId),
            ],
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<Guid>> ListTeamIdsForUserAsync(
        Guid projectId, string userId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(userId)) return [];
        return await db.TeamMembers.AsNoTracking()
            .Where(member => member.UserId == userId
                && db.Teams.Any(team => team.Id == member.TeamId && team.ProjectId == projectId))
            .Select(member => member.TeamId)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// By key, which is how a URL names a project. Tagged with the organization rather
    /// than the project because the entry that says <em>no such key</em> has no project id
    /// to be tagged with - and that negative answer is exactly the one a create must evict.
    /// </summary>
    private async Task<ProjectFacts?> FindFactsAsync(
        Guid organizationId, string projectKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(projectKey) || projectKey.Length > Project.MaxKeyLength)
        {
            return null;
        }

        var key = projectKey.ToUpperInvariant();

        return await cache.GetOrCreateAsync(
            TenancyCache.ProjectByKeyKey(organizationId, key),
            (db, organizationId, key),
            static async (state, ct) => await Select(state.db.Projects
                    .Where(p => p.OrganizationId == state.organizationId && p.Key == state.key))
                .FirstOrDefaultAsync(ct),
            TenancyCache.Entry,
            tags: [TenancyCache.OrgTag(organizationId)],
            cancellationToken: cancellationToken);
    }

    private async Task<ProjectFacts?> GetFactsAsync(Guid projectId, CancellationToken cancellationToken) =>
        await cache.GetOrCreateAsync(
            TenancyCache.ProjectFactsKey(projectId),
            (db, projectId),
            static async (state, ct) => await Select(state.db.Projects.Where(p => p.Id == state.projectId))
                .FirstOrDefaultAsync(ct),
            TenancyCache.Entry,
            tags: [TenancyCache.ProjectTag(projectId)],
            cancellationToken: cancellationToken);

    private static IQueryable<ProjectFacts> Select(IQueryable<Project> projects) =>
        projects
            .AsNoTracking()
            .Select(p => new ProjectFacts(
                p.Id, p.OrganizationId, p.Key, p.Name, p.Visibility, p.ArchivedAt != null));
}

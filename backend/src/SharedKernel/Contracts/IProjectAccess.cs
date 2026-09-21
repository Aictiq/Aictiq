using Aictiq.SharedKernel.Authorization;

namespace Aictiq.SharedKernel.Contracts;

/// <summary>
/// Membership and role questions, answered by the Tenancy module.
///
/// Every answer is "null means not a member", which the authorization filters turn into a
/// **404** rather than a 403: telling someone they lack permission on a project also tells
/// them the project exists.
/// </summary>
/// <summary>
/// Just enough of a project to authorize a request against it.
///
/// <paramref name="IsArchived"/> travels with the identity rather than being fetched
/// separately because every write endpoint under a project has to ask both questions —
/// may this caller act here, and is this project still writable — and asking them in two
/// round trips would put a second query on every request in the product.
/// </summary>
public sealed record ProjectRef(Guid Id, string Key, string Name, bool IsArchived);

public interface IProjectAccess
{
    Task<OrgRole?> GetOrgRoleAsync(
        string userId, Guid organizationId, CancellationToken cancellationToken = default);

    /// <summary>Resolves a project by its key within the organization. Null if it does not exist.</summary>
    Task<ProjectRef?> FindProjectAsync(
        Guid organizationId, string projectKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a project by id, with no caller to check membership against — for
    /// background work that already knows the project (an automation rule fires inside
    /// its own project) and only needs <see cref="ProjectRef.IsArchived"/> to decide
    /// whether a write may proceed, the way <c>RequireProjectWritable</c> does for a
    /// request. Not a substitute for a role check: nothing here says who may act.
    /// </summary>
    Task<ProjectRef?> GetProjectAsync(
        Guid projectId, CancellationToken cancellationToken = default) =>
        Task.FromResult<ProjectRef?>(null);

    /// <summary>
    /// Resolves the one project with this key the caller can see. This is intentionally
    /// user-scoped for transports, such as SignalR, that do not have an org-slug route.
    ///
    /// Only projects the caller can see are candidates: a key is unique per organization, not
    /// per instance, so anyone who creates a project with the same key in their own
    /// organization must not be able to make another team's project unresolvable. When the
    /// caller can see more than one, the answer is null rather than a guess —
    /// <paramref name="organizationSlug"/> is how a client that knows its organization says
    /// which. <paramref name="organizationId"/> narrows to one organization too, and is how a
    /// token bound to an organization stays inside it.
    /// </summary>
    Task<ProjectRef?> FindVisibleProjectAsync(
        string userId, string projectKey, Guid? organizationId = null, string? organizationSlug = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The caller's <b>effective</b> role on a project: the best of what their
    /// organization role grants them implicitly and what an explicit project membership
    /// grants them. Null means they cannot see the project at all, which the filters turn
    /// into a 404.
    /// </summary>
    Task<ProjectRole?> GetProjectRoleAsync(
        string userId, Guid projectId, CancellationToken cancellationToken = default);

    /// <summary>Everyone who can see the project — for mention pickers and notification fan-out.</summary>
    Task<IReadOnlyList<string>> ListProjectMemberIdsAsync(
        Guid projectId, CancellationToken cancellationToken = default);

    /// <summary>Teams in a project that contain this user.  Consumers use this rather
    /// than reaching into Tenancy when a resource grants access to a team.</summary>
    Task<IReadOnlyList<Guid>> ListTeamIdsForUserAsync(
        Guid projectId, string userId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Guid>>([]);

    /// <summary>
    /// Every project in this organization the caller can see — the same rule as
    /// <see cref="GetProjectRoleAsync"/>, asked for all of them at once.
    ///
    /// An organization-wide feed has to be bounded by visibility before it is paged, and
    /// asking project by project would be one query per project on a page-one request. An
    /// empty list means "nothing", never "everything": a caller that forgets to check
    /// therefore shows an empty feed rather than another team's work.
    /// </summary>
    Task<IReadOnlyList<Guid>> ListVisibleProjectIdsAsync(
        string userId, Guid organizationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether the caller may operate the AI software factory in this organization: start,
    /// cancel and watch runs. Owners and Admins always may, Guests never, and a
    /// Member only when their membership says so. A stakeholder is a Member who may not.
    ///
    /// Fails closed. The default here, a missing module and a non-member all answer false:
    /// "nobody said you could" is not permission to spend the organization's agents.
    /// </summary>
    Task<bool> CanOperateFactoryAsync(
        string userId, Guid organizationId, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);
}

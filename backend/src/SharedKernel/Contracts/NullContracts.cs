using Aictiq.SharedKernel.Authorization;

namespace Aictiq.SharedKernel.Contracts;

/// <summary>
/// The fallback when no module has claimed these contracts. The Tenancy module replaces
/// both (<c>AddTenancyModule</c>), so in the API and Workers these are never resolved -
/// they are what a host that forgets to register a module gets.
///
/// They answer "no such organization" and "not a member" rather than "yes to everything".
/// A permissive stub would make every authorization filter pass, so a host missing the
/// module would not fail loudly - it would quietly admit everyone.
/// </summary>
public sealed class NullOrganizationLookup : IOrganizationLookup
{
    public Task<OrganizationRef?> FindBySlugAsync(string slug, CancellationToken cancellationToken = default) =>
        Task.FromResult<OrganizationRef?>(null);

    public Task<OrganizationRef?> FindByIdAsync(Guid organizationId, CancellationToken cancellationToken = default) =>
        Task.FromResult<OrganizationRef?>(null);
}

/// <inheritdoc cref="NullOrganizationLookup"/>
public sealed class NullProjectAccess : IProjectAccess
{
    public Task<OrgRole?> GetOrgRoleAsync(
        string userId, Guid organizationId, CancellationToken cancellationToken = default) =>
        Task.FromResult<OrgRole?>(null);

    public Task<ProjectRef?> FindProjectAsync(
        Guid organizationId, string projectKey, CancellationToken cancellationToken = default) =>
        Task.FromResult<ProjectRef?>(null);

    public Task<ProjectRef?> FindVisibleProjectAsync(
        string userId, string projectKey, Guid? organizationId = null, string? organizationSlug = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<ProjectRef?>(null);

    public Task<ProjectRole?> GetProjectRoleAsync(
        string userId, Guid projectId, CancellationToken cancellationToken = default) =>
        Task.FromResult<ProjectRole?>(null);

    public Task<IReadOnlyList<string>> ListProjectMemberIdsAsync(
        Guid projectId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    public Task<IReadOnlyList<Guid>> ListTeamIdsForUserAsync(
        Guid projectId, string userId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Guid>>([]);

    public Task<IReadOnlyList<Guid>> ListVisibleProjectIdsAsync(
        string userId, Guid organizationId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Guid>>([]);

    public Task<bool> CanOperateFactoryAsync(
        string userId, Guid organizationId, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);
}

/// <inheritdoc cref="NullOrganizationLookup"/>
public sealed class NullUserDirectory : IUserDirectory
{
    public Task<IReadOnlyDictionary<string, UserSummary>> GetAsync(
        IReadOnlyCollection<string> userIds, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<string, UserSummary>>(
            new Dictionary<string, UserSummary>());

    public Task<UserDirectoryPage> SearchAsync(
        IReadOnlyCollection<string> userIds, string? search, int skip, int take,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new UserDirectoryPage([], 0));

    public Task<IReadOnlySet<string>> FilterAgentsAsync(
        IReadOnlyCollection<string> userIds, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());

    public Task<UserContact?> FindByEmailAsync(
        string email, CancellationToken cancellationToken = default) =>
        Task.FromResult<UserContact?>(null);

    public Task<IReadOnlyDictionary<string, UserDeliveryProfile>> GetDeliveryProfilesAsync(
        IReadOnlyCollection<string> userIds, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<string, UserDeliveryProfile>>(
            new Dictionary<string, UserDeliveryProfile>());
}

/// <summary>No Wiki module means no page content; callers must fail closed.</summary>
public sealed class NullWikiPageContent : IWikiPageContent
{
    public Task<WikiPageContent?> GetMarkdownAsync(
        Guid pageId, Guid projectId, CancellationToken cancellationToken = default) =>
        Task.FromResult<WikiPageContent?>(null);
}

/// <summary>No Wiki module means Factory cannot create its starter page.</summary>
public sealed class NullWikiPageCreator : IWikiPageCreator
{
    public Task<Guid?> CreateStarterPageAsync(
        Guid organizationId, Guid projectId, string actorId, string markdown,
        CancellationToken cancellationToken = default) => Task.FromResult<Guid?>(null);
}

/// <summary>No WorkItems module means no workflow state can be accepted.</summary>
public sealed class NullProjectWorkflowAccess : IProjectWorkflowAccess
{
    public Task<bool> StatesBelongToProjectAsync(
        Guid projectId, IReadOnlyCollection<Guid> stateIds,
        CancellationToken cancellationToken = default) => Task.FromResult(stateIds.Count == 0);
}

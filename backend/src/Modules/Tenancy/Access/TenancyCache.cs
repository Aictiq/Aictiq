using Microsoft.Extensions.Caching.Hybrid;
using Npgsql;

namespace Aictiq.Modules.Tenancy.Access;

/// <summary>
/// Cache keys, tags and invalidation for the tenancy lookups.
///
/// These are on the hot path in a way little else is: <c>TenantResolutionMiddleware</c>
/// resolves the slug and checks membership on <b>every</b> organization-scoped request,
/// so uncached that is two queries before an endpoint runs. Caching them is also the one
/// place where staleness has a security flavour - a removed member whose role is still
/// cached keeps their access - which is why invalidation is a NOTIFY every API instance
/// hears, not a local eviction that only helps the process that made the change.
/// </summary>
public static class TenancyCache
{
    /// <summary>Postgres LISTEN/NOTIFY channel. See <see cref="ChangeSignal"/> for the payload.</summary>
    public const string Channel = "tenancy_changed";

    /// <summary>Long enough to matter, short enough that a missed NOTIFY self-heals.</summary>
    public static readonly HybridCacheEntryOptions Entry = new() { Expiration = TimeSpan.FromMinutes(5) };

    public static string OrgTag(Guid organizationId) => $"org:{organizationId}";

    /// <summary>
    /// Slug lookups get their own tag: the entry that says "no such organization" has no
    /// id to be tagged with, and that negative answer is exactly the one a create must
    /// evict.
    /// </summary>
    public static string SlugTag(string slug) => $"org-slug:{slug}";

    /// <summary>
    /// The membership of one organization, as opposed to answers about the organization
    /// itself. Separate from <see cref="OrgTag"/> so a roster change does not have to
    /// evict the slug and settings lookups that did not change - and so that whatever
    /// caches the roster later is invalidated by the same writes that invalidate the
    /// roles today.
    /// </summary>
    public static string MembersTag(Guid organizationId) => $"org:{organizationId}:members";

    public static string OrgBySlugKey(string slug) => $"tenancy:org:slug:{slug}";

    public static string OrgByIdKey(Guid organizationId) => $"tenancy:org:id:{organizationId}";

    public static string MemberRoleKey(string userId, Guid organizationId) =>
        $"tenancy:member:{organizationId}:{userId}";

    /// <summary>Evicted by the same writes as the role: every member write invalidates the organization.</summary>
    public static string FactoryOperatorKey(string userId, Guid organizationId) =>
        $"tenancy:factory-operator:{organizationId}:{userId}";

    /// <summary>The project row itself: its id, name and whether it is archived.</summary>
    public static string ProjectTag(Guid projectId) => $"project:{projectId}";

    /// <summary>
    /// Who is explicitly on a project. Separate from <see cref="ProjectTag"/> so adding
    /// someone does not evict the key lookup that every request under the project makes.
    /// </summary>
    public static string ProjectMembersTag(Guid projectId) => $"project:{projectId}:members";

    public static string ProjectByKeyKey(Guid organizationId, string projectKey) =>
        $"tenancy:project:{organizationId}:{projectKey}";

    public static string ProjectRoleKey(string userId, Guid projectId) =>
        $"tenancy:project-role:{projectId}:{userId}";

    public static string ProjectFactsKey(Guid projectId) => $"tenancy:project:{projectId}";

    /// <summary>Everyone who can see the project, explicit members and implicit alike.</summary>
    public static string ProjectAudienceKey(Guid projectId) => $"tenancy:project-audience:{projectId}";

    /// <summary>
    /// The NOTIFY payload, formatted and parsed in one place so the writer and the six
    /// listeners cannot drift apart.
    ///
    /// <paramref name="ProjectId"/> and <paramref name="ProjectKey"/> are set only when a
    /// project changed; an organization-level change leaves them empty and evicts the
    /// organization's tags alone.
    /// </summary>
    public sealed record ChangeSignal(Guid OrganizationId, string Slug, Guid? ProjectId, string? ProjectKey)
    {
        public string Format() =>
            $"{OrganizationId}|{Slug}|{ProjectId?.ToString() ?? ""}|{ProjectKey ?? ""}";

        public static ChangeSignal? TryParse(string payload)
        {
            var parts = payload.Split('|');
            if (parts.Length < 2 || !Guid.TryParse(parts[0], out var organizationId))
            {
                return null;
            }

            var projectId = parts.Length > 2 && Guid.TryParse(parts[2], out var parsed) ? parsed : (Guid?)null;
            var projectKey = parts.Length > 3 && parts[3].Length > 0 ? parts[3] : null;
            return new ChangeSignal(organizationId, parts[1], projectId, projectKey);
        }
    }

    /// <summary>
    /// Announces that an organization changed. Called after the transaction commits - a
    /// NOTIFY sent inside one is delivered on commit anyway, but this runs on the
    /// connection pool rather than the entity transaction, so it must not run before.
    /// </summary>
    public static async Task NotifyChangedAsync(
        NpgsqlDataSource dataSource, ChangeSignal signal, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var notify = new NpgsqlCommand($"SELECT pg_notify('{Channel}', @payload)", connection);
        notify.Parameters.AddWithValue("payload", signal.Format());
        await notify.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Forgets an organization here and everywhere else. The pair is always used together
    /// - evicting only locally leaves every other instance serving the membership it
    /// remembers, which for a removal is the difference between revoked and not.
    /// </summary>
    public static Task InvalidateAsync(
        HybridCache cache, NpgsqlDataSource dataSource, Guid organizationId, string slug,
        CancellationToken cancellationToken = default) =>
        InvalidateAsync(cache, dataSource, new ChangeSignal(organizationId, slug, null, null), cancellationToken);

    /// <summary>
    /// Forgets a project here and everywhere else. The organization's slug travels with it
    /// because the payload always carries one - the listener needs no second shape.
    /// </summary>
    public static Task InvalidateProjectAsync(
        HybridCache cache, NpgsqlDataSource dataSource, Guid organizationId, string slug,
        Guid projectId, string projectKey, CancellationToken cancellationToken = default) =>
        InvalidateAsync(
            cache, dataSource, new ChangeSignal(organizationId, slug, projectId, projectKey), cancellationToken);

    public static async Task InvalidateAsync(
        HybridCache cache, NpgsqlDataSource dataSource, ChangeSignal signal,
        CancellationToken cancellationToken = default)
    {
        await EvictAsync(cache, signal, cancellationToken);
        await NotifyChangedAsync(dataSource, signal, cancellationToken);
    }

    /// <summary>Drops every cached answer about one organization in this process.</summary>
    public static Task EvictAsync(
        HybridCache cache, Guid organizationId, string slug, CancellationToken cancellationToken = default) =>
        EvictAsync(cache, new ChangeSignal(organizationId, slug, null, null), cancellationToken);

    public static async Task EvictAsync(
        HybridCache cache, ChangeSignal signal, CancellationToken cancellationToken = default)
    {
        List<string> tags =
            [OrgTag(signal.OrganizationId), SlugTag(signal.Slug), MembersTag(signal.OrganizationId)];

        if (signal.ProjectId is { } projectId)
        {
            tags.Add(ProjectTag(projectId));
            tags.Add(ProjectMembersTag(projectId));
        }

        await cache.RemoveByTagAsync(tags, cancellationToken);
    }
}

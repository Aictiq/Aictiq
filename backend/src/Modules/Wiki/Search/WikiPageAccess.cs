using Aictiq.Modules.Tenancy.Access;
using Aictiq.Modules.Wiki.Domain;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;

namespace Aictiq.Modules.Wiki.Search;

/// <summary>Resolves the closest explicit rule set once for the complete project tree.</summary>
public sealed class WikiPageAccess(WikiDbContext db, IProjectAccess projects, HybridCache cache, ICurrentTenant tenant) : IWikiPageAccess
{
    private sealed record PageAccess(IReadOnlySet<Guid> Readable, IReadOnlySet<Guid> Writable);
    public sealed record Snapshot(Guid[] Readable, Guid[] Writable);

    public async Task<bool> CanReadAsync(Guid pageId, Guid projectId, string userId, CancellationToken cancellationToken = default) =>
        (await AccessAsync(projectId, userId, cancellationToken)).Readable.Contains(pageId);

    public async Task<bool> CanWriteAsync(Guid pageId, Guid projectId, string userId, CancellationToken cancellationToken = default) =>
        (await AccessAsync(projectId, userId, cancellationToken)).Writable.Contains(pageId);

    public async Task<IReadOnlySet<Guid>> VisiblePageIdsAsync(Guid projectId, string userId, bool write,
        CancellationToken cancellationToken = default)
    {
        var access = await AccessAsync(projectId, userId, cancellationToken);
        return write ? access.Writable : access.Readable;
    }

    public Task InvalidateAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        cache.RemoveByTagAsync(PermissionsTag(projectId), cancellationToken).AsTask();

    public static string PermissionsTag(Guid projectId) => $"wiki:{projectId}:permissions";

    private async ValueTask<PageAccess> AccessAsync(Guid projectId, string userId, CancellationToken ct)
    {
        var snapshot = await cache.GetOrCreateAsync(
            $"wiki:access:{projectId}:{userId}",
            (self: this, projectId, userId),
            static (state, token) => new ValueTask<Snapshot>(state.self.ComputeSnapshotAsync(state.projectId, state.userId, token)),
            new HybridCacheEntryOptions { Expiration = TimeSpan.FromMinutes(1) },
            // The snapshot is built from the caller's effective project role, which an
            // organization role change moves too (an org Admin is a project Admin). Without
            // the members tag a demoted Admin kept restricted pages for the entry's lifetime.
            tags: tenant.OrganizationId is { } organizationId
                ? [PermissionsTag(projectId), TenancyCache.ProjectMembersTag(projectId), TenancyCache.ProjectTag(projectId), TenancyCache.MembersTag(organizationId)]
                : [PermissionsTag(projectId), TenancyCache.ProjectMembersTag(projectId), TenancyCache.ProjectTag(projectId)],
            cancellationToken: ct);
        return new PageAccess(snapshot.Readable.ToHashSet(), snapshot.Writable.ToHashSet());
    }

    private async Task<Snapshot> ComputeSnapshotAsync(Guid projectId, string userId, CancellationToken ct)
    {
        var access = await ComputeAsync(projectId, userId, ct);
        return new Snapshot(access.Readable.ToArray(), access.Writable.ToArray());
    }

    private async Task<PageAccess> ComputeAsync(Guid projectId, string userId, CancellationToken ct)
    {
        var role = await projects.GetProjectRoleAsync(userId, projectId, ct);
        if (role is null) return new PageAccess(new HashSet<Guid>(), new HashSet<Guid>());
        var pages = await db.Pages.AsNoTracking().Where(page => page.ProjectId == projectId)
            .Select(page => new { page.Id, page.ParentId }).ToListAsync(ct);
        if (pages.Count == 0) return new PageAccess(new HashSet<Guid>(), new HashSet<Guid>());
        if (role == ProjectRole.Admin)
        {
            var all = pages.Select(page => page.Id).ToHashSet();
            return new PageAccess(all, all);
        }
        var pageIds = pages.Select(page => page.Id).ToArray();
        var rules = await db.PagePermissions.AsNoTracking().Where(rule => pageIds.Contains(rule.PageId)).ToListAsync(ct);
        var byPage = rules.GroupBy(rule => rule.PageId).ToDictionary(group => group.Key, group => group.ToArray());
        var parents = pages.ToDictionary(page => page.Id, page => page.ParentId);
        var teams = (await projects.ListTeamIdsForUserAsync(projectId, userId, ct)).Select(id => id.ToString()).ToHashSet(StringComparer.Ordinal);
        var roleSubject = role.Value.ToString().ToLowerInvariant();
        var readable = new HashSet<Guid>(); var writable = new HashSet<Guid>();
        foreach (var page in pages)
        {
            var current = page.Id; WikiPagePermission[]? inherited = null;
            while (true)
            {
                if (byPage.TryGetValue(current, out var explicitRules)) { inherited = explicitRules; break; }
                if (parents[current] is not { } parent) break;
                current = parent;
            }
            if (inherited is null)
            {
                readable.Add(page.Id);
                if (role.Value.Satisfies(ProjectRole.Member)) writable.Add(page.Id);
                continue;
            }
            var best = inherited.Where(rule => Matches(rule, userId, teams, roleSubject)).Select(rule => rule.Access)
                .DefaultIfEmpty((WikiPermissionAccess)(-1)).Max();
            if (best >= WikiPermissionAccess.Read) readable.Add(page.Id);
            // A page rule narrows or opens a page within the project role, never above it: a
            // project Guest reads and comments, so a "write" rule naming one (or a team they
            // are on) grants read — the ceiling is the point of the role.
            if (best >= WikiPermissionAccess.Write && role.Value.Satisfies(ProjectRole.Member)) writable.Add(page.Id);
        }
        return new PageAccess(readable, writable);
    }

    private static bool Matches(WikiPagePermission rule, string userId, IReadOnlySet<string> teams, string role) => rule.SubjectKind switch
    {
        WikiPermissionSubjectKind.User => string.Equals(rule.SubjectId, userId, StringComparison.Ordinal),
        WikiPermissionSubjectKind.Team => teams.Contains(rule.SubjectId),
        WikiPermissionSubjectKind.ProjectRole => string.Equals(rule.SubjectId, role, StringComparison.Ordinal),
        _ => false,
    };
}

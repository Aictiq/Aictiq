using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Aictiq.SharedKernel.Contracts;

namespace Aictiq.Modules.Tenancy.Access;

/// <summary>
/// The real <see cref="IOrganizationLookup"/>, replacing the fail-closed stub SharedKernel
/// registers until this module exists.
///
/// Soft-deleted organizations are invisible here on purpose: this is what the tenant
/// middleware calls, so "deleted" means every organization-scoped route stops resolving
/// at once, without each endpoint remembering to check.
/// </summary>
public sealed class OrganizationLookup(TenancyDbContext db, HybridCache cache) : IOrganizationLookup
{
    public async Task<OrganizationRef?> FindBySlugAsync(string slug, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(slug))
        {
            return null;
        }

        var normalized = slug.ToLowerInvariant();

        return await cache.GetOrCreateAsync(
            TenancyCache.OrgBySlugKey(normalized),
            (db, normalized),
            static async (state, ct) => await state.db.Organizations
                .AsNoTracking()
                .Where(o => o.Slug == state.normalized)
                .Select(o => new OrganizationRef(o.Id, o.Slug, o.Name))
                .FirstOrDefaultAsync(ct),
            TenancyCache.Entry,
            tags: [TenancyCache.SlugTag(normalized)],
            cancellationToken: cancellationToken);
    }

    public async Task<OrganizationRef?> FindByIdAsync(
        Guid organizationId, CancellationToken cancellationToken = default) =>
        await cache.GetOrCreateAsync(
            TenancyCache.OrgByIdKey(organizationId),
            (db, organizationId),
            static async (state, ct) => await state.db.Organizations
                .AsNoTracking()
                .Where(o => o.Id == state.organizationId)
                .Select(o => new OrganizationRef(o.Id, o.Slug, o.Name))
                .FirstOrDefaultAsync(ct),
            TenancyCache.Entry,
            tags: [TenancyCache.OrgTag(organizationId)],
            cancellationToken: cancellationToken);
}

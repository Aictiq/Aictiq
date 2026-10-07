using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Aictiq.Modules.Tenancy.Access;

/// <summary>
/// Owners and people for the hosted free tier. Each organization is read inside its own
/// tenant scope, so the ordinary filter and RLS apply; the one read that spans tenants -
/// "which organizations does this person own" - is admitted by the owner-lookup
/// capability, and its user-id and Owner-role predicate is what keeps it narrow.
/// </summary>
public sealed class TenancyPeopleSource(TenancyDbContext db, AmbientCurrentTenant tenant, TimeProvider clock)
    : IOrganizationPeopleSource
{
    public async Task<IReadOnlyList<string>> GetOwnerIdsAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        using var scope = tenant.Use(organizationId);
        return await db.Members.AsNoTracking()
            .Where(m => m.OrganizationId == organizationId && m.Role == OrgRole.Owner)
            .Select(m => m.UserId)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>> GetOwnedOrganizationIdsAsync(string userId, CancellationToken cancellationToken = default)
    {
        using var scope = tenant.UseOwnerLookup(userId);
        return await db.Members.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.UserId == userId && m.Role == OrgRole.Owner)
            .Select(m => m.OrganizationId)
            .Distinct()
            .ToListAsync(cancellationToken);
    }

    public async Task<OrganizationPeople> GetPeopleAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        using var scope = tenant.Use(organizationId);
        var members = await db.Members.AsNoTracking()
            .Where(m => m.OrganizationId == organizationId)
            .Select(m => m.UserId)
            .ToListAsync(cancellationToken);
        var now = clock.GetUtcNow();
        var invited = await db.Invitations.AsNoTracking()
            .Where(i => i.OrganizationId == organizationId
                && i.AcceptedAt == null && i.RevokedAt == null && i.ExpiresAt > now)
            .Select(i => i.Email)
            .ToListAsync(cancellationToken);
        return new OrganizationPeople(members, invited);
    }
}

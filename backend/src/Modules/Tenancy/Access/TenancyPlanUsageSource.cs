using Aictiq.SharedKernel.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Aictiq.Modules.Tenancy.Access;

/// <summary>Billing asks Tenancy for facts, never for its EF model.</summary>
public sealed class TenancyPlanUsageSource(TenancyDbContext db) : IOrganizationPlanUsageSource
{
    public async Task<OrganizationPlanUsage?> GetAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        var plan = await db.Organizations.AsNoTracking().Where(x => x.Id == organizationId)
            .Select(x => x.Plan).SingleOrDefaultAsync(cancellationToken);
        if (plan is null) return null;
        var members = await db.Members.AsNoTracking().Where(x => x.OrganizationId == organizationId).Select(x => x.UserId).ToListAsync(cancellationToken);
        var projects = await db.Projects.AsNoTracking().CountAsync(x => x.OrganizationId == organizationId && x.ArchivedAt == null, cancellationToken);
        return new OrganizationPlanUsage(plan, members, projects);
    }
}

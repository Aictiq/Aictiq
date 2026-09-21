using Microsoft.EntityFrameworkCore;
using Aictiq.SharedKernel.Contracts;

namespace Aictiq.Modules.Tenancy.Access;

internal sealed class OrganizationTimeZoneSource(TenancyDbContext db) : IOrganizationTimeZoneSource
{
    public async Task<IReadOnlyList<OrganizationTimeZone>> ListAsync(CancellationToken cancellationToken = default) =>
        await db.Organizations.AsNoTracking()
            .OrderBy(org => org.Id).Select(org => new OrganizationTimeZone(org.Id, org.Settings.TimeZone))
            .ToListAsync(cancellationToken);
}

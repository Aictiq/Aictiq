using Aictiq.Modules.WorkItems.Domain;
using Aictiq.SharedKernel.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Aictiq.Modules.WorkItems.Access;

public sealed class WorkItemsStorageUsageSource(WorkItemsDbContext db) : IStorageUsageSource
{
    public Task<long> GetStoredBytesAsync(Guid organizationId, CancellationToken cancellationToken = default) =>
        db.Attachments.AsNoTracking().Where(x => x.OrganizationId == organizationId && x.Status == AttachmentStatus.Committed)
            .Select(x => (long?)x.SizeBytes).SumAsync(cancellationToken).ContinueWith(x => x.Result ?? 0L, cancellationToken);
}

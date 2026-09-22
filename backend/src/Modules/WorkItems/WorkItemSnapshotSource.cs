using Microsoft.EntityFrameworkCore;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Tenancy;

namespace Aictiq.Modules.WorkItems;

internal sealed class WorkItemSnapshotSource(WorkItemsDbContext db, ICurrentTenant tenant) : IWorkItemSnapshotSource
{
    public async Task<IReadOnlyList<WorkItemSnapshot>> ListAsync(Guid organizationId, Guid? afterId, int take,
        CancellationToken cancellationToken = default)
    {
        if (organizationId == Guid.Empty) return [];
        using var scope = tenant is AmbientCurrentTenant ambient ? ambient.Use(organizationId) : null;
        return await db.Items.AsNoTracking()
            .Where(item => item.RemovedAt == null && (afterId == null || item.Id.CompareTo(afterId.Value) > 0))
            .OrderBy(item => item.Id).Take(Math.Clamp(take, 1, 1000))
            .Select(item => new WorkItemSnapshot(item.Id, item.OrganizationId, item.ProjectId, item.StateId,
                item.SprintId, item.Points, item.EstimateHours, item.RemainingHours, item.CompletedHours, item.UpdatedAt))
            .ToListAsync(cancellationToken);
    }
}

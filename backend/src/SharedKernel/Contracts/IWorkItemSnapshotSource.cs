namespace Aictiq.SharedKernel.Contracts;

/// <summary>Read-only WorkItems projection used by Analytics snapshots.  Analytics never
/// reaches into the <c>work</c> schema directly; this contract is its bounded seam.</summary>
public sealed record WorkItemSnapshot(
    Guid Id, Guid OrganizationId, Guid ProjectId, Guid StateId, Guid? SprintId,
    decimal? Points, decimal? EstimateHours, decimal? RemainingHours, decimal? CompletedHours,
    DateTimeOffset UpdatedAt);

public interface IWorkItemSnapshotSource
{
    Task<IReadOnlyList<WorkItemSnapshot>> ListAsync(Guid organizationId, Guid? afterId, int take,
        CancellationToken cancellationToken = default);
}

/// <summary>Organization-local days are owned by Tenancy.  Workers use this small
/// projection instead of reading tenancy tables from Analytics.</summary>
public sealed record OrganizationTimeZone(Guid OrganizationId, string TimeZone);

public interface IOrganizationTimeZoneSource
{
    Task<IReadOnlyList<OrganizationTimeZone>> ListAsync(CancellationToken cancellationToken = default);
}

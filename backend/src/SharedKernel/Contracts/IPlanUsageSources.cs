namespace Aictiq.SharedKernel.Contracts;

/// <summary>Tenant-owned facts that Billing needs without querying another module's tables.</summary>
public interface IOrganizationPlanUsageSource
{
    Task<OrganizationPlanUsage?> GetAsync(Guid organizationId, CancellationToken cancellationToken = default);
}

public sealed record OrganizationPlanUsage(string PlanCode, IReadOnlyList<string> MemberUserIds, int ProjectCount);

/// <summary>Storage is owned by WorkItems even though its limit is owned by Billing.</summary>
public interface IStorageUsageSource
{
    Task<long> GetStoredBytesAsync(Guid organizationId, CancellationToken cancellationToken = default);
}

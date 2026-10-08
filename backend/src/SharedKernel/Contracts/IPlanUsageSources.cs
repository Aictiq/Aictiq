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

/// <summary>
/// Who owns an organization and who is in it, for the hosted free tier: its limits belong
/// to a person rather than to an organization, so Billing has to see across every
/// organization one Owner owns. Implemented by Tenancy; Billing never reads its tables.
/// </summary>
public interface IOrganizationPeopleSource
{
    Task<IReadOnlyList<string>> GetOwnerIdsAsync(Guid organizationId, CancellationToken cancellationToken = default);

    /// <summary>Every organization <paramref name="userId"/> is an Owner of.</summary>
    Task<IReadOnlyList<Guid>> GetOwnedOrganizationIdsAsync(string userId, CancellationToken cancellationToken = default);

    Task<OrganizationPeople> GetPeopleAsync(Guid organizationId, CancellationToken cancellationToken = default);
}

/// <param name="MemberUserIds">Every member, agents included; the caller tells them apart.</param>
/// <param name="PendingInvitationEmails">Normalized addresses of live invitations: not accepted, revoked or expired.</param>
public sealed record OrganizationPeople(IReadOnlyList<string> MemberUserIds, IReadOnlyList<string> PendingInvitationEmails);

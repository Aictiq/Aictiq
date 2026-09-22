namespace Aictiq.SharedKernel.Contracts;

/// <summary>Just enough of an organization to route a request to it.</summary>
public sealed record OrganizationRef(Guid Id, string Slug, string Name);

/// <summary>
/// Implemented by the Tenancy module. Lives here because the tenant middleware
/// and every other module need to resolve an organization without referencing Tenancy's
/// types - modules talk to each other through contracts in SharedKernel, never directly.
/// </summary>
public interface IOrganizationLookup
{
    /// <summary>Null when no such organization exists (or it is soft-deleted).</summary>
    Task<OrganizationRef?> FindBySlugAsync(string slug, CancellationToken cancellationToken = default);

    Task<OrganizationRef?> FindByIdAsync(Guid organizationId, CancellationToken cancellationToken = default);
}

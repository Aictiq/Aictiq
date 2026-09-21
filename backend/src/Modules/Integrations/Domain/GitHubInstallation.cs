using Aictiq.SharedKernel.Domain;

namespace Aictiq.Modules.Integrations.Domain;

public enum GitHubInstallationStatus : short
{
    Active = 0,
    Suspended = 1,
    Deleted = 2,
}

/// <summary>An organization-scoped installation of the Aictiq GitHub App.</summary>
public sealed class GitHubInstallation : TenantEntity, IAudited
{
    /// <summary>The immutable numeric installation identifier assigned by GitHub.</summary>
    public long InstallationId { get; init; }
    public required string AccountLogin { get; set; }
    public required string AccountType { get; set; }
    public GitHubInstallationStatus Status { get; set; } = GitHubInstallationStatus.Active;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }
}

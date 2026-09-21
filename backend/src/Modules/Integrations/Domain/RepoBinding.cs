using Aictiq.SharedKernel.Domain;

namespace Aictiq.Modules.Integrations.Domain;

/// <summary>Connects one GitHub repository to one Aictiq project.</summary>
public sealed class RepoBinding : TenantEntity, IAudited
{
    public Guid ProjectId { get; init; }
    public long InstallationId { get; init; }
    public long RepoId { get; init; }
    public required string FullName { get; set; }
    /// <summary>Optional target when a pull request is opened for this repository.</summary>
    public Guid? OnPullRequestOpenedStateId { get; set; }
    /// <summary>Optional target when a closing-keyword pull request is merged. Null uses the first Resolved state.</summary>
    public Guid? OnPullRequestMergedStateId { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
}

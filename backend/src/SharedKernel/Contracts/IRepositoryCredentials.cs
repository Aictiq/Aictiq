namespace Aictiq.SharedKernel.Contracts;

/// <summary>
/// Short-lived clone credentials for a project's bound repository, for the module that
/// hands a runner its work (Automation) without learning how installations are stored
/// (Integrations). Null means the project has no active binding for that repository -
/// the caller fails the run rather than retrying.
/// </summary>
public interface IRepositoryCredentials
{
    Task<string?> GetCloneTokenAsync(
        Guid projectId, string repoFullName, CancellationToken cancellationToken = default);
}

/// <summary>No Integrations module means no bindings and no tokens.</summary>
public sealed class NullRepositoryCredentials : IRepositoryCredentials
{
    public Task<string?> GetCloneTokenAsync(
        Guid projectId, string repoFullName, CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);
}

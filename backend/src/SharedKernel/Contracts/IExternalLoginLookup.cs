namespace Aictiq.SharedKernel.Contracts;

/// <summary>Identity's narrow lookup seam for integration-attributed activity.</summary>
public interface IExternalLoginLookup
{
    /// <summary>Maps a GitHub login when linked, falling back to its verified commit email.</summary>
    Task<string?> FindGitHubUserIdAsync(string? login, string? email, CancellationToken cancellationToken = default);
}

public sealed class NullExternalLoginLookup : IExternalLoginLookup
{
    public Task<string?> FindGitHubUserIdAsync(string? login, string? email, CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);
}

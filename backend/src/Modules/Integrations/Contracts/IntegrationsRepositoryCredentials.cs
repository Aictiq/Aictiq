using Aictiq.SharedKernel.Contracts;
using Microsoft.EntityFrameworkCore;
using Aictiq.Modules.Integrations.Domain;

namespace Aictiq.Modules.Integrations.Contracts;

/// <summary>
/// Hands a factory runner a short-lived clone token for the project's bound repository.
/// The binding decides everything: the wrong <paramref name="repoFullName"/> (or an
/// installation that is no longer active) is answered with null rather than a token, so
/// a stale factory setting can never clone a repository the organization unbound.
/// </summary>
internal sealed class IntegrationsRepositoryCredentials(IntegrationsDbContext db, GitHubAppClient github)
    : IRepositoryCredentials
{
    public async Task<string?> GetCloneTokenAsync(
        Guid projectId, string repoFullName, CancellationToken cancellationToken = default)
    {
        var binding = await db.RepoBindings.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ProjectId == projectId && x.FullName == repoFullName, cancellationToken);
        if (binding is null
            || !await db.GitHubInstallations.AnyAsync(
                x => x.InstallationId == binding.InstallationId && x.Status == GitHubInstallationStatus.Active,
                cancellationToken))
        {
            return null;
        }

        return await github.GetInstallationTokenAsync(binding.InstallationId, cancellationToken);
    }
}

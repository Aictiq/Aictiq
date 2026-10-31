using Aictiq.Modules.Automation.Domain;

namespace Aictiq.Modules.Automation;

/// <summary>
/// Whether a runner can serve every checkout of a multi-repository run, judged like
/// <see cref="RunnerProjects"/> judges a project: a GitHub checkout is cloned, so any runner has
/// it; a local one needs the runner to map it (<c>KEY</c> for the primary, <c>KEY/alias</c> for the
/// others) or a root above its path hint. Where Aictiq cannot tell, it says yes, and a runner that
/// then finds no clone hands the run back naming the checkout.
/// </summary>
public static class RepositoryRouting
{
    /// <summary>The first checkout the runner plainly does not have, or null when it may have them all.</summary>
    public static string? MissingRepository(RunnerCapabilities? capabilities, string projectKey, IReadOnlyList<RunRepository> repositories)
    {
        foreach (var repository in repositories)
        {
            if (RepositoryPlans.NeedsLocal(repository)
                && !RunnerProjects.CanServe(capabilities, RepositoryPlans.MappingKey(projectKey, repository), repository.LocalPathHint))
            {
                return repository.Alias;
            }
        }
        return null;
    }
}

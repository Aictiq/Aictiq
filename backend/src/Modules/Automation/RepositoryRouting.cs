using Aictiq.Modules.Automation.Domain;

namespace Aictiq.Modules.Automation;

/// <summary>
/// Whether a runner can serve every checkout of a multi-repository run, judged like
/// <see cref="RunnerProjects"/> judges a project: a GitHub checkout is cloned, so any runner has
/// it; a local one needs the runner to map it (<c>KEY</c> for the primary, <c>KEY/alias</c> for the
/// others) or a root above its path hint. Where Aictiq cannot tell, it says yes, and a runner that
/// then finds no clone hands the run back naming the checkout.
/// <para>
/// A runner that lacks some checkouts may still take the run when no other runner has them all:
/// it starts with the ones it has (<see cref="CanStart"/>), and the run records what it missed.
/// </para>
/// </summary>
public static class RepositoryRouting
{
    /// <summary>The first checkout the runner plainly does not have, or null when it may have them all.</summary>
    public static string? MissingRepository(RunnerCapabilities? capabilities, string projectKey, IReadOnlyList<RunRepository> repositories) =>
        MissingRepositories(capabilities, projectKey, repositories).FirstOrDefault();

    /// <summary>The aliases of every checkout the runner plainly does not have.</summary>
    public static List<string> MissingRepositories(RunnerCapabilities? capabilities, string projectKey, IReadOnlyList<RunRepository> repositories) =>
        [.. repositories
            .Where(repository => RepositoryPlans.NeedsLocal(repository)
                && !RunnerProjects.CanServe(capabilities, RepositoryPlans.MappingKey(projectKey, repository), repository.LocalPathHint))
            .Select(repository => repository.Alias)];

    /// <summary>True when the runner may have at least one of the checkouts, so it can start the run with those.</summary>
    public static bool CanStart(RunnerCapabilities? capabilities, string projectKey, IReadOnlyList<RunRepository> repositories) =>
        MissingRepositories(capabilities, projectKey, repositories).Count < repositories.Count;

    /// <summary>The command that maps a missing checkout on a runner.</summary>
    public static string MapCommand(string projectKey, string alias) => $"aictiq runner map {projectKey}/{alias} <path>";
}

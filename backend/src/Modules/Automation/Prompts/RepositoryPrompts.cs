using Aictiq.Modules.Automation.Domain;

namespace Aictiq.Modules.Automation.Prompts;

/// <summary>
/// The parts of a prompt that say where a multi-repository run's code is, shared by every kind of
/// run that checks out the project's repositories side by side.
/// </summary>
public static class RepositoryPrompts
{
    /// <summary>One line per checkout: its alias, directory, source and default branch.</summary>
    public static string Lines(IReadOnlyList<RunRepository> checkouts, string directoryVerb) =>
        string.Join("\n", checkouts.Select(repo =>
        {
            var where = repo.WorkingDirectory is { } directory ? $"; {directoryVerb} `repo/{repo.Alias}/{directory}`" : "";
            var what = repo.RepoFullName ?? repo.LocalPathHint ?? "the runner's local clone";
            return $"- `{repo.Alias}`: `repo/{repo.Alias}/` ({what}, default branch {repo.DefaultBranch}){where}";
        }));

    /// <summary>The ticket's plain <c>repo:</c> labels, as a hint; empty without any.</summary>
    public static string FocusLine(IReadOnlyList<string> focus) =>
        focus.Count == 0
            ? ""
            : $"\nThe ticket's labels point at {string.Join(", ", focus.Select(alias => $"`{alias}`"))}: start there, and look at the others when the work or the question needs them.";

    /// <summary>
    /// The section of a run that reads code and changes none (refine, chat, project chat): every
    /// checkout is the project's own, read-only, and the agent looks in whichever it needs.
    /// Null when the run has a single checkout.
    /// </summary>
    public static string? ReadOnly(RepositoryPrompt? repositories)
    {
        if (repositories is null)
        {
            return null;
        }
        if (repositories.Checkouts.Count == 0)
        {
            return $"""
                # Working directory

                This project is a monorepo. You start in `{repositories.WorkingDirectory}` inside the checkout; the rest of the repository is there too.
                """;
        }
        return $"""
            # Repositories

            This project has more than one repository, and this run has a read-only checkout of each under `repo/`:
            {Lines(repositories.Checkouts, "the code is in")}
            These are the project's repositories. Look in whichever ones the question needs; never say one of them is not part of the project or ask anyone to add it.{FocusLine(repositories.Focus)}
            """;
    }

    /// <summary>
    /// What the claiming runner could not check out, added to the prompt when it starts the run:
    /// the repositories are still the project's, and a runner admin maps them for the next run.
    /// </summary>
    public static string Missing(string projectKey, IReadOnlyList<string> aliases)
    {
        var names = string.Join(", ", aliases.Select(alias => $"`{alias}`"));
        var commands = string.Join("\n", aliases.Select(alias => $"- `{RepositoryRouting.MapCommand(projectKey, alias)}`"));
        return $"""
            # Repositories this runner does not have

            {names} {(aliases.Count == 1 ? "is one" : "are")} of the project's repositories, but this runner has no local clone of {(aliases.Count == 1 ? "it" : "them")}, so {(aliases.Count == 1 ? "it is" : "they are")} not checked out for this run. Work with the repositories you have. If the work or the question needs a missing one, say so plainly in your final message, and that a runner admin can map it with:
            {commands}
            Do not say a missing repository is not part of the project, and do not ask anyone to add it to the project.
            """;
    }
}

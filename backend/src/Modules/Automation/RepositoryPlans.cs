using System.Text.RegularExpressions;
using Aictiq.Modules.Automation.Domain;

namespace Aictiq.Modules.Automation;

/// <summary>
/// Multiple repositories and monorepo sub-packages: which repositories a run checks out, where the
/// agent starts, and in what order the repositories of one ticket run.
/// <list type="bullet">
/// <item>The ticket decides first, with labels: <c>repo:&lt;alias&gt;</c> selects a repository,
/// <c>repo:&lt;alias&gt;:after:&lt;dep&gt;</c> also orders it after <c>dep</c>, and
/// <c>repo:&lt;alias&gt;:after-merge:&lt;dep&gt;</c> after <c>dep</c>'s pull request has merged.</item>
/// <item>Without such labels, the playbook's <see cref="Playbook.RepositorySteps"/>.</item>
/// <item>Without either, the primary repository, as a single-repository project always did.</item>
/// </list>
/// The steps that have nothing to wait for run together, in one run with a checkout of each;
/// the rest wait as <see cref="ChainStepKind.Repositories"/> chain steps.
/// </summary>
public static partial class RepositoryPlans
{
    public const string LabelPrefix = "repo:";

    public const string AliasRule = "Use 1-40 lowercase letters, digits, dots, dashes or underscores, starting with a letter or digit.";

    public static bool IsAlias(string? alias) => alias is not null && AliasPattern().IsMatch(alias);

    /// <summary>The primary repository's alias: the stored one, or its repository's name.</summary>
    public static string PrimaryAlias(ProjectFactorySettings? settings)
    {
        if (settings?.PrimaryAlias is { } stored)
        {
            return stored;
        }
        var source = settings?.RepoSource == ProjectRepositorySource.GitHubBinding ? settings.RepoFullName : settings?.LocalPathHint;
        var name = source?.Replace('\\', '/').TrimEnd('/').Split('/').LastOrDefault()?.ToLowerInvariant() ?? "";
        name = InvalidAliasChars().Replace(name, "-").Trim('-', '.', '_');
        if (name.Length > ProjectRepository.MaxAliasLength)
        {
            name = name[..ProjectRepository.MaxAliasLength].TrimEnd('-', '.', '_');
        }
        return IsAlias(name) ? name : "main";
    }

    /// <summary>
    /// A working directory relative to the checkout's root, cleaned to forward slashes without a
    /// trailing one; null for the root itself. Error is set when it could leave the checkout.
    /// </summary>
    public static (string? Value, string? Error) CleanWorkingDirectory(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return (null, null);
        }
        var clean = value.Trim().Replace('\\', '/');
        while (clean.StartsWith("./", StringComparison.Ordinal))
        {
            clean = clean[2..];
        }
        clean = clean.TrimEnd('/');
        if (clean.Length == 0 || clean == ".")
        {
            return (null, null);
        }
        if (clean.Length > ProjectRepository.MaxWorkingDirectoryLength)
        {
            return (null, "Use 500 characters or fewer.");
        }
        if (clean.StartsWith('/') || WindowsRoot().IsMatch(clean) || clean.Split('/').Any(part => part is ".." or ""))
        {
            return (null, "Use a path inside the repository, such as packages/api.");
        }
        return (clean, null);
    }

    /// <summary>Why the steps cannot be saved, or null. Aliases are not checked against the project: they may be renamed later.</summary>
    public static string? Validate(IReadOnlyList<RepositoryStep> steps)
    {
        if (steps.Count > RepositoryStep.MaxSteps)
        {
            return $"Use at most {RepositoryStep.MaxSteps} repository steps.";
        }
        var aliases = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in steps)
        {
            if (!IsAlias(step.Alias))
            {
                return $"\"{step.Alias}\" is not a repository alias. {AliasRule}";
            }
            if (!aliases.Add(step.Alias))
            {
                return $"{step.Alias} is listed twice.";
            }
            if (CleanWorkingDirectory(step.WorkingDirectory).Error is { } error)
            {
                return $"{step.Alias}: {error}";
            }
        }
        foreach (var step in steps)
        {
            foreach (var dependency in step.After)
            {
                if (dependency == step.Alias)
                {
                    return $"{step.Alias} cannot wait for itself.";
                }
                if (!aliases.Contains(dependency))
                {
                    return $"{step.Alias} waits for {dependency}, which is not a step of this playbook.";
                }
            }
        }
        return HasCycle(steps) ? "The steps wait for each other in a circle; remove one of the waits." : null;
    }

    /// <summary>Steps with cleaned working directories and distinct, sorted waits, as they are stored.</summary>
    public static List<RepositoryStep> Normalize(IEnumerable<RepositoryStep>? steps) =>
        [.. (steps ?? []).Select(step => new RepositoryStep(
            step.Alias?.Trim() ?? "",
            CleanWorkingDirectory(step.WorkingDirectory).Value,
            [.. (step.After ?? []).Select(a => a.Trim()).Where(a => a.Length > 0).Distinct(StringComparer.Ordinal)],
            step.WaitForMerge && step.After is { Length: > 0 }))];

    /// <summary>
    /// The ticket's say, from its label names: the repositories it selects, and the waits it sets.
    /// Null when no label starts with <c>repo:</c>.
    /// </summary>
    public static List<RepositoryStep>? FromLabels(IEnumerable<string> labels)
    {
        var steps = new Dictionary<string, (HashSet<string> After, bool Merge, bool Ordered)>(StringComparer.Ordinal);
        var order = new List<string>();
        void Select(string alias)
        {
            if (!steps.ContainsKey(alias))
            {
                steps[alias] = (new HashSet<string>(StringComparer.Ordinal), false, false);
                order.Add(alias);
            }
        }

        foreach (var label in labels)
        {
            var name = label.Trim();
            if (!name.StartsWith(LabelPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var parts = name[LabelPrefix.Length..].ToLowerInvariant().Split(':');
            if (parts.Length == 1 && IsAlias(parts[0]))
            {
                Select(parts[0]);
            }
            else if (parts.Length == 3 && IsAlias(parts[0]) && IsAlias(parts[2]) && parts[0] != parts[2]
                && parts[1] is "after" or "after-merge")
            {
                Select(parts[2]);
                Select(parts[0]);
                var entry = steps[parts[0]];
                entry.After.Add(parts[2]);
                steps[parts[0]] = (entry.After, entry.Merge || parts[1] == "after-merge", true);
            }
        }
        if (order.Count == 0)
        {
            return null;
        }
        return [.. order.Select(alias => new RepositoryStep(alias, null, [.. steps[alias].After.Order(StringComparer.Ordinal)], steps[alias].Merge))];
    }

    /// <summary>
    /// The graph a new run follows: the ticket's selection with its own waits where it set any and
    /// the playbook's otherwise, or the playbook's steps, or null for the primary repository alone.
    /// Working directories come from the playbook's steps.
    /// </summary>
    public static List<RepositoryStep>? Resolve(IReadOnlyList<RepositoryStep> playbookSteps, IEnumerable<string> labels)
    {
        var fromTicket = FromLabels(labels);
        if (fromTicket is null)
        {
            return playbookSteps.Count == 0 ? null : [.. playbookSteps];
        }
        var playbook = playbookSteps.ToDictionary(step => step.Alias, StringComparer.Ordinal);
        var selected = fromTicket.Select(step => step.Alias).ToHashSet(StringComparer.Ordinal);
        var resolved = fromTicket.Select(step =>
        {
            var own = playbook.GetValueOrDefault(step.Alias);
            // The ticket ordered this repository itself: its waits replace the playbook's.
            var after = step.After.Length > 0 ? step.After : own?.After.Where(selected.Contains).ToArray() ?? [];
            var merge = step.After.Length > 0 ? step.WaitForMerge : own?.WaitForMerge == true && after.Length > 0;
            return new RepositoryStep(step.Alias, own?.WorkingDirectory, after, merge);
        }).ToList();
        return HasCycle(resolved) ? null : resolved;
    }

    /// <summary>The steps whose waits are all in <paramref name="done"/> and that are not done themselves.</summary>
    public static List<RepositoryStep> NextWave(IReadOnlyList<RepositoryStep> plan, IReadOnlySet<string> done) =>
        [.. plan.Where(step => !done.Contains(step.Alias) && step.After.All(done.Contains))];

    /// <summary>A plan that is only the primary repository needs no second checkout: the run keeps the single layout.</summary>
    public static bool IsPrimaryOnly(IReadOnlyList<RepositoryStep> plan, string primaryAlias) =>
        plan.Count == 1 && plan[0].Alias == primaryAlias;

    /// <summary>The checkouts of one wave, or the alias no repository of the project answers to.</summary>
    public static (List<RunRepository> Checkouts, string? Unknown) Checkouts(
        IReadOnlyList<RepositoryStep> wave, ProjectFactorySettings? settings, IReadOnlyList<ProjectRepository> additional)
    {
        var primary = PrimaryAlias(settings);
        var result = new List<RunRepository>();
        foreach (var step in wave)
        {
            if (step.Alias == primary)
            {
                var github = settings?.RepoSource == ProjectRepositorySource.GitHubBinding && settings.RepoFullName is not null;
                result.Add(new RunRepository(primary,
                    github ? ProjectRepositorySource.GitHubBinding : ProjectRepositorySource.RunnerLocal,
                    github ? settings!.RepoFullName : null, github ? null : settings?.LocalPathHint,
                    settings?.DefaultBranch ?? "main", step.WorkingDirectory ?? settings?.PrimaryWorkingDirectory, true));
                continue;
            }
            if (additional.FirstOrDefault(repo => repo.Alias == step.Alias) is not { } repository)
            {
                return ([], step.Alias);
            }
            result.Add(new RunRepository(repository.Alias, repository.RepoSource, repository.RepoFullName,
                repository.LocalPathHint, repository.DefaultBranch, step.WorkingDirectory ?? repository.WorkingDirectory, false));
        }
        return (result, null);
    }

    /// <summary>A checkout the runner has to find on its own machine rather than clone from GitHub.</summary>
    public static bool NeedsLocal(RunRepository repository) =>
        repository is not { Source: ProjectRepositorySource.GitHubBinding, RepoFullName: not null };

    /// <summary>The runner's workspace mapping key for a checkout: the project key for the primary, <c>KEY/alias</c> otherwise.</summary>
    public static string MappingKey(string projectKey, RunRepository repository) =>
        repository.Primary ? projectKey : $"{projectKey}/{repository.Alias}";

    private static bool HasCycle(IReadOnlyList<RepositoryStep> steps)
    {
        var after = steps.ToDictionary(step => step.Alias, step => step.After, StringComparer.Ordinal);
        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        bool Visit(string alias)
        {
            switch (state.GetValueOrDefault(alias))
            {
                case 1: return true;
                case 2: return false;
            }
            state[alias] = 1;
            foreach (var dependency in after.GetValueOrDefault(alias) ?? [])
            {
                if (after.ContainsKey(dependency) && Visit(dependency))
                {
                    return true;
                }
            }
            state[alias] = 2;
            return false;
        }
        return steps.Any(step => Visit(step.Alias));
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,39}$")]
    private static partial Regex AliasPattern();

    [GeneratedRegex("[^a-z0-9._-]+")]
    private static partial Regex InvalidAliasChars();

    [GeneratedRegex(@"^[A-Za-z]:")]
    private static partial Regex WindowsRoot();
}

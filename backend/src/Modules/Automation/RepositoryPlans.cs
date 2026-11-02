using System.Text.RegularExpressions;
using Aictiq.Modules.Automation.Domain;

namespace Aictiq.Modules.Automation;

/// <summary>
/// Multiple repositories and monorepo sub-packages: which repositories a run checks out, where the
/// agent starts, and in what order the repositories of one ticket run.
/// A run checks out every repository of the project (<see cref="AllRepositories"/>); the agent
/// decides which ones it needs. An implement run still orders them:
/// <list type="bullet">
/// <item>The ticket decides first, with labels: <c>repo:&lt;alias&gt;:after:&lt;dep&gt;</c> orders a
/// repository after <c>dep</c>, and <c>repo:&lt;alias&gt;:after-merge:&lt;dep&gt;</c> after
/// <c>dep</c>'s pull request has merged. A plain <c>repo:&lt;alias&gt;</c> only says where to focus.</item>
/// <item>Without such labels, the waits of the playbook's <see cref="Playbook.RepositorySteps"/>.</item>
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
    /// Every repository of the project, primary first: what a refine, chat, project-chat or
    /// implement run checks out while multiple repositories are in force. The agent picks the
    /// ones it needs. Working directories come from the playbook's steps.
    /// <para>
    /// With <paramref name="ordered"/> (an implement run), the ticket's ordering labels and the
    /// playbook's waits still split the repositories into waves: a label's waits replace the
    /// playbook's for that repository. Plan is null when nothing waits, so one run has them all.
    /// An ordering label or a step naming a repository the project does not have is Unknown;
    /// a read-only run ignores waits, and with them such names.
    /// </para>
    /// Focus is what plain <c>repo:&lt;alias&gt;</c> labels name among the project's
    /// repositories: a hint to the agent, no longer a selection.
    /// </summary>
    public static (List<RepositoryStep> Steps, List<RepositoryStep>? Plan, List<string> Focus, string? Unknown) AllRepositories(
        IReadOnlyList<string> aliases, IReadOnlyList<RepositoryStep> playbookSteps, IEnumerable<string> labels, bool ordered)
    {
        var known = aliases.ToHashSet(StringComparer.Ordinal);
        var names = labels.ToList();
        var focus = PlainLabels(names).Where(known.Contains).ToList();
        var playbook = playbookSteps.ToDictionary(step => step.Alias, StringComparer.Ordinal);
        if (!ordered)
        {
            return ([.. aliases.Select(alias => new RepositoryStep(alias, playbook.GetValueOrDefault(alias)?.WorkingDirectory, [], false))],
                null, focus, null);
        }

        var fromTicket = (FromLabels(names) ?? []).Where(step => step.After.Length > 0)
            .ToDictionary(step => step.Alias, StringComparer.Ordinal);
        var named = fromTicket.Values.SelectMany(step => step.After.Append(step.Alias))
            .Concat(playbookSteps.SelectMany(step => step.After.Append(step.Alias)));
        if (named.FirstOrDefault(alias => !known.Contains(alias)) is { } unknown)
        {
            return ([], null, focus, unknown);
        }

        List<RepositoryStep> Steps(bool withTicket) => [.. aliases.Select(alias =>
        {
            var own = playbook.GetValueOrDefault(alias);
            return withTicket && fromTicket.TryGetValue(alias, out var ticket)
                ? new RepositoryStep(alias, own?.WorkingDirectory, ticket.After, ticket.WaitForMerge)
                : new RepositoryStep(alias, own?.WorkingDirectory, own?.After ?? [], own is { WaitForMerge: true, After.Length: > 0 });
        })];
        // Labels that wait for each other in a circle order nothing; the playbook's waits never do.
        var steps = Steps(withTicket: true);
        if (HasCycle(steps))
        {
            steps = Steps(withTicket: false);
        }
        return (steps, steps.Any(step => step.After.Length > 0) ? steps : null, focus, null);
    }

    /// <summary>The aliases plain <c>repo:&lt;alias&gt;</c> labels name, in label order.</summary>
    public static List<string> PlainLabels(IEnumerable<string> labels) =>
        [.. labels.Select(label => label.Trim())
            .Where(name => name.StartsWith(LabelPrefix, StringComparison.OrdinalIgnoreCase))
            .Select(name => name[LabelPrefix.Length..].ToLowerInvariant())
            .Where(IsAlias)
            .Distinct(StringComparer.Ordinal)];

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

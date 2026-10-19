namespace Aictiq.Modules.Automation.Domain;

/// <summary>
/// Whether a runner has a project's repository on its machine, judged from what it last reported
/// (<see cref="RunnerCapabilities.Workspaces"/>, <see cref="RunnerCapabilities.RepoRoots"/>). The
/// runner decides for itself when it provisions the run; this only keeps a run away from a machine
/// that plainly cannot take it. Where Aictiq cannot tell, it says yes, and a runner that then finds
/// no repository hands the run back (<c>POST /runner/runs/{id}/release</c>, reason
/// <c>no-local-repository</c>).
/// </summary>
public static partial class RunnerProjects
{
    /// <summary>A project whose runs work in a runner's own clone rather than one cloned from GitHub.</summary>
    public static bool NeedsLocalRepository(ProjectFactorySettings? settings) =>
        settings is not { RepoSource: ProjectRepositorySource.GitHubBinding, RepoFullName: not null };

    /// <summary>An older CLI reports neither, and keeps claiming every project as it always did.</summary>
    public static bool ReportsProjects(RunnerCapabilities? capabilities) =>
        capabilities is { Workspaces: not null } or { RepoRoots: not null };

    /// <summary>The runner maps the project, or the project's path hint may lie under one of its roots.</summary>
    public static bool CanServe(RunnerCapabilities? capabilities, string projectKey, string? pathHint)
    {
        if (!ReportsProjects(capabilities))
        {
            return true;
        }
        if (capabilities!.Workspaces?.Contains(projectKey, StringComparer.Ordinal) == true)
        {
            return true;
        }
        return !string.IsNullOrWhiteSpace(pathHint) && HintUnderRoot(pathHint, capabilities.RepoRoots ?? []) != false;
    }

    /// <summary>
    /// Whether <paramref name="hint"/> lies under one of <paramref name="roots"/>, compared as text:
    /// the runner resolves symlinks and its home directory, the server cannot. Null when the answer
    /// depends on which home <c>~</c> is. Mirrors <c>hintUnderRoot</c> in the web app's setup guide.
    /// </summary>
    public static bool? HintUnderRoot(string hint, IReadOnlyList<string> roots)
    {
        var path = Clean(hint);
        if (path.Length == 0)
        {
            return null;
        }
        var cleaned = roots.Select(Clean).Where(root => root.Length > 0).ToList();
        if (cleaned.Count == 0)
        {
            return false;
        }

        if (cleaned.Any(root => Inside(path, root)))
        {
            return true;
        }
        if (path.StartsWith("~/", StringComparison.Ordinal))
        {
            // `/home/ana/src` covers `~/src/x` when `~` is `/home/ana`; which home it is only the
            // runner knows, so a match there is as far as this can go.
            var rest = path[1..];
            var homed = cleaned.Select(root => HomeRoot().Match(root)).Where(m => m.Success).ToList();
            if (homed.Count == 0)
            {
                return null;
            }
            return homed.Any(m => Inside(m.Groups[1].Value + rest, m.Value));
        }
        if (!path.StartsWith('/') && !WindowsRoot().IsMatch(path))
        {
            return null;
        }
        // A root written with `~` may be this very directory.
        return cleaned.Any(root => root == "~" || root.StartsWith("~/", StringComparison.Ordinal)) ? null : false;
    }

    private static string Clean(string path)
    {
        var clean = path.Trim().Replace('\\', '/');
        while (clean.Length > 1 && clean.EndsWith('/'))
        {
            clean = clean[..^1];
        }
        return clean;
    }

    private static bool Inside(string candidate, string root) =>
        candidate == root || candidate.StartsWith(root == "/" ? "/" : root + "/", StringComparison.Ordinal);

    [System.Text.RegularExpressions.GeneratedRegex(@"^(/home/[^/]+|/Users/[^/]+|/root)(/.*)?$")]
    private static partial System.Text.RegularExpressions.Regex HomeRoot();

    [System.Text.RegularExpressions.GeneratedRegex(@"^[A-Za-z]:/")]
    private static partial System.Text.RegularExpressions.Regex WindowsRoot();
}

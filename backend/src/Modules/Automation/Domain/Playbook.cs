using Aictiq.SharedKernel.Domain;

namespace Aictiq.Modules.Automation.Domain;

public static class Harnesses
{
    public const string Claude = "claude";
    public const string Codex = "codex";
    public const string OpenCode = "opencode";
    public const string Cursor = "cursor";
    public const string Copilot = "copilot";

    public static bool IsSupported(string? harness) => harness is Claude or Codex or OpenCode or Cursor or Copilot;
}

/// <summary>Project instructions backed by a versioned wiki page.</summary>
public sealed class Playbook : TenantEntity, IAudited
{
    public const int MaxNameLength = 100;

    public Guid ProjectId { get; init; }
    public required string Name { get; set; }
    public Guid? WikiPageId { get; set; }
    public required string Harness { get; set; }
    public Guid? OnSuccessStateId { get; set; }
    public Guid? OnFailureStateId { get; set; }
    public int MaxMinutes { get; set; } = 60;
    public bool IsDefault { get; set; }
    public bool WorkOnDefaultBranch { get; set; }

    /// <summary>Tags every run of this playbook requires of its runner while fleet mode is on.</summary>
    public string[] RequiredTags { get; set; } = [];

    /// <summary>
    /// Multiple repositories: the repositories this playbook works in when the ticket names none,
    /// in the order they run. Kept, but ignored, while the project's switch is off or its plan
    /// does not allow it.
    /// </summary>
    public List<RepositoryStep> RepositorySteps { get; set; } = [];
    public required string CreatedBy { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }
    public uint Version { get; private set; }
}

/// <summary>
/// One follow-up playbook in a playbook's chain. After a successful run of
/// <see cref="PlaybookId"/> opens or updates its pull request, the stages run on that pull
/// request one after another, by <see cref="Position"/>, before the item reaches human review.
/// A stage is a plain playbook of the same project; its own stages are never followed.
/// </summary>
public sealed class PlaybookStage : TenantEntity
{
    /// <summary>A chain longer than this costs more than it can catch.</summary>
    public const int MaxStages = 10;

    public Guid PlaybookId { get; init; }

    public Guid StagePlaybookId { get; init; }

    public int Position { get; init; }
}

public enum ProjectRepositorySource : short
{
    GitHubBinding = 0,
    RunnerLocal = 1,
}

/// <summary>
/// One repository of a run's execution graph. <see cref="After"/> names the steps that must be done
/// first: finished, or with <see cref="WaitForMerge"/> their pull requests merged. Steps with nothing
/// left to wait for run together, in one run with a checkout of each.
/// </summary>
/// <param name="WorkingDirectory">Where the agent starts inside the checkout, in place of the repository's default.</param>
public sealed record RepositoryStep(string Alias, string? WorkingDirectory = null, string[]? After = null, bool WaitForMerge = false)
{
    public const int MaxSteps = 10;

    public string[] After { get; init; } = After ?? [];
}

/// <summary>
/// A repository of a project beyond its primary one, which stays in <see cref="ProjectFactorySettings"/>.
/// Used only while the project's multiple-repositories switch is on and its plan allows it.
/// </summary>
public sealed class ProjectRepository : TenantEntity
{
    public const int MaxRepositories = 20;
    public const int MaxAliasLength = 40;
    public const int MaxWorkingDirectoryLength = 500;

    public Guid ProjectId { get; init; }
    public int Position { get; set; }
    public required string Alias { get; set; }
    public ProjectRepositorySource RepoSource { get; set; }
    public string? RepoFullName { get; set; }
    public string DefaultBranch { get; set; } = "main";
    public string? LocalPathHint { get; set; }
    public string? WorkingDirectory { get; set; }
}

/// <summary>Where a runner gets one project's repository and which agent and runner it uses by default.</summary>
public sealed class ProjectFactorySettings : TenantEntity, IAudited
{
    public Guid ProjectId { get; init; }
    public ProjectRepositorySource RepoSource { get; set; }
    public string? RepoFullName { get; set; }
    public string DefaultBranch { get; set; } = "main";
    public string? LocalPathHint { get; set; }
    public string? DefaultAgentId { get; set; }
    /// <summary>
    /// The runner project chat and new runs go to when nobody chose one; null is any free runner.
    /// Deleting the runner clears it.
    /// </summary>
    public Guid? DefaultRunnerId { get; set; }

    /// <summary>The "multiple repositories / monorepo" switch. Off by default, and kept through a downgrade.</summary>
    public bool MultiRepoEnabled { get; set; }

    /// <summary>The primary repository's alias; null derives one (<see cref="RepositoryPlans.PrimaryAlias"/>).</summary>
    public string? PrimaryAlias { get; set; }

    /// <summary>Where the agent starts inside the primary checkout while multiple repositories are on.</summary>
    public string? PrimaryWorkingDirectory { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public uint Version { get; private set; }
}

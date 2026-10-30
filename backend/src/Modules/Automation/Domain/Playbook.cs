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
    public DateTimeOffset UpdatedAt { get; set; }
    public uint Version { get; private set; }
}

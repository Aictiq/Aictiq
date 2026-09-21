using Aictiq.SharedKernel.Domain;

namespace Aictiq.Modules.Automation.Domain;

public static class Harnesses
{
    public const string Claude = "claude";
    public const string Codex = "codex";
    public const string OpenCode = "opencode";

    public static bool IsSupported(string? harness) => harness is Claude or Codex or OpenCode;
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
    public required string CreatedBy { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }
    public uint Version { get; private set; }
}

public enum ProjectRepositorySource : short
{
    GitHubBinding = 0,
    RunnerLocal = 1,
}

/// <summary>Where a runner gets one project's repository and which agent it uses by default.</summary>
public sealed class ProjectFactorySettings : TenantEntity, IAudited
{
    public Guid ProjectId { get; init; }
    public ProjectRepositorySource RepoSource { get; set; }
    public string? RepoFullName { get; set; }
    public string DefaultBranch { get; set; } = "main";
    public string? LocalPathHint { get; set; }
    public string? DefaultAgentId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public uint Version { get; private set; }
}

using Aictiq.SharedKernel.Domain;

namespace Aictiq.Modules.WorkItems.Domain;

public enum JiraImportStatus : short { Pending, Running, Completed, Failed }

/// <summary>What went wrong with one part of a Jira import, shown to the user in the result.</summary>
public sealed record JiraImportFailure(string? IssueKey, string Kind, string Reason);

/// <summary>
/// A one-time migration of one Jira Cloud project into one Aictiq project. The API checks the
/// connection and queues it; Workers pull the issues. The API token is the one secret here: it
/// is kept data-protected only while the job can still run, and cleared the moment it ends, so a
/// finished job holds nothing that could reach Jira again.
/// </summary>
public sealed class JiraImportJob : TenantEntity
{
    public Guid ProjectId { get; init; }
    public required string ProjectKey { get; init; }
    /// <summary>For attachment links in imported descriptions, which are addressed by slug.</summary>
    public required string OrganizationSlug { get; init; }
    public required string RequestedBy { get; init; }
    public required string SiteUrl { get; init; }
    public required string Email { get; init; }
    /// <summary>Data-protected API token; null once the job has ended.</summary>
    public string? ProtectedToken { get; set; }
    public required string JiraProjectKey { get; init; }
    public required string JiraProjectName { get; init; }
    /// <summary>Jira issue type name to Aictiq item type, as the user confirmed it.</summary>
    public Dictionary<string, WorkItemType> IssueTypeMap { get; init; } = [];
    /// <summary>Jira status name to workflow state id, as the user confirmed it.</summary>
    public Dictionary<string, Guid> StatusMap { get; init; } = [];
    public JiraImportStatus Status { get; set; }
    public int TotalIssues { get; set; }
    public int ProcessedIssues { get; set; }
    public int CreatedItems { get; set; }
    public int SkippedIssues { get; set; }
    public int CreatedComments { get; set; }
    public int CreatedAttachments { get; set; }
    public int CreatedSprints { get; set; }
    public List<JiraImportFailure> Failures { get; set; } = [];
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? StartedAt { get; set; }
    /// <summary>Touched as issues finish, so a job whose worker died can be picked up again.</summary>
    public DateTimeOffset? HeartbeatAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

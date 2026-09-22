using Aictiq.SharedKernel.Domain;
using Aictiq.Modules.WorkItems.Contracts;
using NpgsqlTypes;

namespace Aictiq.Modules.WorkItems.Domain;

public sealed class WorkItem : TenantEntity, IAudited
{
    public Guid ProjectId { get; init; }
    public required string ProjectKey { get; init; }
    public int Number { get; init; }
    public WorkItemType Type { get; set; }
    public required string Title { get; set; }
    public string DescriptionMarkdown { get; set; } = "";
    public string DescriptionHtml { get; set; } = "";
    public Guid StateId { get; set; }
    public WorkItemPriority Priority { get; set; }
    public string? AssigneeId { get; set; }
    public Guid? TeamId { get; set; }
    /// <summary>The exclusive kanban column for the item's current team board.</summary>
    public Guid? BoardColumnId { get; set; }
    public Guid? SprintId { get; set; }
    public Guid? ParentId { get; set; }
    public string? Rank { get; set; }
    /// <summary>Stable identifier from an imported system. Unique within a project so an import can be replayed safely.</summary>
    public string? ExternalRef { get; set; }
    public decimal? Points { get; set; }
    public decimal? EstimateHours { get; set; }
    public decimal? RemainingHours { get; set; }
    public decimal? CompletedHours { get; set; }
    /// <summary>Transient marker consumed by the history staging hook after a time entry.</summary>
    public decimal? LoggedHours { get; private set; }
    public DateOnly? DueDate { get; set; }
    public string? ClaimedBy { get; set; }
    public DateTimeOffset? ClaimedAt { get; set; }
    public DateTimeOffset? ClaimHeartbeatAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? RemovedAt { get; set; }
    /// <summary>The reason for a Removed-category transition, e.g. <c>duplicate</c>.</summary>
    public string? Resolution { get; set; }
    /// <summary>Database-generated full-text document; it is never set by application code.</summary>
    public NpgsqlTsVector Search { get; private set; } = null!;
    public required string CreatedBy { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }
    public uint Version { get; private set; }
    public string Key => $"{ProjectKey}-{Number}";

    public void Transition(Guid toStateId, WorkflowStateCategory category, string actorId, DateTimeOffset now)
    {
        var fromStateId = StateId;
        StateId = toStateId;
        if (category == WorkflowStateCategory.Resolved) ResolvedAt = now;
        if (category == WorkflowStateCategory.Completed) CompletedAt = now;
        if (category == WorkflowStateCategory.Removed) RemovedAt = now;
        if (category is WorkflowStateCategory.Resolved or WorkflowStateCategory.Completed or WorkflowStateCategory.Removed)
        {
            ClaimedBy = null;
            ClaimedAt = null;
            ClaimHeartbeatAt = null;
        }
        UpdatedAt = now;
        Raise(new WorkItemTransitioned(OrganizationId, ProjectId, Id, Key, fromStateId, toStateId, actorId, SprintId) { OccurredAt = now });
        Raise(new ItemChanged(OrganizationId, ProjectId, Id, Key, actorId, ["stateId"]) { OccurredAt = now });
    }

    public void LogTime(decimal hours, DateTimeOffset now)
    {
        CompletedHours = (CompletedHours ?? 0) + hours;
        RemainingHours = Math.Max(0, (RemainingHours ?? 0) - hours);
        LoggedHours = hours;
        UpdatedAt = now;
    }

    public void ClearLoggedHours() => LoggedHours = null;

    public void SprintScopeChanged(Guid sprintId, bool added) => Raise(new SprintScopeChanged(OrganizationId, sprintId, Id, added, Points, RemainingHours));

    public void Changed(string actorId, params string[] changedFields) =>
        Raise(new ItemChanged(OrganizationId, ProjectId, Id, Key, actorId, changedFields));

    public void Moved(string actorId) => Raise(new BoardMoved(ProjectId, Id, Key, actorId));

    public void ReleaseStaleClaim(DateTimeOffset now)
    {
        ClaimedBy = null;
        ClaimedAt = null;
        ClaimHeartbeatAt = null;
        UpdatedAt = now;
        Raise(new ClaimReleased(OrganizationId, ProjectId, Id, Key) { OccurredAt = now });
    }
}

public sealed class ProjectSequence : TenantEntity
{
    public Guid ProjectId { get; init; }
    public int NextNumber { get; set; }
}

public enum CsvImportStatus : short { Pending, Running, Completed, Failed }

/// <summary>
/// Durable CSV import queue. Keeping the source alongside the job makes workers restart-safe;
/// the API only accepts/uploads it and Workers do all item creation in bounded batches.
/// </summary>
public sealed class CsvImportJob : TenantEntity
{
    public Guid ProjectId { get; init; }
    public required string ProjectKey { get; init; }
    public required string RequestedBy { get; init; }
    public required string Csv { get; init; }
    public required string MappingJson { get; init; }
    public CsvImportStatus Status { get; set; }
    public int TotalRows { get; init; }
    public int ProcessedRows { get; set; }
    public int CreatedRows { get; set; }
    public int SkippedRows { get; set; }
    public string[] Errors { get; set; } = [];
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

/// <summary>
/// A project-owned starting point for one fixed work-item type. Label ids are deliberately
/// stored as values: labels remain independently deletable, and applying a template must
/// never resurrect a label that its project administrator removed.
/// </summary>
public sealed class ItemTemplate : TenantEntity
{
    public Guid ProjectId { get; init; }
    public WorkItemType Type { get; set; }
    public required string Name { get; set; }
    public string DescriptionMarkdown { get; set; } = "";
    public Guid[] DefaultLabelIds { get; set; } = [];
    public WorkItemPriority? DefaultPriority { get; set; }
    public bool IsDefault { get; set; }
    public uint Version { get; private set; }
}

/// <summary>
/// A project-owned item-list preset. Private views are visible only to their creator;
/// shared views are an intentionally small piece of project configuration rather than a
/// second kind of permission-bearing resource.
/// </summary>
public sealed class SavedView : TenantEntity
{
    public Guid ProjectId { get; init; }
    public required string OwnerId { get; init; }
    public required string Name { get; set; }
    public string Filter { get; set; } = "";
    public string Sort { get; set; } = "";
    public string[] Columns { get; set; } = [];
    public bool IsShared { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }
    public uint Version { get; private set; }
}

public sealed class ItemHistory : TenantEntity
{
    public Guid ItemId { get; init; }
    public required string ActorId { get; init; }
    public DateTimeOffset At { get; init; }
    public required string Field { get; init; }
    public string? OldValue { get; init; }
    public string? NewValue { get; init; }
    /// <summary>All changes emitted by one SaveChangesAsync call share this id.</summary>
    public Guid EventId { get; init; }
}

public enum ItemRelationKind : short { Related, Blocks, Duplicates }

public sealed class ItemRelation : TenantEntity
{
    public Guid SourceId { get; init; }
    public Guid TargetId { get; init; }
    public ItemRelationKind Kind { get; init; }
}

public enum ItemLinkKind : short { Url, Commit, PullRequest, Branch }

public sealed class ItemLink : TenantEntity
{
    public Guid ItemId { get; init; }
    public ItemLinkKind Kind { get; init; }
    public required string Provider { get; init; }
    public required string ExternalId { get; init; }
    public required string Url { get; init; }
    public string? Title { get; set; }
    public string? State { get; set; }
    public string? Meta { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
}

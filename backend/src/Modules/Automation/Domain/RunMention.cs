using Aictiq.SharedKernel.Domain;

namespace Aictiq.Modules.Automation.Domain;

/// <summary>Where one comment's request for an agent stands. Only <see cref="Pending"/> is still waiting.</summary>
public enum RunMentionStatus : short
{
    /// <summary>Waiting for the item to be free: a run, or someone's claim, holds it.</summary>
    Pending = 0,

    /// <summary>A run started for it; <see cref="RunMention.RunId"/> names it.</summary>
    Started = 1,

    /// <summary>No run could start; <see cref="RunMention.RefusalReason"/> says why, and the agent replied with it.</summary>
    Refused = 2,
}

/// <summary>
/// A comment that mentioned an agent, asking it to work on the item. The item's queue: one run
/// at a time works an item, so a mention made while another holds it waits here, and the
/// sweeper starts the oldest one as soon as the item is free - several mentions run in the
/// order they were written. One row per (comment, agent): a replayed <c>CommentAdded</c>
/// collides on <c>ux_run_mentions_comment_agent</c> instead of asking twice.
/// </summary>
public sealed class RunMention : TenantEntity
{
    public const int MaxRefusalReasonLength = 500;

    /// <summary>How long a mention waits for an item someone else holds before it gives up.</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromHours(24);

    public Guid ProjectId { get; init; }

    public Guid ItemId { get; init; }

    public required string ItemKey { get; init; }

    public Guid CommentId { get; init; }

    public required string AgentUserId { get; init; }

    /// <summary>The comment's author, who becomes the run's requester.</summary>
    public required string RequestedBy { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public RunMentionStatus Status { get; set; }

    public Guid? RunId { get; set; }

    public string? RefusalReason { get; set; }

    public DateTimeOffset? DecidedAt { get; set; }

    public uint Version { get; set; }
}

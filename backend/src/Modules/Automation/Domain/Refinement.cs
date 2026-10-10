using Aictiq.SharedKernel.Domain;

namespace Aictiq.Modules.Automation.Domain;

/// <summary>
/// What a run is for. An implement run delivers code; a refine run writes the ticket itself;
/// a chat run answers a comment.
/// </summary>
public enum RunKind : short
{
    Implement = 0,

    /// <summary>
    /// Turns a short description into a complete ticket, or asks what it cannot know. It
    /// leaves the item where it is and never commits: <see cref="ItemRefinement"/> carries
    /// its answer, and the person who asked confirms the result.
    /// </summary>
    Refine = 1,

    /// <summary>
    /// Answers a comment that mentioned the agent on an item no implement run has worked on
    /// yet. Like a refine run it leaves the item where it is and never commits; its final
    /// message is the reply in the comment's thread.
    /// </summary>
    Chat = 2,
}

/// <summary>Where one item's refinement stands. Only <see cref="Refining"/> has a live run behind it.</summary>
public enum RefinementStatus : short
{
    Refining = 0,

    /// <summary>The agent asked questions only a person can answer.</summary>
    NeedsInput = 1,

    /// <summary>The agent rewrote the ticket; a person reviews and confirms it.</summary>
    Ready = 2,

    /// <summary>The run ended without an answer - it failed, timed out, was cancelled, or never submitted one.</summary>
    Failed = 3,

    /// <summary>A person accepted the ticket. Nothing refines it any more.</summary>
    Confirmed = 4,
}

/// <summary>
/// One item's trip from a short description to a confirmed ticket. One row per item: asking
/// again reuses it, so its questions and the answers given so far travel into the next run's
/// prompt rather than living in comments the agent would have to tell apart from the rest.
/// </summary>
public sealed class ItemRefinement : TenantEntity
{
    public const int MaxQuestions = 10;

    public const int MaxQuestionLength = 1000;

    public const int MaxAnswerLength = 4000;

    public const int MaxSummaryLength = 2000;

    public Guid ProjectId { get; init; }

    public Guid ItemId { get; init; }

    public required string ItemKey { get; init; }

    public RefinementStatus Status { get; set; }

    /// <summary>The questions the last run left open, in the agent's order.</summary>
    public string[] Questions { get; set; } = [];

    /// <summary>Every question answered so far, oldest first; <see cref="Answers"/> is its parallel list.</summary>
    public string[] AnsweredQuestions { get; set; } = [];

    public string[] Answers { get; set; } = [];

    /// <summary>The agent's note on what it changed, or why it is asking.</summary>
    public string? Summary { get; set; }

    public Guid? LastRunId { get; set; }

    public required string RequestedBy { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? ConfirmedAt { get; set; }

    public string? ConfirmedBy { get; set; }

    public uint Version { get; private set; }
}

/// <summary>
/// How one project refines tickets: the playbook a refine run follows, what the agent should
/// know about the product, and the workflow state a confirmed ticket moves to.
/// </summary>
public sealed class ProjectRefinementSettings : TenantEntity, IAudited
{
    public const int MaxTextLength = 8000;

    public Guid ProjectId { get; init; }

    /// <summary>The playbook refine runs follow; null turns refinement off for the project.</summary>
    public Guid? PlaybookId { get; set; }

    /// <summary>The agent refine runs execute as; null uses the project's default agent.</summary>
    public string? AgentId { get; set; }

    public string ProductDescription { get; set; } = "";

    public string WritingInstructions { get; set; } = "";

    /// <summary>Title conventions such as <c>[DS - ...]</c>.</summary>
    public string NamingConventions { get; set; } = "";

    public string Platforms { get; set; } = "";

    /// <summary>The state a confirmed ticket moves to; null leaves it where it was created.</summary>
    public Guid? RefinedStateId { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public uint Version { get; private set; }
}

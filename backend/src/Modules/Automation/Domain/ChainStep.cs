using Aictiq.SharedKernel.Domain;

namespace Aictiq.Modules.Automation.Domain;

public enum ChainStepKind : short
{
    /// <summary>Run the chain's stage at <see cref="ChainStep.StageIndex"/> on the builder's pull request.</summary>
    Stage = 0,

    /// <summary>Rerun the builder with the findings of the stage run <see cref="ChainStep.AfterRunId"/>.</summary>
    Rework = 1,
}

/// <summary>Where a chain step stands. Only <see cref="Pending"/> is still waiting.</summary>
public enum ChainStepStatus : short
{
    /// <summary>Waiting for the item to be free: the finished run's outcome, or someone's claim, holds it.</summary>
    Pending = 0,

    /// <summary>Its run started; <see cref="ChainStep.RunId"/> names it.</summary>
    Started = 1,

    /// <summary>It could never start; <see cref="ChainStep.DropReason"/> says why, and the item went to human review.</summary>
    Dropped = 2,
}

/// <summary>
/// The next step of a playbook chain, written in the same transaction that finishes the run
/// before it. A run cannot start while the run before it still holds the item - its outcome
/// has yet to reach the item, or a person claimed it in between - so the step waits here and
/// the sweeper starts it once the item is free, like a waiting <see cref="RunMention"/>. One
/// row per finished run (<c>ux_chain_steps_after_run</c>): a run hands over exactly once.
/// </summary>
public sealed class ChainStep : TenantEntity
{
    public const int MaxDropReasonLength = 500;

    public Guid ProjectId { get; init; }

    public Guid ItemId { get; init; }

    public required string ItemKey { get; init; }

    /// <summary>The run that finished and handed over: the builder for a stage, the stage for a rework.</summary>
    public Guid AfterRunId { get; init; }

    /// <summary>The builder playbook whose stages make up the chain.</summary>
    public Guid ChainPlaybookId { get; init; }

    public ChainStepKind Kind { get; init; }

    /// <summary>The stage to run, for <see cref="ChainStepKind.Stage"/>; 0 for a rework.</summary>
    public int StageIndex { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public ChainStepStatus Status { get; set; }

    public Guid? RunId { get; set; }

    public string? DropReason { get; set; }

    public DateTimeOffset? DecidedAt { get; set; }

    public uint Version { get; set; }
}

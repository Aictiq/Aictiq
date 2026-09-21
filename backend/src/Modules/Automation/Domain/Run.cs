using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Domain;

namespace Aictiq.Modules.Automation.Domain;

/// <summary>
/// A run's position in its life. Below <see cref="Succeeded"/> the run is live: a runner
/// may still be working it, and a work item may carry only one live run at a time — the
/// partial unique index <c>ux_runs_item_live</c> is what says so.
/// </summary>
public enum RunStatus : short
{
    Queued = 0,
    Assigned = 1,
    Running = 2,
    Succeeded = 3,
    Failed = 4,
    Cancelled = 5,
    TimedOut = 6,
}

public enum RunLogStream : short
{
    Stdout = 0,
    Stderr = 1,

    /// <summary>Protocol milestones the server writes itself (claimed, started, finished).</summary>
    Event = 2,
}

/// <summary>
/// One execution of a playbook by one agent on one item — the item's factory history.
/// Deliberately not <see cref="IAudited"/>: a run is written by machines on every
/// heartbeat, and a field-level diff of that would be noise, not an audit trail. It is
/// never deleted except with the item, project or organization it belongs to.
/// </summary>
public sealed class Run : TenantEntity
{
    public const int MaxItemKeyLength = 20;

    public const int MaxActorLength = 64;

    public const int MaxBranchNameLength = 256;

    public const int MaxOutcomeSummaryLength = 4000;

    public const int MaxPullRequestUrlLength = 2048;

    public const int MaxFailureReasonLength = 500;

    public Guid ProjectId { get; init; }

    public Guid ItemId { get; init; }

    public required string ItemKey { get; init; }

    public Guid PlaybookId { get; init; }

    /// <summary>The agent account the run executes as. Its per-run token is minted on assignment.</summary>
    public required string AgentUserId { get; init; }

    /// <summary>
    /// The person who dispatched the run, or null when <see cref="RuleId"/> did instead —
    /// exactly one of the two is ever set (<c>ck_runs_requested_by_xor_rule</c>).
    /// </summary>
    public string? RequestedBy { get; init; }

    /// <summary>
    /// The automation rule that dispatched the run, or null when <see cref="RequestedBy"/>
    /// did instead. No FK on purpose: deleting the rule must not rewrite or block its runs'
    /// history, and <c>RuleName</c> on the run views is simply null once the rule is gone.
    /// </summary>
    public Guid? RuleId { get; init; }

    public Guid? RunnerId { get; set; }

    public RunStatus Status { get; set; }

    public required string Harness { get; init; }

    /// <summary>The prompt exactly as it was sent: the playbook page can change mid-run.</summary>
    public required string PromptSnapshot { get; init; }

    /// <summary>The wiki revision the prompt was composed from, so a re-run can diff.</summary>
    public Guid? PlaybookRevisionId { get; init; }

    /// <summary>The per-run agent token, minted when a runner claimed the run and revoked when it finished.</summary>
    public Guid? AgentTokenId { get; set; }

    /// <summary>
    /// Stored rather than recomposed: the item's title can change while the run is in
    /// flight, and a runner told to <c>git checkout</c> a branch that renamed underneath
    /// it would lose its work.
    /// </summary>
    public required string BranchName { get; init; }

    public int MaxMinutes { get; set; }

    public DateTimeOffset QueuedAt { get; init; }

    public DateTimeOffset? AssignedAt { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    public DateTimeOffset? LastHeartbeatAt { get; set; }

    public DateTimeOffset? CancelRequestedAt { get; set; }

    public string? OutcomeSummary { get; set; }

    public string? PullRequestUrl { get; set; }

    public int? ExitCode { get; set; }

    public decimal? CostUsd { get; set; }

    public long? InputTokens { get; set; }

    public long? OutputTokens { get; set; }

    public string? FailureReason { get; set; }

    public uint Version { get; set; }

    public bool IsLive => Status < RunStatus.Succeeded;

    public bool IsTerminal => Status >= RunStatus.Succeeded;

    public static RunStatus? StatusFor(string outcome) => outcome switch
    {
        RunOutcomes.Succeeded => RunStatus.Succeeded,
        RunOutcomes.Failed => RunStatus.Failed,
        RunOutcomes.Cancelled => RunStatus.Cancelled,
        RunOutcomes.TimedOut => RunStatus.TimedOut,
        _ => null,
    };

    /// <summary>
    /// Moves a run to a terminal state. The caller sets the detail fields first; the
    /// outcome must agree with the status it claims, so a "succeeded" outcome can never
    /// land on a failed row.
    /// </summary>
    public void Finish(RunStatus terminal, string outcome, DateTimeOffset now)
    {
        if (terminal < RunStatus.Succeeded)
        {
            throw new ArgumentOutOfRangeException(nameof(terminal), terminal, "Only a terminal status finishes a run.");
        }

        if (StatusFor(outcome) is { } outcomeStatus && outcomeStatus != terminal)
        {
            throw new ArgumentException($"The outcome '{outcome}' does not match the terminal status {terminal}.", nameof(outcome));
        }

        Status = terminal;
        FinishedAt = now;
    }
}

/// <summary>
/// One block of a run's log. Keyed by (run, sequence) rather than given an id of its
/// own: a runner retrying a batch collides on the primary key instead of duplicating
/// output, which makes a replayed delivery the same nothing.
/// </summary>
public sealed class RunLogChunk : TenantEntity
{
    public const int MaxTextLength = 65536;

    /// <summary>
    /// The sequence of the server's "log truncated" marker, written when the cap refuses a
    /// batch. Runners may not use it, so its presence is the truncation flag.
    /// </summary>
    public const int TruncatedSeq = int.MaxValue;

    public Guid RunId { get; init; }

    public int Seq { get; init; }

    public DateTimeOffset At { get; set; }

    public RunLogStream Stream { get; set; }

    public required string Text { get; init; }
}

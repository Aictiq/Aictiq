using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Domain;

namespace Aictiq.Modules.Automation.Domain;

/// <summary>
/// A run's position in its life. Below <see cref="Succeeded"/> the run is live: a runner
/// may still be working it, and a work item may carry only one live run at a time - the
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
/// One execution of a playbook by one agent on one item - the item's factory history.
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

    public const int MaxSessionIdLength = 200;

    public const int MaxWorkspacePathLength = 1024;

    /// <summary>A comment's own limit: the instruction is the comment's whole text.</summary>
    public const int MaxInstructionLength = 20_000;

    /// <summary>
    /// The failure a follow-up run reports when the earlier run's pull request or branch is
    /// gone from git: the runner stops before the harness starts, and the agent says why.
    /// </summary>
    public const string FollowUpTargetMissing = "follow-up-target-missing";

    /// <summary>How many times one failed run - and the runs continuing it - is continued automatically.</summary>
    public const int MaxAutoContinues = 2;

    /// <summary>The failure a continue run reports when its runner no longer has the session.</summary>
    public const string SessionUnavailable = "session-unavailable";

    public Guid ProjectId { get; init; }

    public Guid ItemId { get; init; }

    public required string ItemKey { get; init; }

    public Guid PlaybookId { get; init; }

    /// <summary>An implement run delivers code; a refine run rewrites the ticket and leaves the item where it is.</summary>
    public RunKind Kind { get; init; }

    /// <summary>The agent account the run executes as. Its per-run token is minted on assignment.</summary>
    public required string AgentUserId { get; init; }

    /// <summary>
    /// The person who dispatched the run, or null when <see cref="RuleId"/> did instead -
    /// exactly one of the two is ever set (<c>ck_runs_requested_by_xor_rule</c>).
    /// </summary>
    public string? RequestedBy { get; init; }

    /// <summary>
    /// The automation rule that dispatched the run, or null when <see cref="RequestedBy"/>
    /// did instead. No FK on purpose: deleting the rule must not rewrite or block its runs'
    /// history, and <c>RuleName</c> on the run views is simply null once the rule is gone.
    /// </summary>
    public Guid? RuleId { get; init; }

    /// <summary>The runner that took the run, set on claim.</summary>
    public Guid? RunnerId { get; set; }

    /// <summary>
    /// The runner the dispatcher asked for, or null for whichever free runner claims it first.
    /// Only that runner may claim the run, unless it is disabled or deleted while the run
    /// waits: then any runner may, so a run never waits on a machine that cannot come back.
    /// </summary>
    public Guid? RequestedRunnerId { get; set; }

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
    /// it would lose its work. A follow-up run is the one exception that changes it: it is
    /// dispatched on the earlier run's branch, and the runner reports the fresh branch it
    /// started instead when that run's pull request was merged or closed.
    /// </summary>
    public required string BranchName { get; set; }

    /// <summary>Snapshot of the playbook delivery mode, unaffected by later edits.</summary>
    public bool WorkOnDefaultBranch { get; init; }

    public int MaxMinutes { get; set; }

    public DateTimeOffset QueuedAt { get; init; }

    /// <summary>
    /// When a person asked the run to start, in UTC, or null to start as soon as a runner is
    /// free. The run stays <see cref="RunStatus.Queued"/> and no runner claims it before then;
    /// its wait counts from here rather than from <see cref="QueuedAt"/>.
    /// </summary>
    public DateTimeOffset? ScheduledFor { get; init; }

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

    /// <summary>
    /// The harness's own session (Claude's session id, Codex's thread id, OpenCode's session
    /// id), reported by the runner. It is what a continue run resumes - on the same runner,
    /// whose machine holds the conversation and the kept checkout.
    /// </summary>
    public string? SessionId { get; set; }

    /// <summary>
    /// The absolute path of the run's checkout on its runner, reported with the session so a
    /// person can resume the session there by hand. The runner keeps it for a few days after
    /// the run; the server cannot tell when it is gone.
    /// </summary>
    public string? WorkspacePath { get; set; }

    /// <summary>The failed run this one picks up from, or null for a run that starts fresh.</summary>
    public Guid? ContinuesRunId { get; init; }

    /// <summary>The server queued this continue run itself, after a transient failure.</summary>
    public bool AutoContinued { get; init; }

    /// <summary>Automatic continues in this run's chain so far, this run included; capped at <see cref="MaxAutoContinues"/>.</summary>
    public int AutoContinues { get; init; }

    /// <summary>
    /// When the sweeper should queue an automatic continue of this failed run. Set when the run
    /// fails transiently with a session and the chain has continues left; cleared as soon as
    /// the sweeper acts on it, whatever the outcome.
    /// </summary>
    public DateTimeOffset? AutoContinueDueAt { get; set; }

    /// <summary>
    /// The comment that asked for this run by mentioning its agent, or null for a run a person
    /// started from the item or a rule dispatched. The outcome is the agent's reply in its thread.
    /// </summary>
    public Guid? TriggerCommentId { get; init; }

    /// <summary>
    /// The earlier implement run of the same agent on the item that this one follows up, or
    /// null. A follow-up goes to that run's runner, resumes its session where the runner kept
    /// it, and delivers on its branch - or on a fresh one, when its pull request has closed.
    /// </summary>
    public Guid? FollowsUpRunId { get; init; }

    /// <summary>What the person asked for, verbatim; set exactly when <see cref="TriggerCommentId"/> is.</summary>
    public string? Instruction { get; init; }

    public uint Version { get; set; }

    public bool IsFollowUp => FollowsUpRunId is not null;

    public bool IsLive => Status < RunStatus.Succeeded;

    public bool IsTerminal => Status >= RunStatus.Succeeded;

    /// <summary>
    /// Ended without finishing its work, with a session a continue run can resume on the
    /// runner that has it. Whether the item has moved on since is the caller's question.
    /// </summary>
    public bool HasResumableSession =>
        Status is RunStatus.Failed or RunStatus.TimedOut
        && SessionId is not null && RunnerId is not null
        && FailureReason != SessionUnavailable;

    /// <summary>
    /// Failures worth trying again without a person: a rate limit, an overloaded or unreachable
    /// model API, a harness that crashed. Not a cancel, a time limit or a refused prompt.
    /// </summary>
    public static bool IsTransientFailure(string? failureReason) =>
        failureReason is "harness-rate-limited" or "harness-transient" or "harness-crashed";

    /// <summary>
    /// Schedules the automatic continue of a run that just failed, when it qualifies: a
    /// transient failure, a session to resume, no cancel asked for, and continues left in its
    /// chain. Waits 1 minute before the first and 5 before the second - a rate limit needs time.
    /// </summary>
    public void ScheduleAutoContinue(DateTimeOffset now)
    {
        if (Status == RunStatus.Failed && IsTransientFailure(FailureReason) && HasResumableSession
            && CancelRequestedAt is null && AutoContinues < MaxAutoContinues)
        {
            AutoContinueDueAt = now + (AutoContinues == 0 ? TimeSpan.FromMinutes(1) : TimeSpan.FromMinutes(5));
        }
    }

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

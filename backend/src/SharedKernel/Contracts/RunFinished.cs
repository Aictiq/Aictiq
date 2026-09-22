using Aictiq.SharedKernel.Domain;

namespace Aictiq.SharedKernel.Contracts;

/// <summary>The terminal outcomes a run can report. Crossing modules as strings keeps the
/// vocabulary on the wire where both the runner protocol and the item's history can say it.</summary>
public static class RunOutcomes
{
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string TimedOut = "timed_out";
}

/// <summary>
/// A factory run reached a terminal state. Automation commits this with the run's own
/// terminal update; the WorkItems handler in Workers does the rest of the outcome's work
/// - link the pull request, move the item to the playbook's success or failure state when
/// the workflow allows it, release the claim, and leave the comment that says what
/// happened. Everything the item needs to know about a run crosses here, once, and the
/// handler is idempotent on <see cref="DomainEvent.EventId"/>.
/// </summary>
public sealed record RunFinished(
    Guid OrganizationId,
    Guid ProjectId,
    Guid ItemId,
    string ItemKey,
    Guid RunId,
    string AgentId,
    string Outcome,
    Guid? OnSuccessStateId,
    Guid? OnFailureStateId,
    string? Summary,
    string? PullRequestUrl,
    string? FailureReason) : DomainEvent, IIntegrationEvent;

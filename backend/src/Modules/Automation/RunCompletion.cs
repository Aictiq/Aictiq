using Aictiq.Modules.Automation.Domain;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Outbox;
using Microsoft.EntityFrameworkCore;

namespace Aictiq.Modules.Automation;

/// <summary>
/// Stages <see cref="RunFinished"/> for a terminal run. Every path that ends a run - the
/// runner's finish call, cancelling a queued run, the sweeper's lost-runner and timeout
/// verdicts - crosses here, so the event the item's history reacts to always carries the
/// same shape. The playbook's outcome states are read from its <em>current</em> row: a
/// playbook deleted mid-run sends nulls, which the WorkItems handler reads as "leave the
/// item where it is".
///
/// A refine run sends no states at all and settles its item's refinement here too: one that
/// ends without having submitted an answer leaves it <see cref="RefinementStatus.Failed"/>, so
/// the person waiting on it can ask again instead of watching "Refining" forever.
/// </summary>
internal static class RunCompletion
{
    public static async Task StageAsync(AutomationDbContext db, Run run, string outcome,
        string? summary, string? pullRequestUrl, string? failureReason, CancellationToken cancellationToken)
    {
        var refine = run.Kind == RunKind.Refine;
        var playbook = refine ? null : await db.Playbooks.AsNoTracking()
            .SingleOrDefaultAsync(playbook => playbook.Id == run.PlaybookId, cancellationToken);
        var refinement = refine ? await db.Refinements.SingleOrDefaultAsync(
            row => row.ItemId == run.ItemId && row.LastRunId == run.Id, cancellationToken) : null;
        db.Set<OutboxMessage>().Add(OutboxMessage.From(new RunFinished(
            run.OrganizationId, run.ProjectId, run.ItemId, run.ItemKey, run.Id, run.AgentUserId,
            outcome, playbook?.OnSuccessStateId, playbook?.OnFailureStateId,
            summary, pullRequestUrl, failureReason)
        {
            Refinement = refine, TriggerCommentId = run.TriggerCommentId, RequestedBy = run.RequestedBy,
            NeedsInput = refinement is { Status: RefinementStatus.NeedsInput }
        }));

        if (refinement is { Status: RefinementStatus.Refining })
        {
            refinement.Status = RefinementStatus.Failed;
            refinement.Questions = [];
            refinement.Summary = outcome == RunOutcomes.Succeeded
                ? "The run finished without submitting a refined ticket."
                : null;
            refinement.UpdatedAt = run.FinishedAt ?? DateTimeOffset.UtcNow;
        }
    }
}

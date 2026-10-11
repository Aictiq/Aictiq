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
/// A run in a playbook chain lets <see cref="PlaybookChain"/> decide: while the chain goes on
/// the next step is queued here and the item stays where it is; when it ends the item goes to
/// the builder playbook's success state.
///
/// A refine or chat run sends no states at all, so its item stays where it is. A refine run
/// also settles its item's refinement here: one that ends without having submitted an answer
/// leaves it <see cref="RefinementStatus.Failed"/>, so the person waiting on it can ask again
/// instead of watching "Refining" forever.
/// </summary>
internal static class RunCompletion
{
    public static async Task StageAsync(AutomationDbContext db, Run run, string outcome,
        string? summary, string? pullRequestUrl, string? failureReason, CancellationToken cancellationToken)
    {
        var refine = run.Kind == RunKind.Refine;
        var chat = run.Kind == RunKind.Chat;
        var playbook = refine || chat ? null : await db.Playbooks.AsNoTracking()
            .SingleOrDefaultAsync(playbook => playbook.Id == run.PlaybookId, cancellationToken);
        var refinement = refine ? await db.Refinements.SingleOrDefaultAsync(
            row => row.ItemId == run.ItemId && row.LastRunId == run.Id, cancellationToken) : null;
        var chain = refine || chat ? null
            : await PlaybookChain.DecideAsync(db, run, outcome, run.FinishedAt ?? DateTimeOffset.UtcNow, cancellationToken);
        if (chain?.Next is { } next)
        {
            db.ChainSteps.Add(next);
        }

        // A stage's own playbook states mean nothing to the item: the chain either goes on, and
        // the item stays, or ends at the builder playbook's success state, whatever the outcome.
        var stage = run.Kind == RunKind.Stage;
        var onSuccess = stage ? chain?.ReviewStateId : playbook?.OnSuccessStateId;
        var onFailure = stage ? chain?.ReviewStateId : playbook?.OnFailureStateId;
        db.Set<OutboxMessage>().Add(OutboxMessage.From(new RunFinished(
            run.OrganizationId, run.ProjectId, run.ItemId, run.ItemKey, run.Id, run.AgentUserId,
            outcome, onSuccess, onFailure,
            summary, pullRequestUrl, failureReason)
        {
            Refinement = refine, Chat = chat, TriggerCommentId = run.TriggerCommentId, RequestedBy = run.RequestedBy,
            NeedsInput = refinement is { Status: RefinementStatus.NeedsInput },
            Refined = refinement is { Status: RefinementStatus.Ready },
            Chained = chain?.Next is not null,
            ChainNote = chain?.Note,
            StageName = stage ? chain?.StageName ?? "Follow-up" : null,
            StageVerdict = PlaybookChain.VerdictName(run.StageVerdict),
            StageFindings = run.StageVerdict == StageVerdict.ChangesRequested ? run.StageFindings : null,
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

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
/// </summary>
internal static class RunCompletion
{
    public static async Task StageAsync(AutomationDbContext db, Run run, string outcome,
        string? summary, string? pullRequestUrl, string? failureReason, CancellationToken cancellationToken)
    {
        var playbook = await db.Playbooks.AsNoTracking()
            .SingleOrDefaultAsync(playbook => playbook.Id == run.PlaybookId, cancellationToken);
        db.Set<OutboxMessage>().Add(OutboxMessage.From(new RunFinished(
            run.OrganizationId, run.ProjectId, run.ItemId, run.ItemKey, run.Id, run.AgentUserId,
            outcome, playbook?.OnSuccessStateId, playbook?.OnFailureStateId,
            summary, pullRequestUrl, failureReason)));
    }
}

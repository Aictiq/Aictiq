using Aictiq.Modules.WorkItems.Domain;
using Aictiq.SharedKernel.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Aictiq.Modules.WorkItems.Contracts;

/// <summary>
/// The WorkItems half of <see cref="IWorkItemClaims"/>. The contract exists so
/// Automation's dispatcher gets the claim decision back synchronously — the caller is
/// waiting on it, exactly like <c>ITeamUsage</c>. After the CAS it raises the same
/// <c>ItemChanged</c> the claim endpoint does, so boards and webhooks see an agent pick
/// the item up; everything that happens after the run travels by <see cref="RunFinished"/>.
///
/// The compare-and-swap is the same one the claim endpoint and the <c>claim_item</c>
/// MCP tool use: claim, assignment and the move to the workflow's first reachable Active
/// state are one UPDATE the database arbitrates, so two dispatchers racing on one item
/// produce exactly one claim.
/// </summary>
internal sealed class WorkItemClaims(WorkItemsDbContext db, IConfiguration configuration, TimeProvider clock) : IWorkItemClaims
{
    public async Task<WorkItemClaimResult> ClaimForAsync(Guid itemId, string agentUserId, CancellationToken cancellationToken = default)
    {
        var item = await db.Items.AsNoTracking().SingleOrDefaultAsync(x => x.Id == itemId, cancellationToken);
        if (item is null) return new WorkItemClaimResult(WorkItemClaimOutcome.NotFound, 0, null);

        // The lowest-position Active state the item's workflow can actually reach from
        // where it is — the query the Claim endpoint runs, verbatim.
        var activeState = await db.WorkflowStates.AsNoTracking()
            .Where(s => s.Category == WorkflowStateCategory.Active &&
                db.Workflows.Any(w => w.Id == s.WorkflowId && w.ProjectId == item.ProjectId) &&
                (!db.WorkflowTransitions.Any(t => t.WorkflowId == s.WorkflowId) ||
                 db.WorkflowTransitions.Any(t => t.WorkflowId == s.WorkflowId && t.ToStateId == s.Id && (t.FromStateId == null || t.FromStateId == item.StateId))))
            .OrderBy(s => s.Position).Select(s => (Guid?)s.Id).FirstOrDefaultAsync(cancellationToken);
        if (activeState is null) return new WorkItemClaimResult(WorkItemClaimOutcome.Conflict, item.Version, item.ClaimedBy);

        var staleAfter = Math.Max(1, configuration.GetValue("Claims:StaleAfterMinutes", 30));
        var now = clock.GetUtcNow();
        // One UPDATE the database arbitrates: observing an unclaimed row and then
        // assigning it through EF would make a concurrent claim race possible. No version
        // token here — the caller has no item row to have read one from, so the stale-claim
        // predicate alone decides.
        var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE work.items
            SET claimed_by = {agentUserId}, claimed_at = {now}, claim_heartbeat_at = {now},
                assignee_id = {agentUserId}, state_id = {activeState.Value}, updated_at = {now}
            WHERE id = {item.Id}
              AND (claimed_by IS NULL OR claim_heartbeat_at < {now - TimeSpan.FromMinutes(staleAfter)})
            """, cancellationToken);
        if (affected == 1)
        {
            db.ChangeTracker.Clear();
            var claimed = await db.Items.FirstAsync(x => x.Id == item.Id, cancellationToken);
            claimed.Changed(agentUserId, "claim");
            await db.SaveChangesAsync(cancellationToken);
            return new WorkItemClaimResult(WorkItemClaimOutcome.Claimed, claimed.Version, agentUserId);
        }

        // A live claim (or a race loss). The lookup is bounded by the id we already
        // resolved in-tenant — the same shape the Claim endpoint uses for its conflict
        // response — so the row it names cannot be one outside the caller's organization.
        var current = await db.Items.AsNoTracking().IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == itemId, cancellationToken);
        return current is null
            ? new WorkItemClaimResult(WorkItemClaimOutcome.NotFound, 0, null)
            : new WorkItemClaimResult(WorkItemClaimOutcome.Conflict, current.Version, current.ClaimedBy);
    }
}

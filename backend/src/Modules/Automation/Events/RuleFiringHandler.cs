using Aictiq.Modules.Automation.Domain;
using Aictiq.Modules.WorkItems.Contracts;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Events;
using Aictiq.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Aictiq.Modules.Automation.Events;

/// <summary>
/// The conveyor belt behind a workflow's stations: an item enters a state, and
/// every enabled rule watching that state — with a matching label, when it names one —
/// gets exactly one attempt to dispatch a run for it. Runs in Workers, on
/// <see cref="WorkItemTransitioned"/> and only that event: <c>ItemChanged</c> fires on
/// every field, and a rule must not refire on an edit that never touched the state.
///
/// The firing row (<c>automation.rule_firings</c>) is the idempotency key and the record
/// of the attempt in one: it is inserted <c>ON CONFLICT DO NOTHING</c> before the dispatch
/// is even attempted, in the same transaction the dispatch's <see cref="RunDispatcher"/>
/// commits in, so a replayed event either finds its row already there and does nothing, or
/// the whole attempt — firing and run together — rolls back and the next delivery gets a
/// real try. A dispatch outcome that is not <see cref="DispatchOutcome.Created"/> is
/// recorded as a skip, never thrown: only a rule that never got to decide should be retried
/// by the outbox.
/// </summary>
public sealed class RuleFiringHandler(
    AutomationDbContext db, ICurrentTenant currentTenant, IWorkItemLookup items, IWorkItemLabels labels,
    IProjectAccess access, IOrganizationBillingState billing, RunDispatcher dispatcher,
    TimeProvider clock, ILogger<RuleFiringHandler> logger)
    : IDomainEventHandler<WorkItemTransitioned>
{
    public async Task HandleAsync(WorkItemTransitioned @event, CancellationToken cancellationToken)
    {
        using var tenant = currentTenant is AmbientCurrentTenant ambient ? ambient.Use(@event.OrganizationId) : null;

        var candidates = await db.Rules.AsNoTracking()
            .Where(rule => rule.ProjectId == @event.ProjectId && rule.Enabled && rule.TriggerStateId == @event.ToStateId)
            .ToListAsync(cancellationToken);
        if (candidates.Count == 0)
        {
            return;
        }

        var item = (await items.FindByKeysAsync(@event.ProjectId, [@event.Key], cancellationToken)).FirstOrDefault();
        if (item is null)
        {
            // The item moved and then vanished (deleted, or its project was) before this
            // handler ran. WorkItemsDeleted removes any firing already recorded for it;
            // there is nothing here to fire a new one for.
            return;
        }

        var project = await access.GetProjectAsync(@event.ProjectId, cancellationToken);
        // Which read-only this is, not merely that it is one: an archived project and a
        // lapsed organization have different remedies, and the firing row is where someone
        // goes to find out why nothing ran.
        var readOnlyReason = project is null || project.IsArchived
            ? RuleSkipReasons.ProjectReadOnly
            : await billing.IsReadOnlyAsync(@event.OrganizationId, cancellationToken)
                ? RuleSkipReasons.OrganizationReadOnly
                : null;

        foreach (var rule in candidates)
        {
            await FireAsync(rule, @event, item, project, readOnlyReason, cancellationToken);
            // Each rule gets its own transaction (below); clearing here keeps one rule's
            // tracked entities from bleeding into the next in this same DbContext instance.
            db.ChangeTracker.Clear();
        }
    }

    private async Task FireAsync(
        Rule rule, WorkItemTransitioned @event, WorkItemReference item, ProjectRef? project, string? readOnlyReason,
        CancellationToken ct)
    {
        if (rule.RequiredLabelId is { } labelId && !await labels.ItemHasLabelAsync(item.Id, labelId, ct))
        {
            // No firing row at all: the label never matched, so this was never this rule's
            // transition — not something to remember as a skip.
            return;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // The firing row is written before the attempt, with neither a run nor a skip
        // reason: ck_rule_firings_run_or_skip only forbids both. The UPDATE below fills in
        // one of them before this transaction commits, so no reader ever sees it undecided.
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO automation.rule_firings (rule_id, item_id, event_id, organization_id, item_key, at)
            VALUES ({rule.Id}, {item.Id}, {@event.EventId}, {@event.OrganizationId}, {item.Key}, {clock.GetUtcNow()})
            ON CONFLICT DO NOTHING
            """, ct);
        if (inserted == 0)
        {
            // Already recorded by an earlier delivery of this event — the idempotency key
            // doing its job. No commit needed; the transaction disposes as a no-op rollback.
            return;
        }

        var skipReason = readOnlyReason
            ?? (await IsLoopAsync(rule, item.Id, @event.ActorId, ct) ? RuleSkipReasons.RuleLoop : null);

        DispatchResult? result = null;
        if (skipReason is null)
        {
            if (project is null)
            {
                skipReason = RuleSkipReasons.ItemNotFound;
            }
            else
            {
                result = await dispatcher.DispatchAsync(
                    project, item.Key, rule.PlaybookId, rule.AgentUserId, DispatchActor.Rule(rule.Id), ct);
                skipReason = result.Outcome switch
                {
                    DispatchOutcome.Created => null,
                    DispatchOutcome.ItemNotFound => RuleSkipReasons.ItemNotFound,
                    DispatchOutcome.PlaybookNotFound => RuleSkipReasons.PlaybookPageMissing,
                    DispatchOutcome.Validation when result.Field == "agentId" => RuleSkipReasons.AgentUnavailable,
                    DispatchOutcome.Validation => RuleSkipReasons.Invalid,
                    DispatchOutcome.ItemClaimed => RuleSkipReasons.ItemClaimed,
                    DispatchOutcome.RunInProgress => RuleSkipReasons.RunInProgress,
                    DispatchOutcome.ReadOnly => RuleSkipReasons.OrganizationReadOnly,
                    _ => RuleSkipReasons.Invalid,
                };
            }
        }

        var runId = result?.Outcome == DispatchOutcome.Created ? result.Run!.Id : (Guid?)null;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE automation.rule_firings SET run_id = {runId}, skip_reason = {(runId is null ? skipReason : null)}
            WHERE rule_id = {rule.Id} AND item_id = {item.Id} AND event_id = {@event.EventId}
            """, ct);

        // RunDispatcher publishes "run.changed" from its own SaveChangesAsync, which — inside
        // this still-open transaction — has not committed yet: it goes out on a *separate*
        // connection (PostgresRealtimePublisher), so a listener could in principle see it a
        // moment before this commit lands, or (if the commit below somehow failed) for a run
        // that turned out not to exist. Accepted rather than restructured: the UI treats the
        // event as "go refetch", never as the payload of record, and the only step left after
        // the dispatch is this single keyed UPDATE, so the rollback window is negligible.
        await transaction.CommitAsync(ct);

        if (runId is { } started)
        {
            logger.LogInformation("Rule {RuleId} started run {RunId} for {ItemKey}", rule.Id, started, item.Key);
        }
        else
        {
            // Expected traffic, not a fault — item-claimed above all, when two rules or a
            // person race the same item. Never above Information.
            logger.LogInformation("Rule {RuleId} skipped {ItemKey}: {SkipReason}", rule.Id, item.Key, skipReason);
        }
    }

    /// <summary>
    /// True when this transition was the rule's own run finishing and moving the item back
    /// into the very state the rule watches (a playbook whose failure state is its trigger
    /// state, for instance) — without this, RunFinished would hand the item straight back
    /// to the same rule forever.
    /// </summary>
    private async Task<bool> IsLoopAsync(Rule rule, Guid itemId, string actorId, CancellationToken ct)
    {
        var lastRun = await db.Runs.AsNoTracking().Where(run => run.ItemId == itemId)
            .OrderByDescending(run => run.QueuedAt)
            .Select(run => new { run.RuleId, run.AgentUserId })
            .FirstOrDefaultAsync(ct);
        return lastRun is not null && lastRun.RuleId == rule.Id && lastRun.AgentUserId == actorId;
    }
}

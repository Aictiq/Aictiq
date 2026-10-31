using Aictiq.Modules.Automation.Domain;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Outbox;
using Aictiq.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Aictiq.Modules.Automation;

/// <summary>
/// Starts the waiting steps of playbook chains (<see cref="ChainStep"/>, decided by
/// <see cref="PlaybookChain"/>). A step waits for the item the way a mention does: until no run
/// holds it and the finished run's outcome has reached it, so the builder and its stages never
/// run at the same time and never push to the branch together. A step that can never start -
/// its playbook was deleted, the organization went read-only, someone held the item for a day -
/// is dropped, and the item goes to human review with the reason: a chain never blocks its ticket.
/// </summary>
public sealed class ChainQueue(
    AutomationDbContext db, IProjectAccess access, RunDispatcher dispatcher, ICurrentTenant tenant,
    TimeProvider clock, ILogger<ChainQueue> logger, IWorkItemLabels labels)
{
    private const int BatchSize = 50;

    /// <summary>Starts the waiting step of every item that has one and is free.</summary>
    public async Task SweepAsync(CancellationToken ct)
    {
        var itemIds = await db.ChainSteps.AsNoTracking()
            .Where(step => step.Status == ChainStepStatus.Pending)
            .GroupBy(step => step.ItemId)
            .OrderBy(group => group.Min(step => step.CreatedAt))
            .Select(group => group.Key)
            .Take(BatchSize)
            .ToListAsync(ct);
        foreach (var itemId in itemIds)
        {
            try
            {
                await StartNextAsync(itemId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                db.ChangeTracker.Clear();
                logger.LogError(ex, "Could not start the next chain step of item {ItemId}", itemId);
            }
        }
    }

    /// <summary>Starts the item's waiting step, or drops it and says why. Returns true when a run started.</summary>
    public async Task<bool> StartNextAsync(Guid itemId, CancellationToken ct)
    {
        if (await db.Runs.AnyAsync(run => run.ItemId == itemId && run.Status < RunStatus.Succeeded, ct)
            || await RunMentionQueue.RunFinishPendingAsync(db, itemId, ct))
        {
            return false;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var organizationId = tenant.OrganizationId!.Value;
        var step = await db.ChainSteps.FromSqlInterpolated($"""
                SELECT *, xmin FROM automation.chain_steps
                WHERE item_id = {itemId} AND organization_id = {organizationId} AND status = 0
                ORDER BY created_at, id
                LIMIT 1
                FOR UPDATE SKIP LOCKED
                """)
            .FirstOrDefaultAsync(ct);
        if (step is null)
        {
            return false;
        }

        var now = clock.GetUtcNow();
        var (started, drop) = await TryStartAsync(step, now, ct);
        if (started is null && drop is null)
        {
            // Busy after all - a person holds the claim, or a run slipped in. It waits.
            await transaction.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            return false;
        }

        step.DecidedAt = now;
        if (started is not null)
        {
            step.Status = ChainStepStatus.Started;
            step.RunId = started.Id;
        }
        else
        {
            step.Status = ChainStepStatus.Dropped;
            step.DropReason = drop!.Length > ChainStep.MaxDropReasonLength
                ? drop[..(ChainStep.MaxDropReasonLength - 1)] + "…"
                : drop;
            await StageStoppedAsync(step, now, ct);
            logger.LogInformation("Chain step {StepId} on {ItemKey} was dropped: {Reason}", step.Id, step.ItemKey, drop);
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        db.ChangeTracker.Clear();
        return started is not null;
    }

    /// <returns>The run that started; or why none ever will; or neither, when the item is only busy.</returns>
    private async Task<(Run? Started, string? Drop)> TryStartAsync(ChainStep step, DateTimeOffset now, CancellationToken ct)
    {
        var project = await access.GetProjectAsync(step.ProjectId, ct);
        if (project is null)
        {
            return (null, "The project is gone.");
        }
        if (project.IsArchived)
        {
            return (null, $"{project.Key} is archived, so nothing runs on it until it is un-archived.");
        }
        if (step.WaitForMergeUrls.Length > 0)
        {
            // A repository step that waits for a merge waits for GitHub, not for the item.
            var states = await labels.PullRequestStatesAsync(step.ItemId, ct);
            var open = new List<string>();
            foreach (var url in step.WaitForMergeUrls)
            {
                var state = states.GetValueOrDefault(url.TrimEnd('/'));
                if (state == "closed")
                {
                    return (null, $"The pull request {url} was closed without merging, so {string.Join(", ", step.RepositoryAliases)} did not start.");
                }
                if (state != "merged")
                {
                    open.Add(url);
                }
            }
            if (open.Count > 0)
            {
                return now - step.CreatedAt > PlaybookChain.MergeMaxWait
                    ? (null, $"The pull request {open[0]} did not merge within {PlaybookChain.MergeMaxWait.TotalDays:0} days, so the chain stopped waiting.")
                    : (null, null);
            }
        }
        else if (now - step.CreatedAt > RunMention.MaxWait)
        {
            return (null, "Someone else held the item for a day, so the chain stopped waiting.");
        }

        var after = await db.Runs.AsNoTracking().SingleOrDefaultAsync(run => run.Id == step.AfterRunId, ct);
        var chain = await db.Playbooks.AsNoTracking().SingleOrDefaultAsync(row => row.Id == step.ChainPlaybookId, ct);
        if (after is null || chain is null)
        {
            return (null, "The chain's builder playbook was deleted.");
        }

        // The builder's latest run: the stage before this one checked its work, or it just finished.
        var builder = after.Kind == RunKind.Stage && after.FollowsUpRunId is { } builderRunId
            ? await db.Runs.AsNoTracking().SingleOrDefaultAsync(run => run.Id == builderRunId, ct)
            : after;
        if (builder is null)
        {
            return (null, "The builder's run is gone.");
        }

        DispatchResult result;
        if (step.Kind == ChainStepKind.Repositories)
        {
            if (after.RepositoryPlan is not { } plan)
            {
                return (null, "The run before lost its repository plan.");
            }
            var actor = after.RequestedBy is { } requestedBy
                ? DispatchActor.User(requestedBy)
                : DispatchActor.Rule(after.RuleId!.Value);
            result = await dispatcher.DispatchAsync(
                project, step.ItemKey, after.PlaybookId, after.AgentUserId, runnerId: null, actor, ct,
                requiredTags: after.RequiredTags,
                wave: new RepositoryWaveDispatch(after.RepositoryRootRunId ?? after.Id, plan, step.RepositoryAliases));
        }
        else if (step.Kind == ChainStepKind.Stage)
        {
            var stages = await PlaybookChain.StagesAsync(db, chain.Id, ct);
            if (step.StageIndex >= stages.Count)
            {
                return (null, $"{chain.Name}'s follow-up playbooks changed while the chain ran.");
            }
            var stage = await db.Playbooks.AsNoTracking().SingleAsync(row => row.Id == stages[step.StageIndex].Id, ct);
            result = await dispatcher.DispatchStageAsync(project, builder, chain, stage, step.StageIndex, stages.Count, ct);
        }
        else
        {
            var stageName = await db.Playbooks.AsNoTracking().Where(row => row.Id == after.PlaybookId)
                .Select(row => row.Name).SingleOrDefaultAsync(ct) ?? "Follow-up";
            var findings = after.StageFindings ?? after.OutcomeSummary ?? "(The follow-up playbook gave no findings; read its comments on the pull request.)";
            var round = await PlaybookChain.ReworkRoundsAsync(db, step.ItemId, ct) + 1;
            var actor = builder.RequestedBy is { } requestedBy
                ? DispatchActor.User(requestedBy)
                : DispatchActor.Rule(builder.RuleId!.Value);
            result = await dispatcher.DispatchAsync(
                project, step.ItemKey, chain.Id, builder.AgentUserId, runnerId: null, actor, ct,
                rework: new ReworkDispatch(after.Id, stageName, findings, round));
        }

        return result.Outcome switch
        {
            DispatchOutcome.Created => (result.Run, null),
            DispatchOutcome.ItemClaimed or DispatchOutcome.RunInProgress => (null, null),
            DispatchOutcome.ItemNotFound => (null, "The item is gone."),
            DispatchOutcome.ReadOnly => (null,
                "This organization is read-only - the evaluation has ended, a payment is unresolved or the free plan's people limit is exceeded - so agent runs are paused."),
            DispatchOutcome.PlaybookNotFound => (null,
                "A playbook of the chain is gone, or its page cannot be read by the person who started the builder."),
            _ => (null, result.Message ?? "The next run could not be dispatched."),
        };
    }

    /// <summary>
    /// Sends the item to the builder playbook's success state with the reason, through the same
    /// event a finished run sends - which is what the item's side already knows how to apply.
    /// </summary>
    private async Task StageStoppedAsync(ChainStep step, DateTimeOffset now, CancellationToken ct)
    {
        var after = await db.Runs.AsNoTracking().SingleOrDefaultAsync(run => run.Id == step.AfterRunId, ct);
        if (after is null)
        {
            return;
        }
        var review = await db.Playbooks.AsNoTracking().Where(row => row.Id == step.ChainPlaybookId)
            .Select(row => row.OnSuccessStateId).SingleOrDefaultAsync(ct);
        db.Set<OutboxMessage>().Add(OutboxMessage.From(new RunFinished(
            step.OrganizationId, step.ProjectId, step.ItemId, step.ItemKey, after.Id, after.AgentUserId,
            RunOutcomes.Succeeded, review, review, null, null, null)
        {
            OccurredAt = now, RequestedBy = after.RequestedBy,
            ChainStopped = true, ChainNote = $"{step.DropReason} The item goes to human review.",
        }));
    }
}

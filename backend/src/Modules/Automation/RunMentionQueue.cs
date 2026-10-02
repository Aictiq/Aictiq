using Aictiq.Modules.Automation.Domain;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Outbox;
using Aictiq.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Aictiq.Modules.Automation;

/// <summary>
/// The comment door to the factory: a comment that mentions an agent asks it to work on the
/// item, and the comment's text is what it is asked. A mention is recorded first
/// (<see cref="RunMention"/>) and started second, so the one-run-per-item rule needs no
/// exception: a mention made while the item is busy simply waits its turn, and the sweeper
/// starts the oldest waiting mention as soon as the item is free.
///
/// Who may ask is the dispatch door's rule, not the comment box's: a project Member who may
/// operate the factory. Anyone else's mention - a stakeholder's, a Guest's, another agent's -
/// is left to do what a mention always did, which is notify.
/// </summary>
public sealed class RunMentionQueue(
    AutomationDbContext db, IProjectAccess access, IUserDirectory directory, IWorkItemComments comments,
    RunDispatcher dispatcher, ICurrentTenant tenant, TimeProvider clock,
    ILogger<RunMentionQueue> logger)
{
    private const int BatchSize = 50;

    private const string RunFinishedType = "Aictiq.SharedKernel.Contracts.RunFinished";

    /// <summary>
    /// Records the agents <paramref name="mentionedUserIds"/> names as asked to work by
    /// <paramref name="commentId"/>, then starts the item's next mention if the item is free.
    /// Replays record nothing new: the (comment, agent) pair is unique.
    /// </summary>
    public async Task RecordAsync(
        Guid projectId, Guid itemId, string itemKey, Guid commentId, string authorId,
        IReadOnlyList<string> mentionedUserIds, DateTimeOffset at, CancellationToken ct)
    {
        if (mentionedUserIds.Count == 0)
        {
            return;
        }

        var agentIds = await directory.FilterAgentsAsync([.. mentionedUserIds, authorId], ct);
        // An agent mentioning an agent - itself in its own reply, above all - must not start a loop.
        if (agentIds.Contains(authorId))
        {
            return;
        }

        var asked = mentionedUserIds.Where(agentIds.Contains).Distinct(StringComparer.Ordinal).ToArray();
        if (asked.Length == 0 || !await MayAskAsync(authorId, projectId, ct))
        {
            return;
        }

        var organizationId = tenant.OrganizationId!.Value;
        foreach (var agentId in asked)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO automation.run_mentions
                    (id, organization_id, project_id, item_id, item_key, comment_id, agent_user_id, requested_by, created_at, status)
                VALUES ({Guid.CreateVersion7()}, {organizationId}, {projectId}, {itemId}, {itemKey}, {commentId},
                    {agentId}, {authorId}, {at}, {(short)RunMentionStatus.Pending})
                ON CONFLICT (comment_id, agent_user_id) DO NOTHING
                """, ct);
        }

        await StartNextAsync(itemId, ct);
    }

    /// <summary>Starts the oldest waiting mention of every item that has one and is free.</summary>
    public async Task SweepAsync(CancellationToken ct)
    {
        var itemIds = await db.RunMentions.AsNoTracking()
            .Where(mention => mention.Status == RunMentionStatus.Pending)
            .GroupBy(mention => mention.ItemId)
            .OrderBy(group => group.Min(mention => mention.CreatedAt))
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
                logger.LogError(ex, "Could not start the next mention of item {ItemId}", itemId);
            }
        }
    }

    /// <summary>
    /// Starts the item's oldest waiting mention, or decides it can never start and says why.
    /// Leaves it waiting while a run or a claim holds the item. Returns true when a run started.
    /// </summary>
    public async Task<bool> StartNextAsync(Guid itemId, CancellationToken ct)
    {
        if (await db.Runs.AnyAsync(run => run.ItemId == itemId && run.Status < RunStatus.Succeeded, ct)
            || await RunFinishPendingAsync(itemId, ct))
        {
            return false;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var organizationId = tenant.OrganizationId!.Value;
        // Locked, so two sweepers - or a sweeper and a new comment - never both act on one mention.
        var mention = await db.RunMentions.FromSqlInterpolated($"""
                SELECT *, xmin FROM automation.run_mentions
                WHERE item_id = {itemId} AND organization_id = {organizationId} AND status = 0
                ORDER BY created_at, id
                LIMIT 1
                FOR UPDATE SKIP LOCKED
                """)
            .FirstOrDefaultAsync(ct);
        if (mention is null)
        {
            return false;
        }

        var now = clock.GetUtcNow();
        var (started, refusal, reply) = await TryStartAsync(mention, now, ct);
        if (started is null && refusal is null)
        {
            // Busy after all - a person holds the claim, or a run slipped in. It waits.
            await transaction.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            return false;
        }

        mention.DecidedAt = now;
        if (started is not null)
        {
            mention.Status = RunMentionStatus.Started;
            mention.RunId = started.Id;
        }
        else
        {
            mention.Status = RunMentionStatus.Refused;
            mention.RefusalReason = refusal!.Length > RunMention.MaxRefusalReasonLength
                ? refusal[..(RunMention.MaxRefusalReasonLength - 1)] + "…"
                : refusal;
            if (reply)
            {
                db.Set<OutboxMessage>().Add(OutboxMessage.From(new RunMentionRefused(
                    mention.OrganizationId, mention.ProjectId, mention.ItemId, mention.CommentId,
                    mention.AgentUserId, mention.RefusalReason) { OccurredAt = now }));
            }
            logger.LogInformation("Mention {MentionId} of {AgentId} on {ItemKey} started nothing: {Reason}",
                mention.Id, mention.AgentUserId, mention.ItemKey, refusal);
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        db.ChangeTracker.Clear();
        return started is not null;
    }

    /// <returns>
    /// The run that started; or why none ever will, and whether the agent should say so in the
    /// thread; or neither, when the item is only busy and the mention should keep waiting.
    /// </returns>
    private async Task<(Run? Started, string? Refusal, bool Reply)> TryStartAsync(
        RunMention mention, DateTimeOffset now, CancellationToken ct)
    {
        var comment = await comments.FindAsync(mention.CommentId, ct);
        if (comment is null || comment.Deleted)
        {
            // Nobody is left to answer: the request went with its comment.
            return (null, "The comment was deleted before the run could start.", false);
        }

        var project = await access.GetProjectAsync(mention.ProjectId, ct);
        if (project is null)
        {
            return (null, "The project is gone.", false);
        }
        if (project.IsArchived)
        {
            return (null, $"{project.Key} is archived, so nothing runs on it until it is un-archived.", true);
        }
        // Waiting behind runs is the queue working; only a day's wait on an item nobody is
        // running - someone else's claim - gives up.
        var lastFinished = await db.Runs.Where(run => run.ItemId == mention.ItemId)
            .MaxAsync(run => run.FinishedAt, ct);
        var waitingSince = lastFinished > mention.CreatedAt ? lastFinished.Value : mention.CreatedAt;
        if (now - waitingSince > RunMention.MaxWait)
        {
            return (null, "The item stayed claimed by someone else for a day, so I gave up waiting. Mention me again once it is free.", true);
        }
        if (!await MayAskAsync(mention.RequestedBy, mention.ProjectId, ct))
        {
            return (null, "The person who asked can no longer start agent runs in this project.", true);
        }

        var requester = (await directory.GetAsync([mention.RequestedBy], ct))
            .GetValueOrDefault(mention.RequestedBy)?.DisplayName ?? mention.RequestedBy;
        var instruction = comment.BodyMarkdown.Trim();
        if (instruction.Length > Run.MaxInstructionLength)
        {
            instruction = instruction[..Run.MaxInstructionLength];
        }

        var result = await dispatcher.DispatchAsync(
            project, mention.ItemKey, playbookId: null, mention.AgentUserId, runnerId: null,
            DispatchActor.User(mention.RequestedBy), ct,
            mention: new MentionDispatch(mention.CommentId, instruction, requester));
        return result.Outcome switch
        {
            DispatchOutcome.Created => (result.Run, null, false),
            DispatchOutcome.ItemClaimed or DispatchOutcome.RunInProgress => (null, null, false),
            DispatchOutcome.ItemNotFound => (null, "The item is gone.", false),
            DispatchOutcome.ReadOnly => (null,
                "This organization is read-only - the evaluation has ended or a payment is unresolved - so agent runs are paused.", true),
            DispatchOutcome.PlaybookNotFound => (null,
                "This project has no playbook I can use, or its page cannot be read by the person who asked.", true),
            _ => (null, result.Message ?? "The run could not be dispatched.", true),
        };
    }

    /// <summary>The dispatch door's rule: an organization and project Member who may operate the factory.</summary>
    private async Task<bool> MayAskAsync(string userId, Guid projectId, CancellationToken ct)
    {
        var organizationId = tenant.OrganizationId!.Value;
        return await access.GetOrgRoleAsync(userId, organizationId, ct) is { } orgRole && orgRole.Satisfies(OrgRole.Member)
            && await access.GetProjectRoleAsync(userId, projectId, ct) is { } projectRole && projectRole.Satisfies(ProjectRole.Member)
            && await access.CanOperateFactoryAsync(userId, organizationId, ct);
    }

    /// <summary>
    /// Whether a finished run's outcome has yet to reach the item. Until it has, the item still
    /// carries that run's claim - and its move to the outcome state - so a run started now would
    /// lose its claim, and its state, to the old run's finish a moment later.
    /// </summary>
    private async Task<bool> RunFinishPendingAsync(Guid itemId, CancellationToken ct) =>
        await db.Database.SqlQuery<bool>($"""
            SELECT EXISTS (
                SELECT 1 FROM shared.outbox_messages
                WHERE type = {RunFinishedType} AND processed_at IS NULL AND dead_lettered_at IS NULL
                  AND payload::jsonb ->> 'ItemId' = {itemId.ToString()}) AS "Value"
            """).SingleAsync(ct);
}

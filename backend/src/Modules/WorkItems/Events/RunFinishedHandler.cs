using System.Text.Json;
using Aictiq.Modules.WorkItems.Domain;
using Aictiq.Modules.WorkItems.Endpoints;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Events;
using Aictiq.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Aictiq.Modules.WorkItems.Events;

/// <summary>
/// Does the item-side work of a finished factory run: moves the item to the playbook's
/// success or failure state when the workflow allows it, releases the claim, links the
/// pull request, and leaves the comment that says what happened. Failed, timed-out and
/// cancelled runs all use the playbook's failure state, when it names one.
///
/// Delivery is at-least-once, and the idempotency is a database constraint: the comment
/// row carries the event's own id under the <c>ux_comments_item_id_event_id</c> unique
/// index, so a replayed <see cref="RunFinished"/> collides on its insert and the whole
/// save - history rows, link, transition, comment - rolls back as the no-op it already was.
/// </summary>
public sealed class RunFinishedHandler(
    WorkItemsDbContext db, ICurrentTenant currentTenant, TimeProvider clock, ILogger<RunFinishedHandler> logger)
    : IDomainEventHandler<RunFinished>
{
    public async Task HandleAsync(RunFinished @event, CancellationToken cancellationToken)
    {
        using var tenant = currentTenant is AmbientCurrentTenant ambient ? ambient.Use(@event.OrganizationId) : null;
        var item = await db.Items.SingleOrDefaultAsync(x => x.Id == @event.ItemId, cancellationToken);
        if (item is null) return; // A deleted item lets its runs go.

        var now = clock.GetUtcNow();
        var actorId = @event.AgentId;

        var requestedTarget = @event.Outcome switch
        {
            RunOutcomes.Succeeded => @event.OnSuccessStateId,
            _ => @event.OnFailureStateId,
        };
        if (@event.Outcome == RunOutcomes.Succeeded && requestedTarget is null)
        {
            // No playbook success state named: fall back to the workflow's
            // lowest-position Resolved state, as PullRequestReferencedItemHandler does.
            var workflowId = await db.WorkflowStates.Where(x => x.Id == item.StateId).Select(x => (Guid?)x.WorkflowId).SingleOrDefaultAsync(cancellationToken);
            requestedTarget = workflowId is null ? null : await db.WorkflowStates.Where(x => x.WorkflowId == workflowId && x.Category == WorkflowStateCategory.Resolved)
                .OrderBy(x => x.Position).Select(x => (Guid?)x.Id).FirstOrDefaultAsync(cancellationToken);
        }
        if (requestedTarget is not null && requestedTarget != item.StateId)
        {
            var target = await db.WorkflowStates.SingleOrDefaultAsync(x => x.Id == requestedTarget, cancellationToken);
            var current = await db.WorkflowStates.SingleOrDefaultAsync(x => x.Id == item.StateId, cancellationToken);
            var hasRules = current is not null && await db.WorkflowTransitions.AnyAsync(x => x.WorkflowId == current.WorkflowId, cancellationToken);
            var allowed = target is not null && current is not null && target.WorkflowId == current.WorkflowId &&
                (!hasRules || await db.WorkflowTransitions.AnyAsync(x => x.WorkflowId == current.WorkflowId && x.ToStateId == target.Id &&
                    (x.FromStateId == null || x.FromStateId == item.StateId), cancellationToken));
            var previousStateId = item.StateId;
            if (allowed)
            {
                item.Transition(target!.Id, target.Category, actorId, now);
                // Workers have no request user, so stage the normal state history here.
                db.ItemHistory.Add(new ItemHistory
                {
                    OrganizationId = @event.OrganizationId, ItemId = item.Id, ActorId = actorId, At = now,
                    Field = "state", OldValue = JsonSerializer.Serialize(previousStateId), NewValue = JsonSerializer.Serialize(target.Id), EventId = @event.EventId,
                });
            }
            else
            {
                // A refused transition is recorded, never thrown: the run still finished.
                logger.LogWarning("Run {RunId} on {ItemKey} could not move to state {StateId}: not an allowed transition",
                    @event.RunId, @event.ItemKey, requestedTarget);
                db.ItemHistory.Add(new ItemHistory
                {
                    OrganizationId = @event.OrganizationId, ItemId = item.Id, ActorId = actorId, At = now,
                    Field = "auto-transition-skipped", OldValue = JsonSerializer.Serialize(previousStateId),
                    NewValue = JsonSerializer.Serialize(new { reason = "not allowed", targetStateId = requestedTarget }), EventId = @event.EventId,
                });
            }
        }

        // Transition already cleared the claim in the terminal categories; a run that
        // changed nothing (cancelled, or a skipped transition) still lets the item go.
        if (item.ClaimedBy is not null) item.ReleaseStaleClaim(now);

        if (!string.IsNullOrEmpty(@event.PullRequestUrl))
        {
            var provider = @event.PullRequestUrl.Contains("github.com", StringComparison.OrdinalIgnoreCase) ? "github" : "url";
            var linkExists = await db.ItemLinks.AnyAsync(x => x.ItemId == item.Id && x.Provider == provider &&
                x.Kind == ItemLinkKind.PullRequest && x.ExternalId == @event.PullRequestUrl, cancellationToken);
            if (!linkExists)
            {
                db.ItemLinks.Add(new ItemLink
                {
                    OrganizationId = @event.OrganizationId, ItemId = item.Id, Kind = ItemLinkKind.PullRequest,
                    Provider = provider, ExternalId = @event.PullRequestUrl, Url = @event.PullRequestUrl,
                    Title = $"Pull request for run {@event.RunId}", Meta = JsonSerializer.Serialize(new { @event.RunId }), CreatedAt = now,
                });
                db.ItemHistory.Add(new ItemHistory
                {
                    OrganizationId = @event.OrganizationId, ItemId = item.Id, ActorId = actorId, At = now,
                    Field = "pull-request-linked", NewValue = JsonSerializer.Serialize(new { runId = @event.RunId, url = @event.PullRequestUrl }), EventId = @event.EventId,
                });
            }
        }

        var markdown = @event.Outcome switch
        {
            RunOutcomes.Succeeded => string.IsNullOrWhiteSpace(@event.Summary)
                ? $"Run {@event.RunId} succeeded · [View log](/runs/{@event.RunId})"
                : $"Run {@event.RunId} succeeded · {@event.Summary} · [View log](/runs/{@event.RunId})",
            RunOutcomes.Cancelled => string.IsNullOrWhiteSpace(@event.Summary)
                ? $"Run {@event.RunId} was cancelled · [View log](/runs/{@event.RunId})"
                : $"Run {@event.RunId} was cancelled · {@event.Summary} · [View log](/runs/{@event.RunId})",
            _ => $"Run {@event.RunId} failed · {@event.FailureReason} · [View log](/runs/{@event.RunId})",
        };
        var comment = new Comment
        {
            OrganizationId = @event.OrganizationId, ItemId = item.Id, AuthorId = @event.AgentId,
            BodyMarkdown = markdown, BodyHtml = WorkItemEndpoints.Render(markdown),
            MentionedUserIds = [], CreatedAt = now, EventId = @event.EventId,
        };
        // The comment's own event carries the run's news to watchers through the outbox;
        // the column above is the replay guard, unrelated to it.
        comment.Added(item.ProjectId, mentionedUserIds: [], now);
        db.Comments.Add(comment);

        db.ItemHistory.Add(new ItemHistory
        {
            OrganizationId = @event.OrganizationId, ItemId = item.Id, ActorId = actorId, At = now,
            Field = "run-finished", OldValue = null,
            NewValue = JsonSerializer.Serialize(new { @event.RunId, @event.Outcome, @event.Summary, @event.FailureReason }),
            EventId = @event.EventId,
        });

        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // The comment's (item_id, event_id) index refused the replay. Its matching
            // transition, link and history rows committed with it last time, so this
            // retry is complete.
            db.ChangeTracker.Clear();
            logger.LogDebug("RunFinished {EventId} was already applied; skipping the replay", @event.EventId);
        }
    }
}

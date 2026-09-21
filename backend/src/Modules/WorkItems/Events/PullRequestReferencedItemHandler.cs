using System.Diagnostics.Metrics;
using System.Text.Json;
using Aictiq.Modules.Integrations.Contracts;
using Aictiq.Modules.WorkItems.Domain;
using Aictiq.SharedKernel.Events;
using Aictiq.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Aictiq.Modules.WorkItems.Events;

/// <summary>Materializes GitHub PR state and applies only explicitly allowed workflow automation.</summary>
public sealed class PullRequestReferencedItemHandler(
    WorkItemsDbContext db, ICurrentTenant currentTenant, TimeProvider clock)
    : IDomainEventHandler<PullRequestReferencedItem>
{
    private static readonly Meter Meter = new("Aictiq.Integrations.GitHub");
    private static readonly Counter<long> TransitionsSkipped = Meter.CreateCounter<long>("github.transitions.skipped");

    public async Task HandleAsync(PullRequestReferencedItem @event, CancellationToken cancellationToken)
    {
        using var tenant = currentTenant is AmbientCurrentTenant ambient ? ambient.Use(@event.OrganizationId) : null;
        var item = await db.Items.SingleOrDefaultAsync(x => x.ProjectId == @event.ProjectId && x.Number == @event.ItemNumber, cancellationToken);
        if (item is null) return;

        var now = clock.GetUtcNow();
        var actorId = $"github:{@event.OrganizationId:N}";
        var link = await db.ItemLinks.SingleOrDefaultAsync(x => x.ItemId == item.Id && x.Provider == "github" &&
            x.Kind == ItemLinkKind.PullRequest && x.ExternalId == @event.PullRequestId.ToString(), cancellationToken);
        var meta = JsonSerializer.Serialize(new { @event.Number, @event.AuthorLogin, @event.Reviewers, @event.HeadBranch });
        if (link is null)
        {
            db.ItemLinks.Add(new ItemLink
            {
                OrganizationId = @event.OrganizationId, ItemId = item.Id, Kind = ItemLinkKind.PullRequest,
                Provider = "github", ExternalId = @event.PullRequestId.ToString(), Url = @event.Url,
                Title = @event.Title, State = @event.State, Meta = meta, CreatedAt = now,
            });
            db.ItemHistory.Add(new ItemHistory
            {
                OrganizationId = @event.OrganizationId, ItemId = item.Id, ActorId = actorId, At = now,
                Field = "pull-request-linked", NewValue = JsonSerializer.Serialize(new { @event.Number, @event.Url, @event.State }), EventId = @event.EventId,
            });
        }
        else
        {
            link.Title = @event.Title;
            link.State = @event.State;
            link.Meta = meta;
        }

        var requestedTarget = @event.IsMerged && @event.HasClosingKeyword ? @event.MergedTransitionStateId
            : @event.IsOpened ? @event.OpenedTransitionStateId : null;
        if (@event.IsMerged && @event.HasClosingKeyword && requestedTarget is null)
        {
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
            if (allowed)
            {
                var previousStateId = item.StateId;
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
                TransitionsSkipped.Add(1);
                db.ItemHistory.Add(new ItemHistory
                {
                    OrganizationId = @event.OrganizationId, ItemId = item.Id, ActorId = actorId, At = now,
                    Field = "auto-transition-skipped", NewValue = JsonSerializer.Serialize(new { reason = "not allowed", targetStateId = requestedTarget }), EventId = @event.EventId,
                });
            }
        }
        await db.SaveChangesAsync(cancellationToken);
    }
}

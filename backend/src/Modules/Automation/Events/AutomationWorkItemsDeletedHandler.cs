using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Events;
using Aictiq.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Aictiq.Modules.Automation.Events;

/// <summary>
/// Runs die with their items. A finished run's per-run token is already revoked, so only
/// live runs carry one here; those are revoked after the rows are gone, best effort -
/// an unrevoked token belongs to an agent of a live account and expires on its own.
/// Log chunks follow their runs by the foreign key's cascade; deleting what is already
/// gone is success, so a replayed event finds nothing to do.
/// </summary>
public sealed class AutomationWorkItemsDeletedHandler(
    AutomationDbContext db,
    ICurrentTenant currentTenant,
    IAgentIdentities agents,
    ILogger<AutomationWorkItemsDeletedHandler> logger)
    : IDomainEventHandler<WorkItemsDeleted>
{
    public async Task HandleAsync(WorkItemsDeleted @event, CancellationToken cancellationToken)
    {
        using var tenant = currentTenant is AmbientCurrentTenant ambient ? ambient.Use(@event.OrganizationId) : null;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var liveTokens = await db.Runs
            .Where(run => @event.ItemIds.Contains(run.ItemId) && run.AgentTokenId != null)
            .Select(run => new { run.AgentUserId, TokenId = run.AgentTokenId!.Value })
            .ToListAsync(cancellationToken);
        await db.Runs.Where(run => @event.ItemIds.Contains(run.ItemId))
            .ExecuteDeleteAsync(cancellationToken);
        // A rule's firing rows for these items outlive nothing the item took with it - the
        // item itself is gone, so its firing history no longer names anything real.
        await db.RuleFirings.Where(firing => @event.ItemIds.Contains(firing.ItemId))
            .ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        foreach (var token in liveTokens)
        {
            try
            {
                await agents.RevokeTokenAsync(token.AgentUserId, token.TokenId, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not revoke the per-run token of agent {AgentId} for deleted items",
                    token.AgentUserId);
            }
        }
    }
}

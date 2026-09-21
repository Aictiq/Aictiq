using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Events;
using Aictiq.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Aictiq.Modules.Automation.Events;

/// <summary>
/// Everything the factory kept for a deleted organization. Idempotent by construction: a
/// replay finds nothing to delete. Runners of an organization that no longer exists are
/// removed outright rather than soft-deleted — the history they would have anchored went
/// with the organization. Live runs' per-run tokens are revoked after the deletes, best
/// effort; the runs and their chunks go by <see cref="Run"/> rows alone (chunks cascade).
/// </summary>
public sealed class AutomationOrganizationDeletedHandler(
    AutomationDbContext db,
    ICurrentTenant currentTenant,
    IAgentIdentities agents,
    ILogger<AutomationOrganizationDeletedHandler> logger)
    : IDomainEventHandler<OrganizationDeleted>
{
    public async Task HandleAsync(OrganizationDeleted @event, CancellationToken cancellationToken)
    {
        using var tenant = currentTenant is AmbientCurrentTenant ambient ? ambient.Use(@event.OrganizationId) : null;
        var liveTokens = await db.Runs
            .Where(run => run.OrganizationId == @event.OrganizationId && run.AgentTokenId != null)
            .Select(run => new { run.AgentUserId, TokenId = run.AgentTokenId!.Value })
            .ToListAsync(cancellationToken);
        await db.Runs.Where(run => run.OrganizationId == @event.OrganizationId).ExecuteDeleteAsync(cancellationToken);
        // Rules before playbooks: the FK is RESTRICT. Firings cascade with their rule.
        await db.Rules.Where(r => r.OrganizationId == @event.OrganizationId).ExecuteDeleteAsync(cancellationToken);
        await db.ProjectSettings.Where(s => s.OrganizationId == @event.OrganizationId).ExecuteDeleteAsync(cancellationToken);
        await db.Playbooks.Where(p => p.OrganizationId == @event.OrganizationId).ExecuteDeleteAsync(cancellationToken);
        await db.Runners.Where(r => r.OrganizationId == @event.OrganizationId).ExecuteDeleteAsync(cancellationToken);

        foreach (var token in liveTokens)
        {
            try
            {
                await agents.RevokeTokenAsync(token.AgentUserId, token.TokenId, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not revoke the per-run token of agent {AgentId} for deleted organization {OrganizationId}",
                    token.AgentUserId, @event.OrganizationId);
            }
        }
    }
}

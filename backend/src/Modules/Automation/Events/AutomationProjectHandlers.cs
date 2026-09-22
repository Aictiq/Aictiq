using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Events;
using Aictiq.SharedKernel.Persistence;
using Aictiq.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Aictiq.Modules.Automation.Events;

public sealed class AutomationProjectDeletedHandler(
    AutomationDbContext db,
    ICurrentTenant currentTenant,
    IAgentIdentities agents,
    ILogger<AutomationProjectDeletedHandler> logger)
    : IDomainEventHandler<ProjectDeleted>
{
    public async Task HandleAsync(ProjectDeleted @event, CancellationToken cancellationToken)
    {
        using var tenant = currentTenant is AmbientCurrentTenant ambient ? ambient.Use(@event.OrganizationId) : null;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var ruleIds = await db.Rules.Where(rule => rule.ProjectId == @event.ProjectId)
            .Select(rule => rule.Id).ToListAsync(cancellationToken);
        var playbookIds = await db.Playbooks.Where(playbook => playbook.ProjectId == @event.ProjectId)
            .Select(playbook => playbook.Id).ToListAsync(cancellationToken);
        // Live runs' per-run tokens are collected before their rows go and revoked after
        // the commit, best effort; the chunks follow the runs by the foreign key's cascade.
        var liveTokens = await db.Runs
            .Where(run => run.ProjectId == @event.ProjectId && run.AgentTokenId != null)
            .Select(run => new { run.AgentUserId, TokenId = run.AgentTokenId!.Value })
            .ToListAsync(cancellationToken);
        await db.Runs.Where(run => run.ProjectId == @event.ProjectId)
            .ExecuteDeleteAsync(cancellationToken);
        // Rules before playbooks: automation.rules.playbook_id is RESTRICT, and deleting
        // rules cascades their firings (fk_rule_firings_rules_rule_id) at the database level.
        await db.Rules.Where(rule => rule.ProjectId == @event.ProjectId)
            .ExecuteDeleteAsync(cancellationToken);
        await db.Playbooks.Where(playbook => playbook.ProjectId == @event.ProjectId)
            .ExecuteDeleteAsync(cancellationToken);
        await db.ProjectSettings.Where(settings => settings.ProjectId == @event.ProjectId)
            .ExecuteDeleteAsync(cancellationToken);
        await AuditPurge.EntitiesAsync(db, @event.OrganizationId, AuditEntityTypes.Rule, ruleIds, cancellationToken);
        await AuditPurge.EntitiesAsync(db, @event.OrganizationId, AuditEntityTypes.Playbook, playbookIds, cancellationToken);
        await AuditPurge.EntitiesAsync(db, @event.OrganizationId, AuditEntityTypes.ProjectFactorySettings,
            [@event.ProjectId], cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        foreach (var token in liveTokens)
        {
            try
            {
                await agents.RevokeTokenAsync(token.AgentUserId, token.TokenId, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not revoke the per-run token of agent {AgentId} for deleted project {ProjectId}",
                    token.AgentUserId, @event.ProjectId);
            }
        }
    }
}

/// <summary>Keep the playbook but make the missing backing page explicit.</summary>
public sealed class AutomationWikiPagesDeletedHandler(AutomationDbContext db, ICurrentTenant currentTenant)
    : IDomainEventHandler<WikiPagesDeleted>
{
    public async Task HandleAsync(WikiPagesDeleted @event, CancellationToken cancellationToken)
    {
        using var tenant = currentTenant is AmbientCurrentTenant ambient ? ambient.Use(@event.OrganizationId) : null;
        var pageIds = @event.PageIds.ToArray();
        await db.Playbooks
            .Where(playbook => playbook.ProjectId == @event.ProjectId
                && playbook.WikiPageId != null && pageIds.Contains(playbook.WikiPageId.Value))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(playbook => playbook.WikiPageId, (Guid?)null), cancellationToken);
    }
}

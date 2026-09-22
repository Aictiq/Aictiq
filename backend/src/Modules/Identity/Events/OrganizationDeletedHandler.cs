using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Events;
using Aictiq.SharedKernel.Persistence;
using Aictiq.SharedKernel.Storage;
using Aictiq.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Aictiq.Modules.Identity.Events;

/// <summary>
/// What Identity keeps for a deleted organization: tokens bound to it, the agent accounts
/// that belonged to nowhere else (with their avatars and sessions), and - because Identity
/// owns the table - the organization's whole audit log.
///
/// People keep their accounts. An account is not the organization's; it is how someone
/// signs in to whatever else they belong to, or to make a new organization.
///
/// Raw deletes throughout, so the purge writes no audit rows of its own for the purge of
/// the audit log to miss. A replay deletes nothing.
/// </summary>
public sealed class OrganizationDeletedHandler(IdentityDbContext db, AmbientCurrentTenant tenant, IBlobStorage storage)
    : IDomainEventHandler<OrganizationDeleted>
{
    public async Task HandleAsync(OrganizationDeleted @event, CancellationToken cancellationToken)
    {
        using var scope = tenant.Use(@event.OrganizationId);
        var agents = @event.AgentIds.ToArray();

        foreach (var agentId in agents)
            await storage.DeletePrefixAsync($"users/{agentId}/", cancellationToken);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.PersonalAccessTokens.Where(x => x.OrganizationId == @event.OrganizationId).ExecuteDeleteAsync(cancellationToken);
        if (agents.Length > 0)
        {
            await db.PersonalAccessTokens.Where(x => agents.Contains(x.UserId)).ExecuteDeleteAsync(cancellationToken);
            await db.RefreshTokens.Where(x => agents.Contains(x.UserId)).ExecuteDeleteAsync(cancellationToken);
            // The predicate on IsAgent is the guard: an id in this list can never delete a person.
            await db.Users.Where(x => x.IsAgent && agents.Contains(x.Id)).ExecuteDeleteAsync(cancellationToken);
        }
        await AuditPurge.OrganizationAsync(db, @event.OrganizationId, cancellationToken);
        await ProcessedOutbox.PurgeAsync(db, "OrganizationId", [@event.OrganizationId], cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}

/// <summary>
/// The delivered outbox messages about a deleted project: their payloads carry its name and
/// keys, and retention would otherwise keep them for days. Identity owns the table.
/// </summary>
public sealed class ProjectOutboxPurgeHandler(IdentityDbContext db) : IDomainEventHandler<ProjectDeleted>
{
    public Task HandleAsync(ProjectDeleted @event, CancellationToken cancellationToken) =>
        ProcessedOutbox.PurgeAsync(db, "ProjectId", [@event.ProjectId], cancellationToken);
}

/// <inheritdoc cref="ProjectOutboxPurgeHandler"/>
public sealed class WorkItemsOutboxPurgeHandler(IdentityDbContext db) : IDomainEventHandler<WorkItemsDeleted>
{
    public Task HandleAsync(WorkItemsDeleted @event, CancellationToken cancellationToken) =>
        ProcessedOutbox.PurgeAsync(db, "ItemId", @event.ItemIds, cancellationToken);
}

internal static class ProcessedOutbox
{
    /// <summary>
    /// Only what was already delivered. A pending message may be another module's purge of the
    /// very record being deleted, and a dead-lettered one is an open incident - both stay.
    /// </summary>
    public static async Task PurgeAsync(IdentityDbContext db, string property, IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken)
    {
        if (ids.Count == 0) return;
        var values = ids.Select(id => id.ToString()).ToArray();
        await db.Database.ExecuteSqlAsync($"""
            DELETE FROM shared.outbox_messages
            WHERE processed_at IS NOT NULL AND dead_lettered_at IS NULL
              AND payload ->> {property} = ANY({values})
            """, cancellationToken);
    }
}

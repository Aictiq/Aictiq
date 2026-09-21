using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Aictiq.Modules.Tenancy.Contracts;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Events;
using Aictiq.SharedKernel.Tenancy;

namespace Aictiq.Modules.Tenancy.Events;

/// <summary>
/// Hands an agent to somebody else when the person answerable for it leaves.
///
/// An agent with no owner is the thing to avoid: it keeps acting, its token keeps working,
/// and nobody is accountable for either. So when a membership is removed, every agent that
/// person owned <em>in that organization</em> is reassigned to one of its Owners. The new
/// owner may be surprised to inherit it — which is the point, because the alternative is
/// that nobody is.
/// </summary>
/// <remarks>
/// Runs in Workers off the outbox, so it must tolerate replay. It does: the reassignment
/// names the previous owner in its WHERE clause, so a second delivery after the change has
/// landed matches nothing and changes nothing. It is deliberately not a check-then-write
/// — the check is the write.
///
/// The pairing is why this handler lives in Tenancy rather than Identity: only Tenancy
/// knows which organization a user belonged to and who its Owners are, and only Identity
/// knows who owns an agent. Neither could do it alone, and the contract is the seam.
/// </remarks>
public sealed class AgentOwnershipHandler(
    TenancyDbContext db,
    AmbientCurrentTenant tenant,
    IAgentIdentities agents,
    ILogger<AgentOwnershipHandler> logger) : IDomainEventHandler<OrganizationMembershipChanged>
{
    public async Task HandleAsync(
        OrganizationMembershipChanged domainEvent, CancellationToken cancellationToken)
    {
        // Only a removal orphans anything. A role change leaves the owner in place.
        if (domainEvent.Role is not null)
        {
            return;
        }

        // Background work gets no tenant from a request, and without one the query filter
        // matches nothing — which would make this handler a silent no-op forever.
        using var scope = tenant.Use(domainEvent.OrganizationId);

        var memberIds = await db.Members
            .AsNoTracking()
            .Select(m => m.UserId)
            .ToListAsync(cancellationToken);

        var here = await agents.GetAsync(memberIds, cancellationToken);
        var orphaned = here.Values
            .Where(a => string.Equals(a.OwnerUserId, domainEvent.UserId, StringComparison.Ordinal))
            .Select(a => a.Id)
            .ToList();

        if (orphaned.Count == 0)
        {
            return;
        }

        // An Owner, because Owner is the role that cannot be left vacant — the last-owner
        // trigger guarantees there is one to hand these to.
        var newOwner = await db.Members
            .AsNoTracking()
            .Where(m => m.Role == OrgRole.Owner && m.UserId != domainEvent.UserId)
            .OrderBy(m => m.JoinedAt)
            .Select(m => m.UserId)
            .FirstOrDefaultAsync(cancellationToken);

        if (newOwner is null)
        {
            // The organization itself is going away — a cascade, not a departure. Leaving
            // the agents pointing at the old owner is right: there is nobody to hand them to.
            logger.LogWarning(
                "No owner left in organization {OrganizationId} to inherit {Count} agent(s)",
                domainEvent.OrganizationId, orphaned.Count);
            return;
        }

        var reassigned = await agents.ReassignOwnerAsync(
            orphaned, domainEvent.UserId, newOwner, cancellationToken);

        if (reassigned > 0)
        {
            logger.LogInformation(
                "Reassigned {Count} agent(s) in organization {OrganizationId} from the departed {PreviousOwner} to {NewOwner}",
                reassigned, domainEvent.OrganizationId, domainEvent.UserId, newOwner);
        }
    }
}

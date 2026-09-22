using Aictiq.Modules.Tenancy.Contracts;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Domain;
using Aictiq.SharedKernel.Events;

namespace Aictiq.Modules.Billing.Events;

/// <summary>
/// Re-syncs Stripe's seat quantities whenever the seat count may have moved: someone
/// joined, someone left, or the subscription itself changed (a new plan has a different
/// agent allowance, and a fresh subscription has no quantities recorded yet).
///
/// Idempotent without a ledger because <see cref="SeatSynchronizer"/> never applies a
/// delta: it counts the members as they are when it runs and sends absolute quantities, so
/// a replayed or reordered event converges on the same answer and a no-op sends nothing.
/// A role change is ignored - it moves nobody in or out of a seat.
/// </summary>
public sealed class SeatSyncHandler(SeatSynchronizer seats) :
    IDomainEventHandler<OrganizationMemberAdded>,
    IDomainEventHandler<OrganizationMembershipChanged>,
    IDomainEventHandler<OrganizationBillingChanged>
{
    public Task HandleAsync(OrganizationMemberAdded domainEvent, CancellationToken cancellationToken) =>
        seats.SyncAsync(domainEvent.OrganizationId, cancellationToken);

    public Task HandleAsync(OrganizationMembershipChanged domainEvent, CancellationToken cancellationToken) =>
        domainEvent.Role is null ? seats.SyncAsync(domainEvent.OrganizationId, cancellationToken) : Task.CompletedTask;

    public Task HandleAsync(OrganizationBillingChanged domainEvent, CancellationToken cancellationToken) =>
        seats.SyncAsync(domainEvent.OrganizationId, cancellationToken);

    // One class answering three events has to say which of the three default bridges the
    // dispatcher's untyped call goes through.
    Task IDomainEventHandler.HandleAsync(IDomainEvent domainEvent, CancellationToken cancellationToken) =>
        domainEvent switch
        {
            OrganizationMemberAdded added => HandleAsync(added, cancellationToken),
            OrganizationMembershipChanged changed => HandleAsync(changed, cancellationToken),
            OrganizationBillingChanged billing => HandleAsync(billing, cancellationToken),
            _ => Task.CompletedTask,
        };
}

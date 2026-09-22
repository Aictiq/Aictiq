using Aictiq.SharedKernel.Domain;

namespace Aictiq.SharedKernel.Contracts;

/// <summary>
/// What an organization's paid account means for everything outside Billing.
///
/// Asked, never read: a module that wants to know whether it may write must not learn what
/// a subscription or a grace period is, and Billing must not reach into Tenancy's tables to
/// flip a flag there. The Billing module implements this; <see cref="UnmeteredBillingState"/>
/// is the answer for a host without it, and for every self-hosted instance.
/// </summary>
public interface IOrganizationBillingState
{
    /// <summary>
    /// True once a payment has failed and the grace period has run out without it being
    /// fixed. Reads keep working - the team's history is not held hostage - and every write
    /// that passes <c>RequireProjectWritable</c> is refused until the account is paid.
    /// </summary>
    Task<bool> IsReadOnlyAsync(Guid organizationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The plan the organization's subscription entitles it to. Null means Billing has no
    /// opinion - no subscription was ever taken out, or the instance does not bill - and the
    /// organization's stored plan must be left exactly as it is.
    /// </summary>
    Task<string?> GetEntitledPlanAsync(Guid organizationId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The default, and deliberately permissive like <see cref="UnlimitedPlanLimits"/>: a host
/// without a billing module is not metered, so nothing is ever read-only for want of a
/// payment and no plan is ever imposed.
/// </summary>
public sealed class UnmeteredBillingState : IOrganizationBillingState
{
    public Task<bool> IsReadOnlyAsync(Guid organizationId, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    public Task<string?> GetEntitledPlanAsync(Guid organizationId, CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);
}

/// <summary>
/// Integration event: something about the organization's paid account changed - a
/// subscription started, changed plan, lapsed or was cancelled. Raised by Billing, consumed
/// by Tenancy, which owns <c>organizations.plan</c>.
///
/// It carries no plan on purpose. Outbox delivery is at least once and not necessarily in
/// order, so a consumer that applied "the plan in the event" could be walked backwards by a
/// late redelivery. The consumer asks <see cref="IOrganizationBillingState.GetEntitledPlanAsync"/>
/// instead, which always answers with the newest state: a replay converges on the same
/// value and an old message cannot undo a newer one.
///
/// Lives in SharedKernel, like <c>SendEmailRequested</c>, because the module that raises it
/// and the module that handles it cannot reference each other.
/// </summary>
public sealed record OrganizationBillingChanged(Guid OrganizationId) : DomainEvent, IIntegrationEvent;

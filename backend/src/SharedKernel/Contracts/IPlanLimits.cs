namespace Aictiq.SharedKernel.Contracts;

/// <param name="Reason">
/// Shown to the person who was refused, so it has to say what the limit is and not merely
/// that there is one. Null when allowed.
/// </param>
public sealed record SeatDecision(bool Allowed, string? Reason)
{
    public static readonly SeatDecision Allow = new(true, null);

    public static SeatDecision Deny(string reason) => new(false, reason);
}

/// <summary>A metering decision including the stable limit name for API clients.</summary>
public sealed record PlanLimitDecision(bool Allowed, string? Limit, string? Reason, string? UpgradeUrl)
{
    public static readonly PlanLimitDecision Allow = new(true, null, null, null);
    public static PlanLimitDecision Deny(string limit, string reason, string? upgradeUrl) => new(false, limit, reason, upgradeUrl);
}

/// <summary>
/// What the organization's plan permits. Billing answers it; the contract lives here because the
/// two places that add a seat - sending an invitation and accepting one - are written
/// here, and retrofitting a check into them later means finding them again.
///
/// The interface is asked, never the plan: a module that wants to know whether one more
/// person may join must not learn what a plan is, and a self-hosted instance has no plan
/// at all. <see cref="UnlimitedPlanLimits"/> is the answer for every deployment until a
/// billing module replaces it.
/// </summary>
public interface IPlanLimits
{
    /// <param name="additional">Seats about to be taken. An invitation reserves one before it is accepted.</param>
    Task<SeatDecision> CanAddSeatAsync(
        Guid organizationId, int additional = 1, CancellationToken cancellationToken = default);

    Task<PlanLimitDecision> CanAddHumanSeatAsync(Guid organizationId, int additional = 1, CancellationToken cancellationToken = default);
    Task<PlanLimitDecision> CanAddAgentSeatAsync(Guid organizationId, int additional = 1, CancellationToken cancellationToken = default);
    Task<PlanLimitDecision> CanCreateProjectAsync(Guid organizationId, CancellationToken cancellationToken = default);
    Task<PlanLimitDecision> CanStoreBytesAsync(Guid organizationId, long additionalBytes, CancellationToken cancellationToken = default);
    Task<bool> HasFeatureAsync(Guid organizationId, string name, CancellationToken cancellationToken = default);
}

/// <summary>
/// The self-hosted answer, and the default: yes. Unlike the other null contracts this one
/// is deliberately permissive - a missing billing module means "not metered", not "nobody
/// may join", and the fail-closed reflex would make an unlicensed instance unusable
/// rather than merely unmetered.
/// </summary>
public sealed class UnlimitedPlanLimits : IPlanLimits
{
    public Task<SeatDecision> CanAddSeatAsync(
        Guid organizationId, int additional = 1, CancellationToken cancellationToken = default) =>
        Task.FromResult(SeatDecision.Allow);

    public Task<PlanLimitDecision> CanAddHumanSeatAsync(Guid organizationId, int additional = 1, CancellationToken cancellationToken = default) => Task.FromResult(PlanLimitDecision.Allow);
    public Task<PlanLimitDecision> CanAddAgentSeatAsync(Guid organizationId, int additional = 1, CancellationToken cancellationToken = default) => Task.FromResult(PlanLimitDecision.Allow);
    public Task<PlanLimitDecision> CanCreateProjectAsync(Guid organizationId, CancellationToken cancellationToken = default) => Task.FromResult(PlanLimitDecision.Allow);
    public Task<PlanLimitDecision> CanStoreBytesAsync(Guid organizationId, long additionalBytes, CancellationToken cancellationToken = default) => Task.FromResult(PlanLimitDecision.Allow);
    public Task<bool> HasFeatureAsync(Guid organizationId, string name, CancellationToken cancellationToken = default) => Task.FromResult(true);
}

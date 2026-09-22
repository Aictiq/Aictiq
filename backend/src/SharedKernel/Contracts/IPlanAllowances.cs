namespace Aictiq.SharedKernel.Contracts;

/// <summary>
/// The service allowances a hosted plan entitles an organization to, asked by the
/// modules that enforce them: Automation prunes finished-run raw logs by it, Analytics
/// bounds its report windows with it. Billing owns the answer; the asking module never
/// learns what a plan is, and neither module reads Billing's tables.
///
/// Null means "no entitlement opinion" - the module's own operator configuration applies,
/// exactly as before this contract existed. That is the self-hosted answer, and the
/// default below.
/// </summary>
public interface IPlanAllowances
{
    /// <summary>Days a finished run's raw log is kept. Null keeps the configured retention.</summary>
    Task<int?> GetRunLogRetentionDaysAsync(Guid organizationId, CancellationToken cancellationToken = default);

    /// <summary>Days of history the analytics reports may show. Null keeps the module's own bound.</summary>
    Task<int?> GetAnalyticsHistoryDaysAsync(Guid organizationId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The default, deliberately silent like <see cref="UnlimitedPlanLimits"/> and
/// <see cref="UnmeteredBillingState"/>: a deployment without Billing meters nothing and
/// keeps whatever retention its operator configured.
/// </summary>
public sealed class UnmeteredPlanAllowances : IPlanAllowances
{
    public Task<int?> GetRunLogRetentionDaysAsync(
        Guid organizationId, CancellationToken cancellationToken = default) =>
        Task.FromResult<int?>(null);

    public Task<int?> GetAnalyticsHistoryDaysAsync(
        Guid organizationId, CancellationToken cancellationToken = default) =>
        Task.FromResult<int?>(null);
}

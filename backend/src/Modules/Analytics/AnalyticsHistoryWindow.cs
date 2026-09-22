using Aictiq.Modules.Analytics.Workers;
using Aictiq.SharedKernel.Contracts;
using Microsoft.Extensions.Options;

namespace Aictiq.Modules.Analytics;

/// <summary>
/// How far back a report may look. Two different bounds meet here and the
/// narrower one wins: the operator's <see cref="AnalyticsOptions.RetentionDays"/>, past
/// which the daily snapshots no longer physically exist, and the plan's analytics-history
/// allowance, which is a *query* bound and deletes nothing.
///
/// Keeping them apart is the point. The entitlement narrows what a report shows; it never
/// prunes a snapshot, and it never touches work-item history or audit rows - those are the
/// source records and outlive every billing decision. A self-hosted instance has no
/// entitlement opinion (<see cref="IPlanAllowances"/> answers null) and is bounded only by
/// what its operator configured, exactly as before this existed.
/// </summary>
public sealed class AnalyticsHistoryWindow(
    IPlanAllowances allowances, IOptions<AnalyticsOptions> options, TimeProvider clock)
{
    /// <summary>The days of history this organization may see: the narrower of the two bounds.</summary>
    public async Task<int> DaysAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        var retained = Math.Max(1, options.Value.RetentionDays);
        var entitled = await allowances.GetAnalyticsHistoryDaysAsync(organizationId, cancellationToken);
        return entitled is { } days ? Math.Min(Math.Max(1, days), retained) : retained;
    }

    /// <summary>
    /// The earliest day a report may start from. A requested range that begins before it is
    /// moved forward rather than refused: the question was reasonable, the older snapshots
    /// are simply not this organization's to see, and the response says where the data
    /// actually begins so the limit is visible rather than only described in copy.
    /// </summary>
    public async Task<DateOnly> EarliestAsync(Guid organizationId, CancellationToken cancellationToken = default) =>
        DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime.Date)
            .AddDays(-(await DaysAsync(organizationId, cancellationToken) - 1));
}

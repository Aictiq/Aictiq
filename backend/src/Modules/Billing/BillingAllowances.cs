using Aictiq.Modules.Billing.Domain;
using Aictiq.SharedKernel.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Aictiq.Modules.Billing;

/// <summary>
/// Billing's answer to <see cref="IPlanAllowances"/>: what the organization's
/// plan says about run-log retention and the analytics report window. A self-hosted
/// instance answers "no opinion" without a query, so Automation and Analytics keep their
/// operator-configured defaults untouched; on a hosted deployment the entitled plan -
/// which the evaluation counts as - supplies the numbers.
/// </summary>
public sealed class BillingAllowances(
    BillingDbContext db, IOrganizationPlanUsageSource usage, IOrganizationBillingState state,
    IOptions<BillingOptions> options) : IPlanAllowances
{
    public async Task<int?> GetRunLogRetentionDaysAsync(
        Guid organizationId, CancellationToken cancellationToken = default) =>
        (await PlanAsync(organizationId, cancellationToken))?.Limits.RunLogDays;

    public async Task<int?> GetAnalyticsHistoryDaysAsync(
        Guid organizationId, CancellationToken cancellationToken = default) =>
        (await PlanAsync(organizationId, cancellationToken))?.Limits.AnalyticsDays;

    private async Task<BillingPlan?> PlanAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        if (!options.Value.IsSaas) return null;

        // The same resolution every other billing decision uses; an organization without a
        // row here is one Billing knows nothing about, and Billing does not guess for it.
        var stored = (await usage.GetAsync(organizationId, cancellationToken))?.PlanCode;
        if (stored is null) return null;
        var code = await BillingPlans.EffectiveCodeAsync(state, organizationId, stored, selfHosted: false, cancellationToken);
        return await db.Plans.AsNoTracking().SingleOrDefaultAsync(p => p.Code == code, cancellationToken);
    }
}

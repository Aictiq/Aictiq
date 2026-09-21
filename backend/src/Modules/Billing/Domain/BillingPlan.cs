namespace Aictiq.Modules.Billing.Domain;

public sealed class BillingPlan
{
    public required string Code { get; init; }
    public required PlanLimits Limits { get; init; }

    /// <summary>
    /// The per-human price of the legacy seat plans. Zero on everything
    /// sold today; kept so a subscription that still references a legacy plan —
    /// and the checkout that refreshes it in place — keeps making sense.
    /// </summary>
    public decimal HumanSeatPrice { get; init; }

    /// <summary>
    /// The flat price for the whole organization per month: one subscription,
    /// quantity one, whatever the membership does. Null on the legacy seat plans, which
    /// never had one; a positive value marks the plan as billed this way.
    /// </summary>
    public decimal? OrganizationPrice { get; init; }

    /// <summary>
    /// Agents that came free with each paid human seat on the legacy Starter plan. Null
    /// means agents are never billed separately on this plan; the plan's
    /// <see cref="PlanLimits.SeatsAgent"/> still caps how many there may be.
    /// </summary>
    public int? IncludedAgentsPerHuman { get; init; }
}

/// <summary>
/// Null means unlimited. Limits deliberately retain their wire names.
///
/// <see cref="RunLogDays"/> and <see cref="AnalyticsDays"/> are the service allowances:
/// how long finished-run raw logs are kept, and how far back the analytics
/// reports may look. Null defers to the module's own operator configuration — the
/// self-hosted answer, and every legacy plan's.
///
/// <see cref="Features"/> is a collection rather than a set because System.Text.Json cannot
/// materialise an <c>IReadOnlySet</c> — every plan with features failed to load from its
/// jsonb column while it was declared as a set. The JSON (an array) is unchanged.
/// </summary>
public sealed record PlanLimits(int? SeatsHuman, int? SeatsAgent, int? Projects, long? StorageBytes,
    IReadOnlyCollection<string>? Features = null, int? RunLogDays = null, int? AnalyticsDays = null)
{
    public bool HasFeature(string name) => Features?.Contains(name, StringComparer.OrdinalIgnoreCase) == true;
}

public sealed class UsageSnapshot
{
    public Guid OrganizationId { get; init; }
    public DateOnly Day { get; init; }
    public int Humans { get; init; }
    public int Agents { get; init; }
    public int Projects { get; init; }
    public long StorageBytes { get; init; }
}

/// <summary>The plan codes the code itself has to know about; the rest are data.</summary>
public static class PlanCodes
{
    public const string SelfHosted = "self_hosted";
    public const string Free = "free";

    /// <summary>The one purchasable hosted plan: flat, per organization.</summary>
    public const string Hosted = "hosted";

    /// <summary>
    /// The plan an organization is actually held to. A self-hosted instance holds everyone
    /// to <c>self_hosted</c> whatever is stored; a SaaS instance never honours
    /// <c>self_hosted</c> — it is the column's default, which every organization created
    /// before billing existed still carries, and it is not a plan anyone bought.
    /// </summary>
    public static string Effective(string stored, bool selfHosted) =>
        selfHosted ? SelfHosted
        : string.Equals(stored, SelfHosted, StringComparison.OrdinalIgnoreCase) ? Free
        : stored;
}

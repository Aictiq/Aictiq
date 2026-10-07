using System.ComponentModel.DataAnnotations;

namespace Aictiq.Modules.Billing;

public sealed class BillingOptions
{
    public const string SectionName = "Billing";
    [RegularExpression("^(self_hosted|saas)$")]
    public string Mode { get; init; } = "self_hosted";
    public string UpgradeUrl { get; init; } = "/settings/billing";

    /// <summary>How long an organization keeps writing after a failed payment.</summary>
    [Range(1, 90)]
    public int GracePeriodDays { get; init; } = 14;

    /// <summary>
    /// How long a new hosted organization evaluates before ordinary writes stop.
    /// The evaluation starts once, when the organization is created, and nothing moves it.
    /// </summary>
    [Range(1, 365)]
    public int EvaluationDays { get; init; } = 30;

    /// <summary>
    /// The founding offer's monthly price. Null - the default - means the offer
    /// is not running on this instance and checkout always charges the plan's own price.
    /// The grant lives here, server-side: no request body can opt an organization in.
    /// </summary>
    public decimal? FoundingPrice { get; init; }

    /// <summary>How many monthly billing periods the founding price lasts before the plan's own price returns.</summary>
    [Range(1, 60)]
    public int FoundingPeriods { get; init; } = 12;

    /// <summary>
    /// Operational attachment-storage cap for this deployment, smaller of it and the
    /// plan's allowance wins. Null defers to the plan (10 GiB on Hosted). Self-hosted
    /// instances never reach the check at all.
    /// </summary>
    [Range(1, 1_099_511_627_776)]
    public long? StorageAllowanceBytes { get; init; }

    /// <summary>
    /// How often Workers re-derive every subscription's seat quantities from the member list,
    /// catching anything the per-change sync missed. Daily by default - "nightly".
    /// </summary>
    public TimeSpan SeatSyncInterval { get; init; } = TimeSpan.FromDays(1);

    /// <summary>
    /// How long a processed Stripe event id is kept for de-duplication. Stripe retries for
    /// three days; the default keeps a month of margin beyond that.
    /// </summary>
    [Range(7, 3650)]
    public int StripeEventRetentionDays { get; init; } = 30;

    /// <summary>The hosted free tier. Read only in SaaS mode; off by default.</summary>
    public FreeTierOptions FreeTier { get; init; } = new();

    public bool IsSaas => string.Equals(Mode, "saas", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// SaaS mode <em>and</em> the switch on. Self-hosted never has a free tier, whatever is set:
    /// everything is unlimited there already.
    /// </summary>
    public bool FreeTierEnabled => IsSaas && FreeTier.Enabled;
}

/// <summary>
/// What an unpaid hosted organization keeps once its evaluation ends. The people and storage
/// limits belong to a person, not to an organization: they are counted across every unpaid
/// organization an Owner owns, so ten organizations are not ten times the allowance. The
/// numbers are configuration so they can be tuned without a release.
/// </summary>
public sealed class FreeTierOptions
{
    /// <summary>Off: an evaluation ends in read-only, exactly as before the free tier existed.</summary>
    public bool Enabled { get; init; }

    /// <summary>Distinct humans per Owner across their unpaid organizations, the Owner included.</summary>
    [Range(1, 1000)]
    public int MaxPeople { get; init; } = 3;

    /// <summary>Committed attachment bytes per Owner across their unpaid organizations. 200 MiB.</summary>
    [Range(1, 1_099_511_627_776)]
    public long StorageBytes { get; init; } = 209_715_200;

    /// <summary>Days a finished run's raw log is kept on a free organization.</summary>
    [Range(1, 3650)]
    public int RunLogDays { get; init; } = 30;

    /// <summary>Registered runners per free organization, disabled ones included.</summary>
    [Range(0, 1000)]
    public int MaxRunners { get; init; } = 2;
}

/// <summary>
/// Stripe credentials and the price for each billed plan and seat kind.
///
/// Optional in the strict sense, like SMTP: an instance without <see cref="SecretKey"/> is a
/// supported deployment - every self-hosted one - so nothing here is validated on start,
/// nothing is required, and the billing endpoints answer <c>billing-unavailable</c> instead.
/// Prices are keyed <c>{plan}_{kind}</c> (<c>Stripe:Prices:hosted_organization</c>) so a
/// new plan is configuration rather than code. The kinds are <c>organization</c> (the flat
/// hosted plan's one line), <c>founding</c> (the same plan's discounted founding
/// price), and the legacy seat kinds <c>human</c> and <c>agent</c>, which only the retired
/// plans' surviving subscriptions still use.
/// </summary>
public sealed class StripeOptions
{
    public const string SectionName = "Stripe";

    public string? SecretKey { get; init; }
    public string? WebhookSecret { get; init; }

    /// <summary>How old a signed webhook may be before it is refused as a replay.</summary>
    public long WebhookToleranceSeconds { get; init; } = 300;

    public Dictionary<string, string> Prices { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsConfigured => !string.IsNullOrWhiteSpace(SecretKey) && !string.IsNullOrWhiteSpace(WebhookSecret);

    public string? HumanPrice(string plan) => Price(plan, "human");
    public string? AgentPrice(string plan) => Price(plan, "agent");
    public string? OrganizationPrice(string plan) => Price(plan, "organization");
    public string? FoundingPrice(string plan) => Price(plan, "founding");

    /// <summary>Which plan and seat kind a Stripe price id stands for; null when it is not ours.</summary>
    public (string Plan, string Kind)? Resolve(string? priceId)
    {
        if (string.IsNullOrEmpty(priceId)) return null;
        foreach (var (key, value) in Prices)
        {
            if (!string.Equals(value, priceId, StringComparison.Ordinal)) continue;
            var split = key.LastIndexOf('_');
            if (split <= 0) continue;
            return (key[..split].ToLowerInvariant(), key[(split + 1)..].ToLowerInvariant());
        }
        return null;
    }

    private string? Price(string plan, string kind) =>
        Prices.TryGetValue($"{plan}_{kind}", out var price) && !string.IsNullOrWhiteSpace(price) ? price : null;
}

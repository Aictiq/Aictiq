using Aictiq.Modules.Billing.Domain;
using Aictiq.Modules.Billing.Payments;
using Aictiq.SharedKernel.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Aictiq.Modules.Billing;

/// <param name="StoredPlan">What <c>organizations.plan</c> says, before mode mapping.</param>
public sealed record OrganizationUsage(string StoredPlan, int Humans, int Agents, int Projects, long StorageBytes);

/// <summary>One limit the target plan would be over, for the "remove these first" message.</summary>
public sealed record ExceededLimit(string Limit, long Used, long Allowed);

/// <param name="Humans">Human seats to charge for.</param>
/// <param name="Agents">Agent seats to charge for: only those beyond the included allowance.</param>
public sealed record SeatQuantities(int Humans, int Agents);

/// <summary>
/// The facts Billing counts, read through the owning modules' contracts: members from
/// Tenancy, whether each is an agent from Identity, bytes from WorkItems. Nothing here
/// touches another module's tables.
/// </summary>
public sealed class BillingUsage(
    IOrganizationPlanUsageSource usage, IStorageUsageSource storage, IUserDirectory users)
{
    public async Task<OrganizationUsage?> GetAsync(Guid organizationId, bool includeStorage, CancellationToken cancellationToken)
    {
        var facts = await usage.GetAsync(organizationId, cancellationToken);
        if (facts is null) return null;
        var directory = await users.GetAsync(facts.MemberUserIds, cancellationToken);
        var agents = directory.Values.Count(x => x.IsAgent);
        var bytes = includeStorage ? await storage.GetStoredBytesAsync(organizationId, cancellationToken) : 0;
        return new OrganizationUsage(facts.PlanCode, directory.Count - agents, agents, facts.ProjectCount, bytes);
    }

    /// <summary>
    /// What Stripe should charge for. Every human is a seat. Agents are free up to the
    /// plan's allowance per human (04-PRICING: three per human on Starter) and billed
    /// beyond it; a plan with no allowance never bills agents separately.
    ///
    /// Never fewer than one human: an organization always has a human Owner, and a
    /// zero-quantity seat line is a subscription that charges nothing for a paid plan.
    /// </summary>
    public static SeatQuantities Seats(int humans, int agents, BillingPlan plan)
    {
        var billedHumans = Math.Max(1, humans);
        var billedAgents = plan.IncludedAgentsPerHuman is { } included
            ? Math.Max(0, agents - (billedHumans * included))
            : 0;
        return new SeatQuantities(billedHumans, billedAgents);
    }

    /// <summary>
    /// The priced lines for a plan at these quantities, or null when the plan cannot be
    /// billed because a price it needs is not configured.
    /// </summary>
    public static IReadOnlyList<StripeLine>? Lines(StripeOptions stripe, string plan, SeatQuantities seats)
    {
        if (stripe.HumanPrice(plan) is not { } humanPrice) return null;
        var lines = new List<StripeLine> { new(humanPrice, seats.Humans) };
        if (seats.Agents > 0)
        {
            if (stripe.AgentPrice(plan) is not { } agentPrice) return null;
            lines.Add(new StripeLine(agentPrice, seats.Agents));
        }
        return lines;
    }

    /// <summary>
    /// True when the plan is billed once for the whole organization rather than
    /// per seat. A flat subscription's charge does not move when the membership does, so
    /// every seat-counting path - the per-change sync and the nightly pass - must skip it.
    /// </summary>
    public static bool IsFlat(BillingPlan plan) => plan.OrganizationPrice is > 0;

    /// <summary>
    /// The flat plan's one priced line: the organization price, quantity one. Null when the
    /// price is not configured on this instance.
    /// </summary>
    public static IReadOnlyList<StripeLine>? FlatLines(StripeOptions stripe, string plan) =>
        stripe.OrganizationPrice(plan) is { } price ? [new StripeLine(price, 1)] : null;

    /// <summary>
    /// The founding offer's one priced line: the discounted price for the first periods,
    /// quantity one. Null when the instance is not running the offer.
    /// </summary>
    public static IReadOnlyList<StripeLine>? FoundingLines(StripeOptions stripe, string plan) =>
        stripe.FoundingPrice(plan) is { } price ? [new StripeLine(price, 1)] : null;

    /// <summary>Everything current usage exceeds on the target plan. Empty means the move is allowed.</summary>
    public static IReadOnlyList<ExceededLimit> Exceeded(OrganizationUsage usage, PlanLimits target)
    {
        var exceeded = new List<ExceededLimit>();
        Check("seats_human", usage.Humans, target.SeatsHuman);
        Check("seats_agent", usage.Agents, target.SeatsAgent);
        Check("projects", usage.Projects, target.Projects);
        Check("storage_bytes", usage.StorageBytes, target.StorageBytes);
        return exceeded;

        void Check(string name, long used, long? allowed)
        {
            if (allowed is { } max && used > max) exceeded.Add(new ExceededLimit(name, used, max));
        }
    }
}

/// <summary>Whether this instance takes payments at all, decided once from configuration.</summary>
public sealed class BillingAvailability(IOptions<BillingOptions> billing, IOptions<StripeOptions> stripe)
{
    public bool IsSaas => billing.Value.IsSaas;

    /// <summary>SaaS mode <em>and</em> Stripe configured. Self-hosted never bills, whatever keys are set.</summary>
    public bool IsEnabled => billing.Value.IsSaas && stripe.Value.IsConfigured;

    public string UnavailableReason => billing.Value.IsSaas
        ? "Billing is not configured on this instance (Stripe:SecretKey and Stripe:WebhookSecret are unset)."
        : "This is a self-hosted instance: there is nothing to pay for and no plan limits apply.";
}

internal static class BillingPlans
{
    public static async Task<BillingPlan> FindAsync(BillingDbContext db, string code, CancellationToken cancellationToken) =>
        await db.Plans.AsNoTracking().SingleOrDefaultAsync(x => x.Code == code, cancellationToken)
        ?? await db.Plans.AsNoTracking().SingleAsync(x => x.Code == PlanCodes.Free, cancellationToken);

    /// <summary>
    /// The plan an organization is held to right now, asked in one place so every caller
    /// agrees. The entitlement decides when there is one - a live subscription,
    /// or the evaluation a hosted organization is born with - because the stored column is
    /// only ever a *record* of the last billing change and is nobody's entitlement while a
    /// team is still evaluating. Without one the stored code decides, mapped as before.
    /// </summary>
    public static async Task<string> EffectiveCodeAsync(IOrganizationBillingState state, Guid organizationId,
        string storedCode, bool selfHosted, CancellationToken cancellationToken)
    {
        if (selfHosted) return PlanCodes.SelfHosted;
        return await state.GetEntitledPlanAsync(organizationId, cancellationToken)
            ?? PlanCodes.Effective(storedCode, selfHosted: false);
    }
}

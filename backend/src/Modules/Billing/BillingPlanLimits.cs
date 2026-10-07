using Aictiq.Modules.Billing.Domain;
using Aictiq.SharedKernel.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Aictiq.Modules.Billing;

/// <summary>
/// Billing's answer to <see cref="IPlanLimits"/>. The plan a decision is made
/// against is the <em>entitled</em> one, not the stored column: an organization inside its
/// evaluation is held to Hosted - unlimited people, agents and projects, 10 GiB of
/// attachments - and would otherwise have been held to Free the whole time it was trying
/// the product out.
///
/// On the hosted free tier the people and attachment limits are not the organization's but
/// its Owners': each is checked across every unpaid organization the Owner owns, through
/// <see cref="FreeTierLedger"/>. Those refusals are the only ones that point at an upgrade -
/// paying for Hosted is what lifts them.
/// </summary>
public sealed class BillingPlanLimits(BillingDbContext db, IOrganizationPlanUsageSource usage,
    IStorageUsageSource storage, IUserDirectory users, IOrganizationBillingState state,
    FreeTierLedger freeTier, IOptions<BillingOptions> options) : IPlanLimits
{
    public async Task<SeatDecision> CanAddSeatAsync(Guid organizationId, int additional = 1, CancellationToken cancellationToken = default)
    {
        var decision = await CanAddHumanSeatAsync(organizationId, additional, cancellationToken);
        return decision.Allowed ? SeatDecision.Allow : SeatDecision.Deny(decision.Reason!);
    }
    public Task<PlanLimitDecision> CanAddHumanSeatAsync(Guid organizationId, int additional = 1, CancellationToken ct = default) => CheckCount(organizationId, "seats_human", additional, false, ct);
    public Task<PlanLimitDecision> CanAddAgentSeatAsync(Guid organizationId, int additional = 1, CancellationToken ct = default) => CheckCount(organizationId, "seats_agent", additional, true, ct);
    public async Task<PlanLimitDecision> CanCreateProjectAsync(Guid organizationId, CancellationToken ct = default)
    {
        var source = await usage.GetAsync(organizationId, ct); if (source is null) return PlanLimitDecision.Allow;
        var plan = await PlanAsync(organizationId, source.PlanCode, ct); return Check("projects", source.ProjectCount, 1, plan.Limits.Projects);
    }
    public async Task<PlanLimitDecision> CanStoreBytesAsync(Guid organizationId, long additionalBytes, CancellationToken ct = default)
    {
        var source = await usage.GetAsync(organizationId, ct); if (source is null) return PlanLimitDecision.Allow;
        var plan = await PlanAsync(organizationId, source.PlanCode, ct);
        if (plan.Code == PlanCodes.HostedFree) return await CanStoreFreeBytesAsync(organizationId, additionalBytes, ct);
        var maximum = plan.Limits.StorageBytes;
        if (options.Value.StorageAllowanceBytes is { } cap)
        {
            // An operational cap narrows the plan's allowance; it never widens it.
            maximum = maximum is { } planBytes ? Math.Min(cap, planBytes) : cap;
        }
        var stored = await storage.GetStoredBytesAsync(organizationId, ct);
        if (maximum is null || stored + additionalBytes <= maximum) return PlanLimitDecision.Allow;

        // There is no higher tier to sell: the message says what to do about it,
        // and no upgrade URL is offered, because one does not exist.
        return PlanLimitDecision.Deny("storage_bytes",
            $"This organization has reached its attachment allowance ({Format(maximum.Value)}). "
            + "Delete attachments to free space - existing files stay readable and downloadable.",
            upgradeUrl: null);
    }
    public async Task<bool> HasFeatureAsync(Guid organizationId, string name, CancellationToken ct = default)
    {
        if (SelfHosted) return true; var source = await usage.GetAsync(organizationId, ct); if (source is null) return false;
        return (await PlanAsync(organizationId, source.PlanCode, ct)).Limits.HasFeature(name);
    }
    public async Task<PlanLimitDecision> CanAddPersonAsync(Guid organizationId, IncomingPerson person, CancellationToken ct = default)
    {
        var seat = await CanAddHumanSeatAsync(organizationId, 1, ct);
        if (!seat.Allowed || !await IsOnFreeTierAsync(organizationId, ct)) return seat;
        if (await freeTier.KeyAsync(person, ct) is not { } incoming) return PlanLimitDecision.Allow;
        var accepted = person.AcceptedInvitationEmail is { } email ? await freeTier.EmailKeyAsync(email, ct) : null;

        foreach (var footprint in await freeTier.OwnersOfAsync(organizationId, ct))
        {
            var after = new HashSet<string>(footprint.People, StringComparer.Ordinal);
            // The invitation was counted while it was pending; the person accepting it is
            // that same seat, not a second one.
            if (accepted is not null) after.Remove(accepted);
            after.Add(incoming);
            if (after.Count > freeTier.Limits.MaxPeople) return PeopleRefusal(after.Count);
        }
        return PlanLimitDecision.Allow;
    }

    public async Task<PlanLimitDecision> CanMakeOwnerAsync(Guid organizationId, string userId, CancellationToken ct = default)
    {
        if (!await IsOnFreeTierAsync(organizationId, ct)) return PlanLimitDecision.Allow;
        // The new Owner's own footprint, with this organization's people in it from now on.
        var footprint = await freeTier.ForOwnerAsync(userId, ct, including: organizationId);
        return footprint.People.Count > freeTier.Limits.MaxPeople
            ? PeopleRefusal(footprint.People.Count)
            : PlanLimitDecision.Allow;
    }

    public async Task<PlanLimitDecision> CanRegisterRunnerAsync(Guid organizationId, int registered, CancellationToken ct = default)
    {
        if (!await IsOnFreeTierAsync(organizationId, ct) || registered < freeTier.Limits.MaxRunners) return PlanLimitDecision.Allow;
        return PlanLimitDecision.Deny("runners",
            $"The free plan allows {freeTier.Limits.MaxRunners} registered runners per organization. "
            + "Delete a runner, or upgrade to Hosted for unlimited runners.",
            options.Value.UpgradeUrl);
    }

    /// <summary>
    /// Attachments on the free tier are pooled per Owner: every Owner's committed bytes across
    /// their unpaid organizations, against the free allowance (narrowed, never widened, by
    /// the deployment's operational cap). Over it, only new uploads stop.
    /// </summary>
    private async Task<PlanLimitDecision> CanStoreFreeBytesAsync(Guid organizationId, long additionalBytes, CancellationToken ct)
    {
        var maximum = freeTier.Limits.StorageBytes;
        if (options.Value.StorageAllowanceBytes is { } cap) maximum = Math.Min(cap, maximum);
        foreach (var footprint in await freeTier.OwnersOfAsync(organizationId, ct))
        {
            if (await freeTier.StoredBytesAsync(footprint, ct) + additionalBytes <= maximum) continue;
            return PlanLimitDecision.Deny("storage_bytes",
                $"The free plan allows {Format(maximum)} of attachments across all the free organizations an owner has. "
                + "Delete attachments to free space, or upgrade this organization to Hosted - existing files stay readable and downloadable.",
                options.Value.UpgradeUrl);
        }
        return PlanLimitDecision.Allow;
    }

    private PlanLimitDecision PeopleRefusal(int people) =>
        PlanLimitDecision.Deny("free_people",
            $"The free plan allows {freeTier.Limits.MaxPeople} people, owner included, across all the free organizations "
            + $"an owner has; this would make {people}. Remove someone or revoke an invitation, or upgrade this organization to Hosted.",
            options.Value.UpgradeUrl);

    private async Task<bool> IsOnFreeTierAsync(Guid organizationId, CancellationToken ct) =>
        options.Value.FreeTierEnabled
        && await state.GetEntitledPlanAsync(organizationId, ct) == PlanCodes.HostedFree;

    private async Task<PlanLimitDecision> CheckCount(Guid id, string limit, int add, bool agents, CancellationToken ct)
    {
        if (SelfHosted) return PlanLimitDecision.Allow;
        var source = await usage.GetAsync(id, ct); if (source is null) return PlanLimitDecision.Allow;
        var directory = await users.GetAsync(source.MemberUserIds, ct);
        var count = directory.Values.Count(u => u.IsAgent == agents);
        var plan = await PlanAsync(id, source.PlanCode, ct);
        return Check(limit, count, add, agents ? plan.Limits.SeatsAgent : plan.Limits.SeatsHuman);
    }
    private bool SelfHosted => string.Equals(options.Value.Mode, "self_hosted", StringComparison.OrdinalIgnoreCase);
    // The entitlement decides, falling back to the stored code: on a SaaS instance an
    // organization still carrying the column's self_hosted default is on Free, not on the
    // unlimited self-hosted plan.
    private async Task<BillingPlan> PlanAsync(Guid organizationId, string code, CancellationToken ct) =>
        await BillingPlans.FindAsync(db,
            await BillingPlans.EffectiveCodeAsync(state, organizationId, code, SelfHosted, ct), ct);
    private PlanLimitDecision Check(string name, long current, long additional, long? maximum) =>
        maximum is null || current + additional <= maximum ? PlanLimitDecision.Allow : PlanLimitDecision.Deny(name, $"Your plan allows {maximum:N0} {name.Replace('_', ' ')}.", options.Value.UpgradeUrl);

    private static string Format(long bytes) =>
        bytes >= 1_073_741_824 ? $"{bytes / 1_073_741_824.0:0.#} GiB"
        : bytes >= 1_048_576 ? $"{bytes / 1_048_576.0:0.#} MiB"
        : $"{bytes / 1_024.0:0.#} KiB";
}

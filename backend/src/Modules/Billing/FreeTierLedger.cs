using Aictiq.Modules.Billing.Domain;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Aictiq.Modules.Billing;

/// <summary>What one Owner's unpaid organizations add up to.</summary>
/// <param name="People">
/// Distinct humans, as keys: <c>u:{userId}</c> for an account, <c>e:{address}</c> for an
/// invitation to an address nobody has registered yet.
/// </param>
public sealed record OwnerFootprint(string OwnerId, IReadOnlyList<Guid> Organizations, IReadOnlySet<string> People);

/// <summary>
/// The hosted free tier's arithmetic. Its limits belong to a person: for every Owner, the
/// distinct humans and the committed attachment bytes across every <em>unpaid</em>
/// organization they own. Unpaid means no subscription Stripe is charging - an evaluating
/// organization is unpaid, a paid Hosted one is not and is invisible here.
///
/// Members come from Tenancy, whether each is an agent from Identity, bytes from WorkItems,
/// each organization read inside its own tenant scope; nothing here reads another module's
/// tables.
/// </summary>
public sealed class FreeTierLedger(
    BillingDbContext db, AmbientCurrentTenant tenant, IOrganizationPeopleSource people,
    IStorageUsageSource storage, IUserDirectory users, IOptions<BillingOptions> options)
{
    public FreeTierOptions Limits => options.Value.FreeTier;

    /// <summary>A subscription Stripe is charging. Past due still counts: grace, not this, takes writes away.</summary>
    public async Task<bool> IsPaidAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        using var scope = tenant.Use(organizationId);
        var status = await db.Subscriptions.AsNoTracking()
            .Select(x => (SubscriptionStatus?)x.Status)
            .SingleOrDefaultAsync(cancellationToken);
        return status?.IsBilling() == true;
    }

    public async Task<IReadOnlyList<OwnerFootprint>> OwnersOfAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        var footprints = new List<OwnerFootprint>();
        foreach (var owner in await people.GetOwnerIdsAsync(organizationId, cancellationToken))
        {
            footprints.Add(await ForOwnerAsync(owner, cancellationToken));
        }
        return footprints;
    }

    public async Task<OwnerFootprint> ForOwnerAsync(string ownerId, CancellationToken cancellationToken, Guid? including = null)
    {
        var unpaid = new List<Guid>();
        foreach (var organizationId in await people.GetOwnedOrganizationIdsAsync(ownerId, cancellationToken))
        {
            if (!await IsPaidAsync(organizationId, cancellationToken)) unpaid.Add(organizationId);
        }
        // An organization the person is about to own counts as theirs already.
        if (including is { } extra && !unpaid.Contains(extra)) unpaid.Add(extra);

        var keys = new HashSet<string>(StringComparer.Ordinal) { UserKey(ownerId) };
        var members = new HashSet<string>(StringComparer.Ordinal);
        var invited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var organizationId in unpaid)
        {
            var found = await people.GetPeopleAsync(organizationId, cancellationToken);
            members.UnionWith(found.MemberUserIds);
            invited.UnionWith(found.PendingInvitationEmails);
        }

        var agents = await users.FilterAgentsAsync([.. members], cancellationToken);
        foreach (var member in members)
        {
            if (!agents.Contains(member)) keys.Add(UserKey(member));
        }
        foreach (var email in invited)
        {
            keys.Add(await EmailKeyAsync(email, cancellationToken));
        }
        return new OwnerFootprint(ownerId, unpaid, keys);
    }

    public async Task<long> StoredBytesAsync(OwnerFootprint footprint, CancellationToken cancellationToken)
    {
        long total = 0;
        foreach (var organizationId in footprint.Organizations)
        {
            using var scope = tenant.Use(organizationId);
            total += await storage.GetStoredBytesAsync(organizationId, cancellationToken);
        }
        return total;
    }

    /// <summary>
    /// True when any Owner of the organization has more people across their unpaid
    /// organizations than the free tier allows. Nobody is removed for it; the
    /// organization is read-only until someone is, or until it pays.
    /// </summary>
    public async Task<bool> IsOverPeopleLimitAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        foreach (var footprint in await OwnersOfAsync(organizationId, cancellationToken))
        {
            if (footprint.People.Count > Limits.MaxPeople) return true;
        }
        return false;
    }

    /// <summary>The key a person counts under; an invitation to someone with an account counts as that account.</summary>
    public async Task<string?> KeyAsync(IncomingPerson person, CancellationToken cancellationToken) =>
        person.UserId is { } userId ? UserKey(userId)
        : person.Email is { } email ? await EmailKeyAsync(email, cancellationToken)
        : null;

    public async Task<string> EmailKeyAsync(string email, CancellationToken cancellationToken) =>
        await users.FindByEmailAsync(email, cancellationToken) is { } contact
            ? UserKey(contact.Id)
            : $"e:{email.Trim().ToLowerInvariant()}";

    private static string UserKey(string userId) => $"u:{userId}";
}

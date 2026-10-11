using System.Security.Cryptography;
using Aictiq.Modules.Automation.Domain;
using Aictiq.SharedKernel.Contracts;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Aictiq.Modules.Automation;

/// <summary>
/// Whether fleet mode is in force for an organization: the Admin's switch, and the plan's say.
/// Everything that behaves differently in fleet mode asks here, so an organization that never
/// turned it on - or whose plan no longer allows it - takes exactly the path it always did.
/// </summary>
public sealed class FleetMode(AutomationDbContext db, IPlanLimits plans)
{
    public async Task<FleetState> GetAsync(Guid organizationId, CancellationToken ct)
    {
        var enabled = await db.FleetSettings.AsNoTracking().AnyAsync(row => row.Enabled, ct);
        if (!enabled)
        {
            // No plan check for the common case: an organization that never opted in.
            return FleetState.Off;
        }

        var availability = await plans.GetFleetModeAvailabilityAsync(organizationId, ct);
        return new FleetState(true, availability == FleetModeAvailability.Available);
    }

    /// <summary>Every tag a runner of the organization has offered, deleted runners included, for the "no runner has this tag" warning.</summary>
    public async Task<IReadOnlyList<string>> KnownTagsAsync(CancellationToken ct)
    {
        var runners = await db.Runners.AsNoTracking().ToListAsync(ct);
        return [.. runners.SelectMany(FleetTags.Effective).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }
}

/// <summary>The scale webhook's HMAC secret, protected at rest like an outbound webhook's.</summary>
public sealed class FleetSecrets(IDataProtectionProvider protection)
{
    private readonly IDataProtector _protector = protection.CreateProtector("Aictiq.Automation.FleetWebhookSecret.v1");

    public string Protect(string secret) => _protector.Protect(secret);

    public string Unprotect(string protectedSecret) => _protector.Unprotect(protectedSecret);

    public static string CreateSecret() => "whsec_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
}

using System.Diagnostics.Metrics;

namespace Aictiq.Modules.Identity.Auth;

/// <summary>
/// Security-relevant authentication outcomes. These counters deliberately contain no
/// account, address, token, or IP label: such labels are high-cardinality and would turn
/// the telemetry backend into another store of sensitive identifiers.
/// </summary>
public static class IdentitySecurityMetrics
{
    public const string MeterName = "Aictiq.Identity";
    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> LoginLockouts = Meter.CreateCounter<long>("identity.login.lockouts");

    /// <summary>Records a login refused because the account is currently locked.</summary>
    public static void RecordLoginLockout() => LoginLockouts.Add(1);
}

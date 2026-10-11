using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Aictiq.SharedKernel.Domain;

namespace Aictiq.Modules.Automation.Domain;

/// <summary>
/// An organization's fleet mode: runner tags, required tags on runs, least-loaded dispatch and
/// the scale-from-zero webhook. One row per organization, created the first time an Admin saves
/// it; no row reads as "off", which is how every organization behaves until someone opts in.
///
/// Only paid hosted plans may turn it on (<see cref="Aictiq.SharedKernel.Contracts.IPlanLimits.GetFleetModeAvailabilityAsync"/>).
/// <see cref="Enabled"/> is what the Admin asked for; whether it is in force also depends on the
/// plan, so an organization that drops off a paid plan keeps its settings but dispatches as if
/// fleet mode were off until it pays again. Self-hosted instances are not offered fleet mode yet;
/// support may come later.
/// </summary>
public sealed class FleetSettings : TenantEntity, IAudited
{
    public const int MaxWebhookUrlLength = 2048;

    public const int MaxWebhookErrorLength = 500;

    public bool Enabled { get; set; }

    /// <summary>The customer's endpoint that starts an ephemeral runner, or null for no scale-from-zero.</summary>
    public string? ScaleWebhookUrl { get; set; }

    /// <summary>The HMAC secret for <c>X-Aictiq-Signature</c>, data-protected. Shown once, when minted.</summary>
    public string? ScaleWebhookSecretProtected { get; set; }

    /// <summary>How long the oldest due run must have waited before the webhook is called.</summary>
    public int ScaleAfterSeconds { get; set; } = 120;

    /// <summary>The least time between two webhook calls, whatever they answered.</summary>
    public int ScaleCooldownSeconds { get; set; } = 600;

    public DateTimeOffset? LastScaleCallAt { get; set; }

    /// <summary>Why the last call failed (a status code, a timeout), or null when it succeeded.</summary>
    public string? LastScaleCallError { get; set; }

    /// <summary>Hex SHA-256 of the enrollment token ephemeral runners register with; null when none was minted.</summary>
    public string? EnrollTokenHash { get; set; }

    public string? EnrollTokenPrefix { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public uint Version { get; private set; }

    /// <summary>A call is due when the cooldown since the last one has passed.</summary>
    public bool ScaleCallDue(DateTimeOffset now) =>
        LastScaleCallAt is not { } last || now - last >= TimeSpan.FromSeconds(ScaleCooldownSeconds);
}

/// <summary>
/// Whether fleet mode is in force for an organization right now, and why not when it is not.
/// </summary>
/// <param name="Enabled">An Admin turned it on.</param>
/// <param name="Available">The plan allows it.</param>
public readonly record struct FleetState(bool Enabled, bool Available)
{
    public static readonly FleetState Off = new(false, false);

    /// <summary>Dispatch, claim and the run views follow fleet rules only while this is true.</summary>
    public bool Active => Enabled && Available;

    /// <summary>Turned on, but the plan no longer allows it: dispatch falls back and the UI says so.</summary>
    public bool Downgraded => Enabled && !Available;
}

/// <summary>
/// Runner tags and the tags a run requires. A tag is lower case, short and free-form
/// (<c>gpu-enabled</c>, <c>linux-docker</c>); the OS and architecture a runner reports are
/// tags too, as <c>os:linux</c> and <c>arch:arm64</c>.
/// </summary>
public static partial class FleetTags
{
    public const int MaxTags = 20;

    public const int MaxTagLength = 50;

    public const string Rule = "Tags are lower case letters, digits, '-', '_', '.' or ':', at most 50 characters, at most 20 of them.";

    /// <summary>Trimmed, lower-cased, distinct and sorted; null when any tag is not a tag.</summary>
    public static string[]? Normalize(IEnumerable<string?>? tags)
    {
        if (tags is null)
        {
            return [];
        }

        var result = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var tag in tags)
        {
            var value = tag?.Trim().ToLowerInvariant() ?? "";
            if (!TagPattern().IsMatch(value))
            {
                return null;
            }
            result.Add(value);
        }

        return result.Count > MaxTags ? null : [.. result];
    }

    /// <summary>The tags a runner offers: the ones set on Factory → Runners, the CLI's <c>--tag</c>s, and its OS and arch.</summary>
    public static string[] Effective(Runner runner) =>
    [
        .. runner.Tags
            .Concat(runner.Capabilities?.Tags ?? [])
            .Concat(BuiltIn(runner.Capabilities))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal),
    ];

    public static IEnumerable<string> BuiltIn(RunnerCapabilities? capabilities)
    {
        if (capabilities?.Os is { Length: > 0 } os && Normalize([$"os:{os}"]) is [var osTag])
        {
            yield return osTag;
        }
        if (capabilities?.Arch is { Length: > 0 } arch && Normalize([$"arch:{arch}"]) is [var archTag])
        {
            yield return archTag;
        }
    }

    public static bool Satisfies(IEnumerable<string> offered, IReadOnlyCollection<string> required) =>
        required.Count == 0 || required.All(offered.Contains);

    public static string[] Union(params IEnumerable<string>?[] sets) =>
        [.. sets.SelectMany(set => set ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    [GeneratedRegex("^[a-z0-9][a-z0-9._:-]{0,49}$")]
    private static partial Regex TagPattern();
}

/// <summary>
/// The <c>jfe_</c> enrollment token an ephemeral runner registers itself with. It carries its
/// organization's id in the clear, because the token is what decides the tenant of an anonymous
/// request; the secret part is checked against its hash under that tenant.
/// </summary>
public static class FleetEnrollToken
{
    public const string TokenPrefix = "jfe_";

    private const int SecretLength = 40;

    private const string Base62 = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    public static string Generate(Guid organizationId) =>
        $"{TokenPrefix}{organizationId:N}_{RandomNumberGenerator.GetString(Base62, SecretLength)}";

    public static bool TryParse(string? value, out Guid organizationId)
    {
        organizationId = Guid.Empty;
        return value is { Length: 4 + 32 + 1 + SecretLength }
            && value.StartsWith(TokenPrefix, StringComparison.Ordinal)
            && value[36] == '_'
            && Guid.TryParseExact(value.AsSpan(4, 32), "N", out organizationId);
    }

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static string PrefixOf(string token) => token.Substring(37, 8);

    public static string Display(string prefix) => $"{TokenPrefix}…{prefix}…";
}

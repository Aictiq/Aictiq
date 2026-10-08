using System.Security.Cryptography;
using System.Text;
using Aictiq.SharedKernel.Domain;

namespace Aictiq.Modules.Automation.Domain;

/// <summary>
/// A process on a machine someone controls - a VPS, a laptop, a CI box - registered with one
/// organization to execute factory runs.
///
/// A runner is a <em>machine</em>, not an actor. It authenticates with its own secret
/// (<c>jrn_…</c>), and the principal that secret produces has no user id, so nothing that
/// asks "which person is this" can be answered by a runner. It is never removed: a runner's
/// id will be written on every run it executed, so "deleting" one disables it for good and
/// hides it from the roster.
/// </summary>
public sealed class Runner : TenantEntity, IAudited
{
    public const int MaxNameLength = 100;

    public required string Name { get; set; }

    /// <summary>Hex SHA-256 of the secret. The secret itself exists once, in the response that minted it.</summary>
    public required string TokenHash { get; set; }

    /// <summary>The first characters of the secret, kept in the clear only so a person can tell two secrets apart.</summary>
    public required string TokenPrefix { get; set; }

    public required string RegisteredBy { get; init; }

    /// <summary>What the runner said it can do in its last <c>hello</c> or heartbeat; null until it has spoken.</summary>
    public RunnerCapabilities? Capabilities { get; set; }

    public DateTimeOffset? LastSeenAt { get; set; }

    public DateTimeOffset? DisabledAt { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }

    public uint Version { get; set; }

    public bool IsUsable => DisabledAt is null && DeletedAt is null;

    public string Display => RunnerCredential.Display(TokenPrefix);

    public static Runner Register(Guid organizationId, string name, string registeredBy, DateTimeOffset now, out string secret)
    {
        secret = RunnerCredential.Generate();
        return new Runner
        {
            OrganizationId = organizationId,
            Name = name,
            RegisteredBy = registeredBy,
            TokenHash = RunnerCredential.Hash(secret),
            TokenPrefix = RunnerCredential.PrefixOf(secret),
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>A new secret; the previous one stops working the moment this is saved.</summary>
    public string Rotate(DateTimeOffset now)
    {
        var secret = RunnerCredential.Generate();
        TokenHash = RunnerCredential.Hash(secret);
        TokenPrefix = RunnerCredential.PrefixOf(secret);
        UpdatedAt = now;
        return secret;
    }
}

/// <summary>The <c>jrn_</c> secret: shape, hashing and display. Mirrors a personal access token on purpose.</summary>
public static class RunnerCredential
{
    public const string TokenPrefix = "jrn_";

    public const int SecretLength = 40;

    public const int PrefixLength = 8;

    private const string Base62 = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    private const int TotalLength = 4 + SecretLength;

    public static string Generate() => TokenPrefix + RandomNumberGenerator.GetString(Base62, SecretLength);

    public static bool LooksLikeToken(string? value) =>
        value is { Length: TotalLength } && value.StartsWith(TokenPrefix, StringComparison.Ordinal);

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static string PrefixOf(string token) => token.Substring(TokenPrefix.Length, PrefixLength);

    public static string Display(string prefix) => $"{TokenPrefix}{prefix}…";
}

/// <summary>
/// What a runner reports about itself. Versioned (<see cref="V"/>): the CLI writes it and
/// the dispatcher matches runs against <see cref="Harnesses"/>, so the shape is a contract between
/// the CLI and the server, not an implementation detail of either.
/// </summary>
/// <param name="MachineId">
/// A random id the CLI keeps in its <c>runner.json</c>, the same for every organization that
/// machine is registered with. It only groups one person's runners into machines for display
/// ("use a runner I already have"); nothing is authorized by it, because a runner could
/// report any value.
/// </param>
/// <param name="Service">
/// Whether a definition from <c>aictiq runner install-service</c> started the runner. Only the
/// setup guide reads it, to tell a runner that survives a reboot from one left in a terminal.
/// </param>
/// <param name="Workspaces">
/// This organization's project keys the runner maps to a clone (<c>aictiq runner map</c>). Each
/// profile reports its own, so one organization never learns another's.
/// </param>
/// <param name="RepoRoots">
/// This organization's repository roots (<c>aictiq runner root</c>), so the setup guide can say
/// whether a project's path hint lies under one. Display only: the runner decides for itself.
/// </param>
/// <param name="MissingHarnesses">
/// Every harness the CLI knows that the runner does not offer, and why, so the runner page can
/// say what to fix on the machine. Display only: dispatch reads <see cref="Harnesses"/>.
/// </param>
/// <param name="Path">
/// The PATH the runner looked for harnesses on. A service's differs from a login shell's, and
/// that difference is the usual reason a harness that works in a terminal is missing.
/// </param>
/// <param name="UpdateFailure">The runner's last self-update that did not take, while it still stands.</param>
/// <param name="UsageLimits">
/// The last 5-hour and weekly allowance each harness account on the machine reported in a run's
/// output, per harness. Only harnesses that report one appear (Claude Code and Codex today).
/// Display only, and possibly old: <see cref="RunnerUsageLimits.ObservedAt"/> says when it was read.
/// </param>
public sealed record RunnerCapabilities(
    int V,
    IReadOnlyList<RunnerHarness> Harnesses,
    string? Os,
    string? Arch,
    string? CliVersion,
    int MaxParallel,
    string? MachineId = null,
    bool? Service = null,
    IReadOnlyList<string>? Workspaces = null,
    IReadOnlyList<string>? RepoRoots = null,
    IReadOnlyList<RunnerMissingHarness>? MissingHarnesses = null,
    string? Path = null,
    RunnerUpdateFailure? UpdateFailure = null,
    IReadOnlyList<RunnerUsageLimits>? UsageLimits = null);

/// <param name="Name">The harness as a playbook names it: <c>claude</c>, <c>codex</c>, <c>opencode</c>, <c>cursor</c>, <c>copilot</c>.</param>
public sealed record RunnerHarness(string Name, string? Version);

/// <param name="Reason"><c>not-on-path</c>: no executable found; <c>version-failed</c>: found, but <c>--version</c> failed.</param>
/// <param name="Command">The executable looked for, or the one found.</param>
public sealed record RunnerMissingHarness(string Name, string Reason, string Command);

/// <param name="Version">The CLI version the runner tried to install.</param>
/// <param name="Error">Why it failed, as the package manager said it.</param>
public sealed record RunnerUpdateFailure(string Version, string Error, DateTimeOffset At);

/// <param name="Harness">The harness whose account this is, named as in <see cref="RunnerHarness.Name"/>.</param>
/// <param name="ObservedAt">When the harness reported it, which is when a run last used it.</param>
/// <param name="FiveHour">The rolling 5-hour window, or null when the harness did not report one.</param>
/// <param name="Weekly">The weekly window, or null when the harness did not report one.</param>
public sealed record RunnerUsageLimits(
    string Harness, DateTimeOffset ObservedAt, RunnerUsageWindow? FiveHour, RunnerUsageWindow? Weekly);

/// <param name="UsedPercent">How much of the window's allowance was used, 0 to 100 (a little more past the limit).</param>
/// <param name="ResetsAt">When the window starts over, when the harness said.</param>
public sealed record RunnerUsageWindow(double UsedPercent, DateTimeOffset? ResetsAt);

using System.Security.Cryptography;
using System.Text;
using Aictiq.SharedKernel.Domain;

namespace Aictiq.Modules.Automation.Domain;

/// <summary>
/// A process on a machine someone controls — a VPS, a laptop, a CI box — registered with one
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
public sealed record RunnerCapabilities(
    int V,
    IReadOnlyList<RunnerHarness> Harnesses,
    string? Os,
    string? Arch,
    string? CliVersion,
    int MaxParallel);

/// <param name="Name">The harness as a playbook names it: <c>claude</c>, <c>codex</c>, <c>opencode</c>.</param>
public sealed record RunnerHarness(string Name, string? Version);

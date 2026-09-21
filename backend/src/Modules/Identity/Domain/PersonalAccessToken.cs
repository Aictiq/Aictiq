using System.Security.Cryptography;
using System.Text;
using Aictiq.SharedKernel.Domain;

namespace Aictiq.Modules.Identity.Domain;

/// <summary>
/// The credential the CLI, the MCP server, scripts and agents authenticate with.
///
/// Only the SHA-256 of the token is stored, exactly as for refresh tokens and invitations:
/// a leaked database hands out nothing usable, and there is nothing to compare a presented
/// token against except a hash. <see cref="Prefix"/> exists so a person can tell their
/// tokens apart in a list without the product having to remember the secret.
///
/// A token can be bound to one organization. That is not a permission — it never grants
/// what its owner lacks — it is a <em>narrowing</em>, and it is what lets an agent's
/// credential be useless anywhere but the organization it was made for.
/// </summary>
public sealed class PersonalAccessToken : IAudited
{
    /// <summary>Recognisable at a glance in a log, a config file or a leaked gist.</summary>
    public const string TokenPrefix = "aiq_";

    /// <summary>40 base62 characters: ~238 bits, and still one double-click to copy.</summary>
    public const int SecretLength = 40;

    /// <summary>How much of the secret is kept in the clear, only to identify the row to a human.</summary>
    public const int PrefixLength = 8;

    public const int MaxNameLength = 60;

    private const string Base62 = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required string UserId { get; init; }

    /// <summary>
    /// Null for a token that works wherever its owner does. Set, and the token is refused
    /// against any other organization — see <c>TenantResolutionMiddleware</c>.
    /// </summary>
    public Guid? OrganizationId { get; init; }

    public required string Name { get; set; }

    public required string TokenHash { get; init; }

    /// <summary>The first characters of the secret, for display: <c>aiq_a1b2c3d4…</c>.</summary>
    public required string Prefix { get; init; }

    /// <summary>
    /// Empty means unscoped — the token may do whatever its owner may. Scopes only ever
    /// narrow; they never grant. See <c>ScopeRequirements</c>.
    /// </summary>
    public string[] Scopes { get; init; } = [];

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Null for a token that never expires. Discouraged, not forbidden.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>
    /// Written at most once every <see cref="LastUsedThrottle"/>. It exists to answer "is
    /// anything still using this", which does not need per-request resolution — and a
    /// write on every authenticated request would be a needless one on the hottest path
    /// the API has.
    /// </summary>
    public DateTimeOffset? LastUsedAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    public static readonly TimeSpan LastUsedThrottle = TimeSpan.FromMinutes(5);

    public bool IsUsableAt(DateTimeOffset now) =>
        RevokedAt is null && (ExpiresAt is null || ExpiresAt > now);

    /// <param name="token">The plaintext, returned once. It is never stored and never recoverable.</param>
    public static PersonalAccessToken Create(
        string userId, Guid? organizationId, string name, IReadOnlyCollection<string> scopes,
        DateTimeOffset? expiresAt, DateTimeOffset now, out string token)
    {
        var secret = RandomNumberGenerator.GetString(Base62, SecretLength);
        token = TokenPrefix + secret;

        return new PersonalAccessToken
        {
            UserId = userId,
            OrganizationId = organizationId,
            Name = name,
            TokenHash = Hash(token),
            Prefix = secret[..PrefixLength],
            Scopes = [.. scopes],
            CreatedAt = now,
            ExpiresAt = expiresAt,
        };
    }

    /// <summary>
    /// Cheap enough to run on every request before anything touches the database — which
    /// is exactly what it is for: the scheme selector uses it to route a bearer value to
    /// this handler or to the JWT one.
    /// </summary>
    private const int TotalLength = 4 + SecretLength;

    public static bool LooksLikeToken(string? value) =>
        value is { Length: TotalLength }
        && value.StartsWith(TokenPrefix, StringComparison.Ordinal);

    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>What a person sees in a list. Never the secret — it does not exist here.</summary>
    public string Display => $"{TokenPrefix}{Prefix}…";
}

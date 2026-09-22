using System.Security.Cryptography;
using System.Text;

namespace Aictiq.Modules.Identity.Domain;

/// <summary>
/// Only the SHA-256 hash of the token is stored. Tokens rotate on every use; FamilyId
/// links a rotation chain so reuse of a consumed token revokes the entire family.
///
/// A family is also what a person means by "a session": one browser, one CLI, one phone,
/// signed in once and rotating quietly ever since. <c>/me/sessions</c> lists families and
/// revokes them, which is why the chain is worth keeping intact rather than collapsing
/// each rotation onto one row.
/// </summary>
public sealed class RefreshToken
{
    /// <summary>
    /// How much of the <c>User-Agent</c> header is kept. Enough to say "Firefox on Linux"
    /// in the sessions list; short enough that the column is a label rather than a log.
    /// </summary>
    public const int MaxUserAgentLength = 256;

    public Guid Id { get; init; } = Guid.CreateVersion7();
    public required string UserId { get; init; }
    public required string TokenHash { get; init; }
    public Guid FamilyId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public DateTimeOffset? UsedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>
    /// The client that signed in, verbatim and truncated. Not parsed into a device name
    /// here: the string is what a person recognises ("this is the laptop"), and a parser
    /// that guessed wrong would make them revoke the wrong session.
    /// </summary>
    public string? UserAgent { get; set; }

    // Deliberately no IsUsable(now) helper: reading usability and then writing used_at
    // is a race that forks the family and defeats reuse detection. TokenService consumes
    // the token with a single conditional UPDATE instead.

    /// <summary>
    /// Hex SHA-256, the convention this schema uses for every stored credential. Public
    /// because the sessions endpoint needs to recognise the caller's own token without
    /// going through the rotation machinery.
    /// </summary>
    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}

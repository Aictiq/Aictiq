using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Aictiq.SharedKernel.Domain;
using Aictiq.SharedKernel.Email;

namespace Aictiq.Modules.Identity.Domain;

/// <summary>What a one-time link is for. Stored as a smallint, so the numbers are the contract.</summary>
public enum SecurityTokenPurpose
{
    PasswordReset = 0,
    EmailChange = 1,
}

/// <summary>
/// A one-time link mailed to an address: "reset your password", "confirm this is your new
/// address". The token in the mail is the credential, so - exactly as for refresh tokens,
/// personal access tokens and invitations - only its SHA-256 is stored. A leaked database
/// hands out no working links, and there is no endpoint that could show one again.
///
/// Consumption is a single conditional <c>UPDATE ... WHERE used_at IS NULL AND expires_at
/// &gt; now</c>: two clicks on one link must produce one password change and one refusal,
/// and a read-then-check would let both through.
/// </summary>
/// <remarks>
/// A partial unique index (<c>ux_user_security_tokens_live</c>) allows one unused token
/// per user per purpose. Issuing a replacement therefore has to retire the incumbent
/// first, which is the rule we want anyway: only the newest link works, and a link that
/// went to the wrong inbox stops working the moment a new one is asked for.
/// </remarks>
public sealed class UserSecurityToken : EntityBase
{
    public const int TokenBytes = 32;

    /// <summary>
    /// An hour. A reset link is used within minutes of asking for it or not at all, and
    /// the window is how long a compromised mailbox is worth breaking into.
    /// </summary>
    public static readonly TimeSpan PasswordResetLifetime = TimeSpan.FromHours(1);

    /// <summary>
    /// A day. Longer than a reset because the address being confirmed may be one the
    /// person only reads in the evening, and the link changes nothing on its own.
    /// </summary>
    public static readonly TimeSpan EmailChangeLifetime = TimeSpan.FromHours(24);

    public const string PasswordResetTemplate = "password-reset";
    public const string EmailChangeTemplate = "email-change";

    public required string UserId { get; init; }

    public SecurityTokenPurpose Purpose { get; init; }

    public required string TokenHash { get; init; }

    /// <summary>
    /// The address being confirmed, for an email change and nothing else - a check
    /// constraint says so. It lives on the token rather than on the user because an
    /// unconfirmed address is not yet anybody's: parking it in a <c>pending_email</c>
    /// column would make "is this address taken" a question with two answers.
    /// </summary>
    public string? NewEmail { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset ExpiresAt { get; init; }

    public DateTimeOffset? UsedAt { get; set; }

    public static TimeSpan LifetimeFor(SecurityTokenPurpose purpose) =>
        purpose == SecurityTokenPurpose.EmailChange ? EmailChangeLifetime : PasswordResetLifetime;

    /// <param name="token">The plaintext, returned to the caller. Never stored, never recoverable.</param>
    public static UserSecurityToken Create(
        string userId, SecurityTokenPurpose purpose, DateTimeOffset now, out string token,
        string? newEmail = null)
    {
        token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TokenBytes));
        return new UserSecurityToken
        {
            UserId = userId,
            Purpose = purpose,
            TokenHash = Hash(token),
            NewEmail = newEmail?.Trim().ToLowerInvariant(),
            CreatedAt = now,
            ExpiresAt = now + LifetimeFor(purpose),
        };
    }

    /// <summary>
    /// Asks for the mail carrying this link. Through the outbox like every other email, in
    /// the same transaction as the token row: a "reset your password" message about a
    /// token that was rolled back is worse than no message. On an instance with no relay
    /// the row is parked as <c>skipped</c> and nobody can reset by mail - which is why
    /// <c>/health/ready</c> says so out loud.
    /// </summary>
    public void RequestEmail(string to, string template, IReadOnlyDictionary<string, string> variables) =>
        Raise(new SendEmailRequested(to, template, variables));

    /// <summary>Hex SHA-256 - the same convention as refresh and access tokens.</summary>
    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}

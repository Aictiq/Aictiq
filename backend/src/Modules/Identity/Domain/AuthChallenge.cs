using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Aictiq.Modules.Identity.Domain;

/// <summary>What a sign-in ticket is waiting for. Stored as a number, so the numbers are the contract.</summary>
public enum AuthChallengePurpose
{
    /// <summary>The password was right; the account still owes an authenticator or recovery code.</summary>
    TwoFactorLogin = 0,

    /// <summary>A signed-in person asked to add a passkey; the browser is creating one.</summary>
    PasskeyRegistration = 1,

    /// <summary>Somebody on the sign-in page asked to use a passkey; we do not know who yet.</summary>
    PasskeyLogin = 2,
}

/// <summary>
/// The state between two requests of one sign-in ceremony, held by the server.
///
/// ASP.NET Core Identity keeps this in a cookie: "password OK, second factor pending" is a
/// <c>TwoFactorUserId</c> cookie, and the WebAuthn challenge rides in another. We sign in
/// with tokens, not cookies, and the CLI has no cookie jar at all, so the state lives here
/// instead and the client holds only an opaque ticket. As with every other credential in
/// this module, only the ticket's SHA-256 is stored.
///
/// A ticket is short-lived and works once. A two-factor ticket survives a wrong code - the
/// person mistyped, and the lockout counter is what limits guessing - but it is spent by
/// the code that succeeds. A passkey ticket is spent by the first attempt, right or wrong:
/// its challenge is what makes an assertion fresh, and a second try at the same challenge
/// is a replay.
/// </summary>
public sealed class AuthChallenge
{
    public const int TicketBytes = 32;

    /// <summary>
    /// Five minutes: long enough to unlock a phone and read a code, short enough that a
    /// ticket lifted from a log is worthless by the time anyone reads the log.
    /// </summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    public Guid Id { get; init; } = Guid.CreateVersion7();

    public AuthChallengePurpose Purpose { get; init; }

    /// <summary>Null only for a passkey sign-in, which does not know whose passkey it will be.</summary>
    public string? UserId { get; init; }

    public required string TicketHash { get; init; }

    /// <summary>The WebAuthn state Identity hands back with the options. Null for a two-factor ticket.</summary>
    public string? State { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset ExpiresAt { get; init; }

    public DateTimeOffset? UsedAt { get; set; }

    /// <param name="ticket">The plaintext, returned to the client. Never stored.</param>
    public static AuthChallenge Create(
        AuthChallengePurpose purpose, string? userId, string? state, DateTimeOffset now,
        out string ticket)
    {
        ticket = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TicketBytes));
        return new AuthChallenge
        {
            Purpose = purpose,
            UserId = userId,
            TicketHash = Hash(ticket),
            State = state,
            CreatedAt = now,
            ExpiresAt = now + Lifetime,
        };
    }

    /// <summary>Hex SHA-256 - the same convention as the other tokens.</summary>
    public static string Hash(string ticket) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ticket)));
}

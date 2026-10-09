using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Aictiq.Modules.Identity.Auth;
using Aictiq.Modules.Identity.Domain;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Email;

namespace Aictiq.Modules.Identity.Endpoints;

/// <summary>
/// The 202 from <c>/auth/login</c> when the password was right but the account has
/// two-factor authentication on. No session yet: the ticket goes to
/// <c>/auth/login/two-factor</c> with a code, and only that issues tokens.
/// </summary>
public sealed record TwoFactorChallenge(string Ticket, DateTimeOffset ExpiresAt, bool TwoFactorRequired = true);

/// <summary>Exactly one of <c>Code</c> (from the authenticator app) and <c>RecoveryCode</c>.</summary>
public sealed record TwoFactorLoginRequest(string? Ticket, string? Code, string? RecoveryCode);

public sealed record TwoFactorStatus(bool Enabled, int RecoveryCodesLeft);

/// <param name="SharedKey">The secret, grouped in fours for typing into an app by hand.</param>
/// <param name="AuthenticatorUri">The <c>otpauth://</c> URI the QR code encodes.</param>
public sealed record TwoFactorSetup(string SharedKey, string AuthenticatorUri);

public sealed record EnableTwoFactorRequest(string? Code);

/// <summary>
/// Proof that the person at the keyboard is the account's owner, for turning two-factor
/// off or replacing the recovery codes: the password, or - for an account that has none -
/// a current authenticator code.
/// </summary>
public sealed record TwoFactorProofRequest(string? CurrentPassword, string? Code);

/// <summary>Shown once. Only their hashes are kept, so nobody can show them again.</summary>
public sealed record RecoveryCodes(IReadOnlyList<string> Codes);

/// <summary>
/// Two-factor authentication with an authenticator app (TOTP), and the recovery codes that
/// stand in for the phone when it is lost.
///
/// Opt-in and personal: nothing here runs at registration, and nothing forces it on. A
/// personal access token is unaffected - it is a credential its owner already minted from
/// a signed-in session, so the CLI and agents keep working whatever is set here.
///
/// The "password OK, code pending" state that Identity keeps in a cookie is an
/// <see cref="AuthChallenge"/> ticket instead, because sign-in here issues tokens and the
/// CLI has no cookie jar.
/// </summary>
public static class TwoFactorEndpoints
{
    public const int RecoveryCodeCount = 10;

    private const string RecoveryCodesLoginProvider = "[AspNetUserStore]";
    private const string RecoveryCodesTokenName = "RecoveryCodes";

    public static IEndpointRouteBuilder MapTwoFactorEndpoints(this IEndpointRouteBuilder api)
    {
        MapLoginStep(api);
        MapSettings(api);
        return api;
    }

    /// <summary>
    /// What <c>/auth/login</c> answers for an account with two-factor on, once the
    /// password has checked out.
    /// </summary>
    internal static async Task<IResult> ChallengeAsync(
        IdentityDbContext db, ApplicationUser user, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var (challenge, ticket) = await AuthChallenges.IssueAsync(
            db, AuthChallengePurpose.TwoFactorLogin, user.Id, state: null, now, cancellationToken);
        return Results.Accepted(value: new TwoFactorChallenge(ticket, challenge.ExpiresAt));
    }

    private static void MapLoginStep(IEndpointRouteBuilder api)
    {
        // The credential-guessing limiter, like the password step: a six-digit code is a
        // much smaller space than a password, and the lockout counter is the other guard.
        var group = api.MapGroup("/auth").WithTags("Auth").RequireRateLimiting("auth");

        group.MapPost("/login/two-factor", async Task<IResult> (
            TwoFactorLoginRequest request,
            HttpContext http,
            string? mode,
            UserManager<ApplicationUser> userManager,
            ITokenService tokenService,
            IdentityDbContext db,
            IOptions<JwtOptions> jwtOptions,
            IOptions<EmailOptions> emailOptions,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var code = Normalize(request.Code);
            var recoveryCode = NormalizeRecoveryCode(request.RecoveryCode);
            if ((code is null) == (recoveryCode is null))
            {
                return Invalid("code", "Enter the code from your authenticator app, or a recovery code.");
            }

            var now = timeProvider.GetUtcNow();
            var challenge = await AuthChallenges.FindLiveAsync(
                db, request.Ticket, AuthChallengePurpose.TwoFactorLogin, now, cancellationToken);
            var user = challenge?.UserId is null ? null : await userManager.FindByIdAsync(challenge.UserId);
            if (challenge is null || user is null || !user.IsActive || user.IsAgent || !user.TwoFactorEnabled)
            {
                return SignInExpired();
            }

            // Checked before the code, so a locked account cannot be used to test codes -
            // and so the answer does not depend on whether this guess was the right one.
            if (await userManager.IsLockedOutAsync(user))
            {
                return Locked();
            }

            var verified = code is not null
                ? await userManager.VerifyTwoFactorTokenAsync(
                    user, userManager.Options.Tokens.AuthenticatorTokenProvider, code)
                : (await userManager.RedeemTwoFactorRecoveryCodeAsync(user, recoveryCode!)).Succeeded;

            if (!verified)
            {
                // The same counter the password feeds: five wrong answers of either kind
                // lock the account for the same fifteen minutes.
                await userManager.AccessFailedAsync(user);
                if (await userManager.IsLockedOutAsync(user))
                {
                    IdentitySecurityMetrics.RecordLoginLockout();
                    return Locked();
                }

                return Results.Problem(
                    title: "That code is not valid.",
                    detail: code is not null
                        ? "Check that the time on your device is right, and enter the newest code."
                        : "Each recovery code works once. Try another one.",
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            // Spent only by the code that worked, so a typo does not send anyone back to
            // the password. Two requests racing with one ticket get one session.
            if (!await AuthChallenges.ConsumeAsync(db, challenge, now, cancellationToken))
            {
                return SignInExpired();
            }

            await userManager.ResetAccessFailedCountAsync(user);

            if (recoveryCode is not null)
            {
                var left = await userManager.CountRecoveryCodesAsync(user);
                SecurityNotices.Queue(db, http, emailOptions.Value, user, SecurityNotice.RecoveryCodeUsed, now,
                    left == 1 ? "You have 1 recovery code left." : $"You have {left} recovery codes left.");
                await db.SaveChangesAsync(cancellationToken);
            }

            var tokens = await tokenService.IssueAsync(user, cancellationToken);
            var roles = await userManager.GetRolesAsync(user);
            return Results.Ok(AuthEndpoints.Respond(
                http, mode, tokens, AuthEndpoints.ToSession(user, [.. roles]), jwtOptions.Value));
        });
    }

    private static void MapSettings(IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/me/two-factor").WithTags("Profile").RequireAuthorization();

        group.MapGet("", async Task<IResult> (
            ClaimsPrincipal principal,
            UserManager<ApplicationUser> userManager) =>
        {
            var user = await AuthEndpoints.FindAsync(principal, userManager);
            if (user is null)
            {
                return NotFound();
            }

            return Results.Ok(new TwoFactorStatus(
                user.TwoFactorEnabled,
                user.TwoFactorEnabled ? await userManager.CountRecoveryCodesAsync(user) : 0));
        });

        // Every write below needs `admin`, as changing the password does: each of them
        // decides what it takes to sign in to the account, and a narrowed token must not
        // be able to strip or replace the second factor of the person who minted it.

        group.MapPost("/setup", async Task<IResult> (
            ClaimsPrincipal principal,
            UserManager<ApplicationUser> userManager,
            CancellationToken cancellationToken) =>
        {
            var user = await AuthEndpoints.FindAsync(principal, userManager);
            if (user is null)
            {
                return NotFound();
            }

            if (user.IsAgent)
            {
                return AgentRefusal();
            }

            if (user.TwoFactorEnabled)
            {
                // Re-keying a running authenticator would lock its owner out on their next
                // sign-in. Turning it off first is the deliberate way to move phones.
                return Results.Problem(
                    title: "Two-factor authentication is already on.",
                    detail: "Turn it off first to set up a different authenticator app.",
                    type: ProblemTypes.Conflict,
                    statusCode: StatusCodes.Status409Conflict);
            }

            // A fresh key every time setup starts: a key that was shown once and abandoned
            // may have been seen by someone else, and it is not in use yet.
            await userManager.ResetAuthenticatorKeyAsync(user);
            var key = await userManager.GetAuthenticatorKeyAsync(user);
            if (string.IsNullOrEmpty(key))
            {
                return Results.Problem(
                    title: "Could not start two-factor setup.",
                    statusCode: StatusCodes.Status500InternalServerError);
            }

            return Results.Ok(new TwoFactorSetup(Group(key), AuthenticatorUri(user.Email ?? user.UserName ?? user.Id, key)));
        })
        .RequireScope(Scopes.Admin);

        group.MapPost("/enable", async Task<IResult> (
            EnableTwoFactorRequest request,
            HttpContext http,
            ClaimsPrincipal principal,
            UserManager<ApplicationUser> userManager,
            IdentityDbContext db,
            IOptions<EmailOptions> emailOptions,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var user = await AuthEndpoints.FindAsync(principal, userManager);
            if (user is null)
            {
                return NotFound();
            }

            if (user.IsAgent)
            {
                return AgentRefusal();
            }

            if (user.TwoFactorEnabled)
            {
                return Results.Problem(
                    title: "Two-factor authentication is already on.",
                    type: ProblemTypes.Conflict,
                    statusCode: StatusCodes.Status409Conflict);
            }

            var code = Normalize(request.Code);
            if (code is null)
            {
                return Invalid("code", "Enter the six-digit code from your authenticator app.");
            }

            if (string.IsNullOrEmpty(await userManager.GetAuthenticatorKeyAsync(user)))
            {
                return Invalid("code", "Start the setup again to get a QR code.");
            }

            // The proof that the app really holds the key. Without it, a mis-scanned code
            // would turn on a second factor nobody can produce.
            if (!await userManager.VerifyTwoFactorTokenAsync(
                    user, userManager.Options.Tokens.AuthenticatorTokenProvider, code))
            {
                return Invalid("code", "That code is not valid. Check that the time on your device is right and try again.");
            }

            var enabled = await userManager.SetTwoFactorEnabledAsync(user, true);
            if (!enabled.Succeeded)
            {
                return Results.Problem(
                    title: "Two-factor authentication could not be turned on.",
                    statusCode: StatusCodes.Status500InternalServerError);
            }

            var codes = await userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, RecoveryCodeCount);

            SecurityNotices.Queue(db, http, emailOptions.Value, user, SecurityNotice.TwoFactorEnabled,
                timeProvider.GetUtcNow());
            await db.SaveChangesAsync(cancellationToken);

            return Results.Ok(new RecoveryCodes([.. codes ?? []]));
        })
        .RequireScope(Scopes.Admin);

        group.MapPost("/recovery-codes", async Task<IResult> (
            TwoFactorProofRequest request,
            HttpContext http,
            ClaimsPrincipal principal,
            UserManager<ApplicationUser> userManager,
            IdentityDbContext db,
            IOptions<EmailOptions> emailOptions,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var user = await AuthEndpoints.FindAsync(principal, userManager);
            if (user is null)
            {
                return NotFound();
            }

            if (!user.TwoFactorEnabled)
            {
                return NotEnabled();
            }

            if (await ProveAsync(userManager, user, request) is { } refusal)
            {
                return refusal;
            }

            // Generating replaces the stored set, so every older code stops working here.
            var codes = await userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, RecoveryCodeCount);

            SecurityNotices.Queue(db, http, emailOptions.Value, user, SecurityNotice.RecoveryCodesRegenerated,
                timeProvider.GetUtcNow());
            await db.SaveChangesAsync(cancellationToken);

            return Results.Ok(new RecoveryCodes([.. codes ?? []]));
        })
        .RequireScope(Scopes.Admin);

        group.MapPost("/disable", async Task<IResult> (
            TwoFactorProofRequest request,
            HttpContext http,
            ClaimsPrincipal principal,
            UserManager<ApplicationUser> userManager,
            IdentityDbContext db,
            IOptions<EmailOptions> emailOptions,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var user = await AuthEndpoints.FindAsync(principal, userManager);
            if (user is null)
            {
                return NotFound();
            }

            if (!user.TwoFactorEnabled)
            {
                return NotEnabled();
            }

            if (await ProveAsync(userManager, user, request) is { } refusal)
            {
                return refusal;
            }

            var disabled = await userManager.SetTwoFactorEnabledAsync(user, false);
            if (!disabled.Succeeded)
            {
                return Results.Problem(
                    title: "Two-factor authentication could not be turned off.",
                    statusCode: StatusCodes.Status500InternalServerError);
            }

            // Nothing of the old setup survives: the key in the app and the printed codes
            // would otherwise come back to life if it were ever turned on again.
            await userManager.ResetAuthenticatorKeyAsync(user);
            await userManager.RemoveAuthenticationTokenAsync(user, RecoveryCodesLoginProvider, RecoveryCodesTokenName);

            SecurityNotices.Queue(db, http, emailOptions.Value, user, SecurityNotice.TwoFactorDisabled,
                timeProvider.GetUtcNow());
            await db.SaveChangesAsync(cancellationToken);

            return Results.NoContent();
        })
        .RequireScope(Scopes.Admin);
    }

    /// <summary>
    /// The password if the account has one and it was sent; otherwise a current
    /// authenticator code. Null when the proof holds.
    /// </summary>
    private static async Task<IResult?> ProveAsync(
        UserManager<ApplicationUser> userManager, ApplicationUser user, TwoFactorProofRequest request)
    {
        if (!string.IsNullOrEmpty(request.CurrentPassword))
        {
            return await userManager.HasPasswordAsync(user)
                && await userManager.CheckPasswordAsync(user, request.CurrentPassword)
                    ? null
                    : Invalid("currentPassword", "That is not your password.");
        }

        var code = Normalize(request.Code);
        if (code is null)
        {
            return Invalid("code", "Enter your password or a code from your authenticator app.");
        }

        return await userManager.VerifyTwoFactorTokenAsync(
            user, userManager.Options.Tokens.AuthenticatorTokenProvider, code)
            ? null
            : Invalid("code", "That code is not valid.");
    }

    /// <summary>Spaces and dashes are how codes are printed and read aloud, not part of them.</summary>
    private static string? Normalize(string? code)
    {
        var normalized = code?.Replace(" ", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal);
        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }

    /// <summary>
    /// Identity mints recovery codes as <c>XXXXX-XXXXX</c> and compares them exactly. Case,
    /// spaces and a dropped dash are how people copy them from paper, so those are put back.
    /// </summary>
    private static string? NormalizeRecoveryCode(string? code)
    {
        var normalized = Normalize(code)?.ToUpperInvariant();
        return normalized is { Length: 10 } ? $"{normalized[..5]}-{normalized[5..]}" : normalized;
    }

    private static string Group(string key)
    {
        var grouped = new StringBuilder();
        for (var i = 0; i < key.Length; i += 4)
        {
            if (i > 0) grouped.Append(' ');
            grouped.Append(key.AsSpan(i, Math.Min(4, key.Length - i)));
        }
        return grouped.ToString().ToLowerInvariant();
    }

    /// <summary>
    /// The de-facto Key URI format every authenticator app reads. Thirty seconds, six
    /// digits and SHA-1 are the defaults Identity's provider checks against, so they are
    /// left implicit as most apps expect.
    /// </summary>
    private static string AuthenticatorUri(string account, string key)
    {
        const string issuer = "Aictiq";
        return $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}"
            + $"?secret={key}&issuer={Uri.EscapeDataString(issuer)}&digits=6";
    }

    private static IResult SignInExpired() =>
        Results.Problem(
            title: "This sign-in has expired.",
            detail: "Enter your email and password again.",
            type: ProblemTypes.SignInExpired,
            statusCode: StatusCodes.Status401Unauthorized);

    private static IResult Locked() =>
        Results.Problem(
            title: "Account temporarily locked after too many failed sign-in attempts.",
            statusCode: StatusCodes.Status423Locked);

    private static IResult NotEnabled() =>
        Results.Problem(
            title: "Two-factor authentication is not on.",
            type: ProblemTypes.Conflict,
            statusCode: StatusCodes.Status409Conflict);

    private static IResult Invalid(string field, string message) =>
        Results.ValidationProblem(
            new Dictionary<string, string[]> { [field] = [message] }, type: ProblemTypes.Validation);

    private static IResult AgentRefusal() =>
        Results.Problem(
            title: "An agent cannot sign in interactively.",
            detail: "Its only credential is a personal access token, issued by its owner.",
            type: ProblemTypes.InsufficientRole,
            statusCode: StatusCodes.Status403Forbidden);

    private static IResult NotFound() =>
        Results.Problem(title: "User not found.", statusCode: StatusCodes.Status404NotFound);
}

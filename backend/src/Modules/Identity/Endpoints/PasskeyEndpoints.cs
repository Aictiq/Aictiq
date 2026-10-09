using System.Buffers.Text;
using System.Security.Claims;
using System.Text.Json;
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

/// <param name="Ticket">Goes back with the credential the browser makes from <c>Options</c>.</param>
/// <param name="Options">
/// <c>PublicKeyCredentialCreationOptionsJSON</c> or <c>PublicKeyCredentialRequestOptionsJSON</c>,
/// for <c>PublicKeyCredential.parseCreationOptionsFromJSON</c> / <c>parseRequestOptionsFromJSON</c>.
/// </param>
public sealed record PasskeyOptions(string Ticket, JsonElement Options);

/// <param name="Credential">The browser's <c>PublicKeyCredential.toJSON()</c>, unchanged.</param>
public sealed record AddPasskeyRequest(string? Ticket, JsonElement Credential, string? Name);

public sealed record RenamePasskeyRequest(string? Name);

/// <param name="Credential">The browser's <c>PublicKeyCredential.toJSON()</c>, unchanged.</param>
public sealed record PasskeyLoginRequest(string? Ticket, JsonElement Credential);

/// <param name="Id">The credential id, base64url - what the rename and remove URLs take.</param>
public sealed record PasskeySummary(string Id, string Name, DateTimeOffset CreatedAt, bool IsBackedUp);

/// <summary>
/// Passkeys (WebAuthn): adding, naming and removing them from a signed-in session, and
/// signing in with one from the login page.
///
/// Identity's <c>SignInManager</c> passkey helpers keep the challenge in a cookie and end
/// in a cookie sign-in. Neither fits a token-based API, so these call the lower-level
/// <see cref="IPasskeyHandler{TUser}"/> directly and keep the challenge state in an
/// <see cref="AuthChallenge"/> ticket. The cryptography - attestation, assertion, origin
/// and signature-counter checks - is entirely Identity's.
///
/// A passkey proves possession and (by default) user verification on the device, so a
/// passkey sign-in is a complete sign-in: it does not also ask for an authenticator code.
/// </summary>
public static class PasskeyEndpoints
{
    public const int MaxNameLength = 100;

    public static IEndpointRouteBuilder MapPasskeyEndpoints(this IEndpointRouteBuilder api)
    {
        MapSignIn(api);
        MapSettings(api);
        return api;
    }

    private static void MapSignIn(IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/auth/passkey").WithTags("Auth").RequireRateLimiting("auth");

        group.MapPost("/options", async Task<IResult> (
            HttpContext http,
            IPasskeyHandler<ApplicationUser> passkeys,
            IdentityDbContext db,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            // No user: the browser offers whichever passkeys it holds for this site, and the
            // one chosen says whose it is. Nothing here hints at which accounts exist.
            var options = await passkeys.MakeRequestOptionsAsync(null, http);
            var (_, ticket) = await AuthChallenges.IssueAsync(
                db, AuthChallengePurpose.PasskeyLogin, userId: null, options.AssertionState,
                timeProvider.GetUtcNow(), cancellationToken);

            return Results.Ok(new PasskeyOptions(ticket, Parse(options.RequestOptionsJson)));
        });

        group.MapPost("/login", async Task<IResult> (
            PasskeyLoginRequest request,
            HttpContext http,
            string? mode,
            IPasskeyHandler<ApplicationUser> passkeys,
            UserManager<ApplicationUser> userManager,
            ITokenService tokenService,
            IdentityDbContext db,
            EmailConfirmationPolicy confirmation,
            IOptions<JwtOptions> jwtOptions,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var now = timeProvider.GetUtcNow();
            var challenge = await AuthChallenges.FindLiveAsync(
                db, request.Ticket, AuthChallengePurpose.PasskeyLogin, now, cancellationToken);

            // Spent before the assertion is checked, right or wrong: the challenge is what
            // makes an assertion fresh, and a second try at the same one is a replay.
            if (challenge is null || !await AuthChallenges.ConsumeAsync(db, challenge, now, cancellationToken))
            {
                return SignInExpired();
            }

            if (request.Credential.ValueKind != JsonValueKind.Object)
            {
                return Invalid("credential", "The passkey response is missing.");
            }

            var result = await passkeys.PerformAssertionAsync(new PasskeyAssertionContext
            {
                HttpContext = http,
                CredentialJson = request.Credential.GetRawText(),
                AssertionState = challenge.State,
            });

            var user = result.Succeeded ? result.User : null;
            if (user is null || !user.IsActive || user.IsAgent)
            {
                return Results.Problem(
                    title: "That passkey could not be used to sign in.",
                    detail: "It may have been removed from your account. Sign in with your password instead.",
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            // The same rules as the password and Identity's own PasskeySignInAsync: a locked
            // or unconfirmed account does not get in through a side door.
            if (await userManager.IsLockedOutAsync(user))
            {
                return Results.Problem(
                    title: "Account temporarily locked after too many failed sign-in attempts.",
                    statusCode: StatusCodes.Status423Locked);
            }

            if (!user.EmailConfirmed && confirmation.Required)
            {
                return Results.Problem(
                    title: "Confirm your email address to sign in.",
                    type: ProblemTypes.EmailUnconfirmed,
                    statusCode: StatusCodes.Status403Forbidden);
            }

            // Stores the new signature counter, which is how a cloned authenticator is
            // caught the next time either copy is used.
            await userManager.AddOrUpdatePasskeyAsync(user, result.Passkey!);

            var tokens = await tokenService.IssueAsync(user, cancellationToken);
            var roles = await userManager.GetRolesAsync(user);
            return Results.Ok(AuthEndpoints.Respond(
                http, mode, tokens, AuthEndpoints.ToSession(user, [.. roles]), jwtOptions.Value));
        });
    }

    private static void MapSettings(IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/me/passkeys").WithTags("Profile").RequireAuthorization();

        group.MapGet("", async Task<IResult> (
            ClaimsPrincipal principal,
            UserManager<ApplicationUser> userManager) =>
        {
            var user = await AuthEndpoints.FindAsync(principal, userManager);
            if (user is null)
            {
                return NotFound();
            }

            var passkeys = await userManager.GetPasskeysAsync(user);
            return Results.Ok(passkeys.OrderBy(p => p.CreatedAt).Select(ToSummary).ToList());
        });

        // Adding, renaming and removing need `admin`, as the password does: a passkey is a
        // way in, and a narrowed token must not be able to give itself one.

        group.MapPost("/options", async Task<IResult> (
            HttpContext http,
            ClaimsPrincipal principal,
            UserManager<ApplicationUser> userManager,
            IPasskeyHandler<ApplicationUser> passkeys,
            IdentityDbContext db,
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

            var options = await passkeys.MakeCreationOptionsAsync(new PasskeyUserEntity
            {
                Id = user.Id,
                Name = user.Email ?? user.UserName ?? user.Id,
                DisplayName = string.IsNullOrWhiteSpace(user.FullName) ? user.Email ?? user.Id : user.FullName,
            }, http);

            var (_, ticket) = await AuthChallenges.IssueAsync(
                db, AuthChallengePurpose.PasskeyRegistration, user.Id, options.AttestationState,
                timeProvider.GetUtcNow(), cancellationToken);

            return Results.Ok(new PasskeyOptions(ticket, Parse(options.CreationOptionsJson)));
        })
        .RequireScope(Scopes.Admin);

        group.MapPost("", async Task<IResult> (
            AddPasskeyRequest request,
            HttpContext http,
            ClaimsPrincipal principal,
            UserManager<ApplicationUser> userManager,
            IPasskeyHandler<ApplicationUser> passkeys,
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

            var name = request.Name?.Trim() ?? "";
            if (name.Length is 0 or > MaxNameLength)
            {
                return Invalid("name", $"Give the passkey a name of up to {MaxNameLength} characters.");
            }

            if (request.Credential.ValueKind != JsonValueKind.Object)
            {
                return Invalid("credential", "The passkey response is missing.");
            }

            var now = timeProvider.GetUtcNow();
            var challenge = await AuthChallenges.FindLiveAsync(
                db, request.Ticket, AuthChallengePurpose.PasskeyRegistration, now, cancellationToken);
            // Another person's ticket is as good as none: the state names the user it was
            // minted for, and that is whose account the passkey would land on.
            if (challenge is null || challenge.UserId != user.Id
                || !await AuthChallenges.ConsumeAsync(db, challenge, now, cancellationToken))
            {
                return Invalid("ticket", "This passkey request has expired. Start again.");
            }

            var result = await passkeys.PerformAttestationAsync(new PasskeyAttestationContext
            {
                HttpContext = http,
                CredentialJson = request.Credential.GetRawText(),
                AttestationState = challenge.State,
            });

            if (!result.Succeeded)
            {
                return Invalid("credential", $"The passkey could not be added: {result.Failure.Message}");
            }

            var passkey = result.Passkey;
            passkey.Name = name;
            var added = await userManager.AddOrUpdatePasskeyAsync(user, passkey);
            if (!added.Succeeded)
            {
                return Results.ValidationProblem(
                    new Dictionary<string, string[]>
                    {
                        ["credential"] = [.. added.Errors.Select(e => e.Description)]
                    },
                    type: ProblemTypes.Validation);
            }

            SecurityNotices.Queue(db, http, emailOptions.Value, user, SecurityNotice.PasskeyAdded, now, name);
            await db.SaveChangesAsync(cancellationToken);

            var summary = ToSummary(passkey);
            return Results.Created($"/api/v1/me/passkeys/{summary.Id}", summary);
        })
        .RequireScope(Scopes.Admin);

        group.MapPatch("/{id}", async Task<IResult> (
            string id,
            RenamePasskeyRequest request,
            ClaimsPrincipal principal,
            UserManager<ApplicationUser> userManager) =>
        {
            var user = await AuthEndpoints.FindAsync(principal, userManager);
            var passkey = user is null ? null : await FindAsync(userManager, user, id);
            if (passkey is null)
            {
                return PasskeyNotFound();
            }

            var name = request.Name?.Trim() ?? "";
            if (name.Length is 0 or > MaxNameLength)
            {
                return Invalid("name", $"Give the passkey a name of up to {MaxNameLength} characters.");
            }

            passkey.Name = name;
            await userManager.AddOrUpdatePasskeyAsync(user!, passkey);
            return Results.Ok(ToSummary(passkey));
        })
        .RequireScope(Scopes.Admin);

        group.MapDelete("/{id}", async Task<IResult> (
            string id,
            HttpContext http,
            ClaimsPrincipal principal,
            UserManager<ApplicationUser> userManager,
            IdentityDbContext db,
            IOptions<EmailOptions> emailOptions,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var user = await AuthEndpoints.FindAsync(principal, userManager);
            var passkey = user is null ? null : await FindAsync(userManager, user, id);
            if (passkey is null)
            {
                return PasskeyNotFound();
            }

            // No "last way in" guard as for providers: removing a passkey never removes the
            // password or a provider, and an account cannot have only passkeys - it was
            // signed in some other way to add the first one.
            var removed = await userManager.RemovePasskeyAsync(user!, passkey.CredentialId);
            if (!removed.Succeeded)
            {
                return PasskeyNotFound();
            }

            SecurityNotices.Queue(db, http, emailOptions.Value, user!, SecurityNotice.PasskeyRemoved,
                timeProvider.GetUtcNow(), passkey.Name);
            await db.SaveChangesAsync(cancellationToken);

            return Results.NoContent();
        })
        .RequireScope(Scopes.Admin);
    }

    private static async Task<UserPasskeyInfo?> FindAsync(
        UserManager<ApplicationUser> userManager, ApplicationUser user, string id)
    {
        byte[] credentialId;
        try
        {
            credentialId = Base64Url.DecodeFromChars(id);
        }
        catch (FormatException)
        {
            return null;
        }

        return credentialId.Length == 0 ? null : await userManager.GetPasskeyAsync(user, credentialId);
    }

    private static PasskeySummary ToSummary(UserPasskeyInfo passkey) =>
        new(Base64Url.EncodeToString(passkey.CredentialId), passkey.Name ?? "Passkey", passkey.CreatedAt,
            passkey.IsBackedUp);

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static IResult SignInExpired() =>
        Results.Problem(
            title: "This sign-in has expired.",
            detail: "Try the passkey again.",
            type: ProblemTypes.SignInExpired,
            statusCode: StatusCodes.Status401Unauthorized);

    private static IResult Invalid(string field, string message) =>
        Results.ValidationProblem(
            new Dictionary<string, string[]> { [field] = [message] }, type: ProblemTypes.Validation);

    private static IResult AgentRefusal() =>
        Results.Problem(
            title: "An agent cannot have a passkey.",
            detail: "Its only credential is a personal access token, issued by its owner.",
            type: ProblemTypes.InsufficientRole,
            statusCode: StatusCodes.Status403Forbidden);

    private static IResult NotFound() =>
        Results.Problem(title: "User not found.", statusCode: StatusCodes.Status404NotFound);

    private static IResult PasskeyNotFound() =>
        Results.Problem(title: "Passkey not found.", statusCode: StatusCodes.Status404NotFound);
}

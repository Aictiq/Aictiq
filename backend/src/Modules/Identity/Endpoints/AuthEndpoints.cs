using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Aictiq.Modules.Identity.Auth;
using Aictiq.Modules.Identity.Domain;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Email;
using Aictiq.SharedKernel.Turnstile;

namespace Aictiq.Modules.Identity.Endpoints;

/// <param name="InvitationToken">
/// The invitation link the person arrived with, when they did. Registering from it with the
/// very address it was mailed to is proof enough of the mailbox, so the account starts
/// confirmed and signed in. Any other address - a forwarded link - confirms by mail as usual.
/// </param>
/// <param name="Next">
/// Where the confirmation link should send them once the address is confirmed: a path on
/// this origin, typically the invitation they have not accepted yet. Anything else is dropped.
/// </param>
public sealed record RegisterRequest(
    string? Email, string? Password, string? FirstName, string? LastName,
    string? InvitationToken = null, string? Next = null);

/// <summary>
/// The 202 from registration when the address still has to be confirmed. No session: the
/// account exists but cannot be used until the link in the mail is followed.
/// </summary>
public sealed record RegistrationPending(string Email, bool EmailConfirmationRequired = true);

/// <summary>What the sign-in and sign-up pages need to know before they render.</summary>
/// <param name="TurnstileSiteKey">Null when this instance does not challenge anonymous forms.</param>
public sealed record AuthChallengeInfo(string? TurnstileSiteKey);
public sealed record LoginRequest(string? Email, string? Password);
public sealed record RefreshRequest(string? RefreshToken);
public sealed record UserInfo(string Id, string Email, string FirstName, string LastName, IReadOnlyList<string> Roles);

/// <summary>
/// What the SPA needs to render a signed-in shell. An agent never has one - it cannot log
/// in - but <c>IsAgent</c> is here because <c>/auth/session</c> is also how the CLI and the
/// MCP server answer <c>whoami</c>, and there it is the whole question.
/// </summary>
public sealed record SessionResponse(
    string Id, string Email, string FirstName, string LastName, string FullName,
    IReadOnlyList<string> Roles, bool IsAgent, string? AvatarKey, string? TimeZone, int UnreadCount = 0);

/// <summary>
/// Bearer-mode response. In cookie mode the tokens are in <c>Set-Cookie</c> instead and
/// these fields are null - the browser must never receive a credential it can read.
///
/// The user is the same <see cref="SessionResponse"/> <c>/auth/session</c> returns, so a
/// client that signs in and a client that resumes a cookie session hold one shape.
/// </summary>
public sealed record AuthResponse(
    string? AccessToken, DateTimeOffset AccessTokenExpiresAt, string? RefreshToken, SessionResponse User);

public static class AuthEndpoints
{
    /// <summary>
    /// The refresh token a non-browser client puts in the request body.
    ///
    /// Read by hand rather than bound as a <c>RefreshRequest?</c> parameter. An inferred
    /// JSON body parameter makes the endpoint match <c>application/json</c> only, so a
    /// POST with no body - which is exactly what the SPA sends to sign out and to rotate
    /// a cookie session, because there is nothing for it to send - matched no endpoint
    /// and fell through to the API's 404 fallback in Program.cs. Signing out then never
    /// cleared the cookies and silent refresh never succeeded.
    ///
    /// Returns null when there is no JSON body, which leaves "no token presented" to the
    /// caller to interpret: a required-field problem for refresh, and a no-op for logout.
    /// </summary>
    private static async Task<string?> ReadRefreshTokenAsync(
        HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentLength is null or 0 || !request.HasJsonContentType())
        {
            return null;
        }

        try
        {
            var payload = await request.ReadFromJsonAsync<RefreshRequest>(cancellationToken);
            return payload?.RefreshToken;
        }
        catch (JsonException)
        {
            // A body we cannot parse carries no token. Treated as none rather than as a
            // 500, so a malformed request gets the same answer as an empty one.
            return null;
        }
    }

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/auth").WithTags("Auth").RequireRateLimiting("auth");

        // The "auth" limiter is a credential-guessing guard, so it is keyed by IP and
        // deliberately tight. The two authenticated reads below do not belong in it: they
        // already require a valid token, the SPA calls /session on every full page load,
        // and an office - or a developer reloading - shares one egress address. The
        // global per-IP limiter still covers them, exactly as it covers every other read.
        var session = api.MapGroup("/auth").WithTags("Auth");

        group.MapPost("/register", async (
            RegisterRequest request,
            HttpContext http,
            string? mode,
            UserManager<ApplicationUser> userManager,
            ITokenService tokenService,
            IOptions<JwtOptions> jwtOptions,
            IConfiguration configuration,
            EmailConfirmationPolicy confirmation,
            IInvitationLookup invitations,
            IdentityDbContext db,
            IOptions<EmailOptions> emailOptions,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            // Self-registration is on by default in Aictiq; flip the flag for
            // invite-only products.
            if (!configuration.GetValue("Features:SelfRegistration", true))
            {
                return Results.Problem(title: "Registration is disabled.", statusCode: StatusCodes.Status403Forbidden);
            }

            var errors = new Dictionary<string, string[]>();
            if (string.IsNullOrWhiteSpace(request.Email)) errors["email"] = ["Email is required."];
            if (string.IsNullOrWhiteSpace(request.Password)) errors["password"] = ["Password is required."];
            if (string.IsNullOrWhiteSpace(request.FirstName)) errors["firstName"] = ["First name is required."];
            if (string.IsNullOrWhiteSpace(request.LastName)) errors["lastName"] = ["Last name is required."];
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            // The invitation was mailed to one address. Registering from its link with that
            // same address proves the mailbox exactly as a confirmation link would.
            // Trimmed: a trailing space from autofill is not part of anybody's address.
            var email = request.Email!.Trim();
            var invitee = await invitations.FindPendingInviteeAsync(request.InvitationToken, cancellationToken);
            var confirmedByInvitation = invitee is not null
                && string.Equals(invitee, email, StringComparison.OrdinalIgnoreCase);

            var now = timeProvider.GetUtcNow();
            var user = new ApplicationUser
            {
                UserName = email,
                Email = email,
                EmailConfirmed = confirmedByInvitation,
                FirstName = request.FirstName!.Trim(),
                LastName = request.LastName!.Trim(),
                CreatedAt = now
            };

            var result = await userManager.CreateAsync(user, request.Password!);
            if (!result.Succeeded)
            {
                return Results.ValidationProblem(ToFieldErrors(result));
            }

            await userManager.AddToRoleAsync(user, Roles.User);

            if (!confirmedByInvitation && confirmation.Required)
            {
                // No session until the address is proven. A failure to queue the mail
                // leaves an account that the resend form can still reach, so it is not
                // worth unwinding the registration over.
                await CredentialEndpoints.RequestConfirmationAsync(
                    db, http, emailOptions.Value, user, request.Next, now, cancellationToken);

                return Results.Accepted(value: new RegistrationPending(user.Email!));
            }

            var tokens = await tokenService.IssueAsync(user, cancellationToken);
            return Results.Created("/api/v1/auth/session",
                Respond(http, mode, tokens, ToSession(user, [Roles.User]), jwtOptions.Value));
        })
        .RequireTurnstile("register");

        group.MapPost("/login", async (
            LoginRequest request,
            HttpContext http,
            string? mode,
            UserManager<ApplicationUser> userManager,
            SignInManager<ApplicationUser> signInManager,
            ITokenService tokenService,
            IOptions<JwtOptions> jwtOptions,
            EmailConfirmationPolicy confirmation,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["email"] = ["Email and password are required."]
                });
            }

            var user = await userManager.FindByEmailAsync(request.Email);
            // An agent has no password and must never acquire one: its only credential is
            // a personal access token, which is what makes "revoke the agent" a complete
            // sentence. Folded into the same answer as an unknown account so this is not
            // a way to enumerate which addresses are agents.
            if (user is null || !user.IsActive || user.IsAgent)
            {
                // Same response for unknown user and wrong password - no user enumeration.
                return Results.Problem(title: "Invalid email or password.", statusCode: StatusCodes.Status401Unauthorized);
            }

            var signIn = await signInManager.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: true);
            if (signIn.IsLockedOut)
            {
                IdentitySecurityMetrics.RecordLoginLockout();
                return Results.Problem(
                    title: "Account temporarily locked after too many failed sign-in attempts.",
                    statusCode: StatusCodes.Status423Locked);
            }
            if (!signIn.Succeeded)
            {
                return Results.Problem(title: "Invalid email or password.", statusCode: StatusCodes.Status401Unauthorized);
            }

            // After the password, never before: only the account's owner learns that the
            // address is unconfirmed, so this is not a way to probe who has signed up.
            if (!user.EmailConfirmed && confirmation.Required)
            {
                return Results.Problem(
                    title: "Confirm your email address to sign in.",
                    detail: "Follow the link we sent when you registered, or ask for a new one.",
                    type: ProblemTypes.EmailUnconfirmed,
                    statusCode: StatusCodes.Status403Forbidden);
            }

            var tokens = await tokenService.IssueAsync(user, cancellationToken);
            var roles = await userManager.GetRolesAsync(user);
            return Results.Ok(Respond(http, mode, tokens, ToSession(user, [.. roles]), jwtOptions.Value));
        })
        .RequireTurnstile("login");

        group.MapPost("/refresh", async (
            HttpContext http,
            string? mode,
            ITokenService tokenService,
            UserManager<ApplicationUser> userManager,
            IOptions<JwtOptions> jwtOptions,
            CancellationToken cancellationToken) =>
        {
            var useCookies = AuthCookies.UseCookies(http.Request, mode);
            // In cookie mode the caller has no way to send the token - it is httpOnly.
            var presented = useCookies
                ? http.Request.Cookies[AuthCookies.RefreshCookieName]
                : await ReadRefreshTokenAsync(http.Request, cancellationToken);

            if (string.IsNullOrWhiteSpace(presented))
            {
                if (useCookies)
                {
                    // No cookie means no session, not a malformed request.
                    AuthCookies.Clear(http);
                    return Results.Problem(title: "Not authenticated.", statusCode: StatusCodes.Status401Unauthorized);
                }
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["refreshToken"] = ["Refresh token is required."]
                });
            }

            var tokens = await tokenService.RefreshAsync(presented, cancellationToken);
            if (tokens is null)
            {
                // Reuse, expiry or revocation. The family is already dead; drop the
                // cookies so the SPA stops retrying with a token that can never work.
                if (useCookies)
                {
                    AuthCookies.Clear(http);
                }
                return Results.Problem(title: "Invalid refresh token.", statusCode: StatusCodes.Status401Unauthorized);
            }

            if (!useCookies)
            {
                return Results.Ok(tokens);
            }

            AuthCookies.Issue(http, tokens, jwtOptions.Value);
            var user = await CurrentUserAsync(userManager, tokens.AccessToken);
            return user is null
                ? Results.NoContent()
                : Results.Ok(new AuthResponse(null, tokens.AccessTokenExpiresAt, null, user));
        });

        // Not RequireAuthorization: signing out must work even when the access token has
        // already expired, and it must still clear the cookies.
        group.MapPost("/logout", async (
            HttpContext http,
            string? mode,
            ITokenService tokenService,
            CancellationToken cancellationToken) =>
        {
            var useCookies = AuthCookies.UseCookies(http.Request, mode);
            var presented = useCookies
                ? http.Request.Cookies[AuthCookies.RefreshCookieName]
                : await ReadRefreshTokenAsync(http.Request, cancellationToken);

            if (!string.IsNullOrWhiteSpace(presented))
            {
                await tokenService.RevokeAsync(presented, cancellationToken);
            }

            if (useCookies)
            {
                AuthCookies.Clear(http);
            }

            return Results.NoContent();
        });

        // Anonymous and cheap: the sign-in pages ask before rendering whether to show the
        // challenge widget, and with which public key.
        session.MapGet("/challenge", (IOptions<TurnstileOptions> turnstile) =>
            Results.Ok(new AuthChallengeInfo(
                turnstile.Value.IsEnabled ? turnstile.Value.SiteKey : null)))
            .AllowAnonymous();

        session.MapGet("/session", async (
            ClaimsPrincipal principal,
            UserManager<ApplicationUser> userManager,
            IUnreadNotificationCounter notifications,
            CancellationToken cancellationToken) =>
        {
            var user = await FindAsync(principal, userManager);
            if (user is null)
            {
                return Results.Problem(title: "User not found.", statusCode: StatusCodes.Status404NotFound);
            }

            var roles = await userManager.GetRolesAsync(user);
            var session = ToSession(user, [.. roles]) with
            {
                UnreadCount = await notifications.CountUnreadAsync(user.Id, cancellationToken)
            };
            return Results.Ok(session);
        }).RequireAuthorization();

        session.MapGet("/me", async (
            ClaimsPrincipal principal,
            UserManager<ApplicationUser> userManager) =>
        {
            var user = await FindAsync(principal, userManager);
            if (user is null)
            {
                return Results.Problem(title: "User not found.", statusCode: StatusCodes.Status404NotFound);
            }

            var roles = await userManager.GetRolesAsync(user);
            return Results.Ok(new UserInfo(user.Id, user.Email!, user.FirstName, user.LastName, [.. roles]));
        }).RequireAuthorization();

        return api;
    }

    /// <summary>
    /// Cookie mode puts the tokens in <c>Set-Cookie</c> and returns only the user; body
    /// mode returns them for the caller to store. One shape either way, so the client
    /// does not branch on which mode it asked for.
    /// </summary>
    internal static AuthResponse Respond(
        HttpContext http, string? mode, TokenPair tokens, SessionResponse user, JwtOptions options)
    {
        if (!AuthCookies.UseCookies(http.Request, mode))
        {
            return new AuthResponse(tokens.AccessToken, tokens.AccessTokenExpiresAt, tokens.RefreshToken, user);
        }

        AuthCookies.Issue(http, tokens, options);
        return new AuthResponse(null, tokens.AccessTokenExpiresAt, null, user);
    }

    internal static async Task<ApplicationUser?> FindAsync(
        ClaimsPrincipal principal, UserManager<ApplicationUser> userManager)
    {
        var userId = principal.FindFirstValue("sub");
        return userId is null ? null : await userManager.FindByIdAsync(userId);
    }

    /// <summary>
    /// Refresh runs unauthenticated (the access token is usually expired by then), so
    /// the user for the response body comes from the token just issued.
    /// </summary>
    internal static SessionResponse ToSession(ApplicationUser user, IReadOnlyList<string> roles) =>
        new(user.Id, user.Email!, user.FirstName, user.LastName, user.FullName, roles, user.IsAgent,
            user.AvatarKey, user.TimeZone);

    private static async Task<SessionResponse?> CurrentUserAsync(
        UserManager<ApplicationUser> userManager, string accessToken)
    {
        var subject = JwtSubject(accessToken);
        if (subject is null)
        {
            return null;
        }

        var user = await userManager.FindByIdAsync(subject);
        if (user is null)
        {
            return null;
        }

        var roles = await userManager.GetRolesAsync(user);
        return ToSession(user, [.. roles]);
    }

    /// <summary>
    /// Reads <c>sub</c> out of a token this process just signed. No validation is needed
    /// or implied - the token never left the API.
    /// </summary>
    private static string? JwtSubject(string accessToken)
    {
        var handler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
        return handler.CanReadToken(accessToken)
            ? handler.ReadJwtToken(accessToken).Subject
            : null;
    }

    /// <summary>Maps Identity errors onto the request fields so clients can render them inline.</summary>
    internal static Dictionary<string, string[]> ToFieldErrors(IdentityResult result)
    {
        var errors = new Dictionary<string, List<string>>();
        foreach (var error in result.Errors)
        {
            var key = error.Code.Contains("Password", StringComparison.OrdinalIgnoreCase) ? "password"
                : error.Code.Contains("Email", StringComparison.OrdinalIgnoreCase)
                  || error.Code.Contains("UserName", StringComparison.OrdinalIgnoreCase) ? "email"
                : "";
            (errors.TryGetValue(key, out var list) ? list : errors[key] = []).Add(error.Description);
        }
        return errors.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());
    }
}

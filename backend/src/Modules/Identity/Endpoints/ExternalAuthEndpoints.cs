using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Aictiq.Modules.Identity.Auth;
using Aictiq.Modules.Identity.Domain;
using Aictiq.Modules.Identity.External;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Authorization;

namespace Aictiq.Modules.Identity.Endpoints;

/// <param name="Name">The value to put in the URL: <c>google</c>, <c>github</c>.</param>
public sealed record ExternalProviderView(string Name, string DisplayName);

/// <param name="CanUnlink">
/// False when this is the only way the account can be signed into. Removing it would lock
/// its owner out, so the API refuses and the UI does not offer.
/// </param>
public sealed record ExternalLoginView(string Provider, string DisplayName, bool CanUnlink);

/// <summary>
/// Sign in with Google or GitHub, and link those accounts to an existing one.
///
/// The rule the whole flow turns on: <b>an unverified provider email proves nothing</b>.
/// It is never used to find an existing account and never used to create one — a provider
/// that lets someone type any address without proving it would otherwise be a way to claim
/// somebody else's Aictiq account. Both refusals are distinct error codes rather than one
/// generic failure, because the two have different remedies.
/// </summary>
public static class ExternalAuthEndpoints
{
    /// <summary>The provider would not say the address is theirs.</summary>
    public const string EmailUnverifiedError = "email-unverified";

    /// <summary>An account holds that address, but neither side has proved ownership.</summary>
    public const string LinkRequiredError = "account-exists-link-required";

    public const string ProviderUnavailableError = "provider-unavailable";
    public const string ProviderFailedError = "provider-failed";

    public static IEndpointRouteBuilder MapExternalAuthEndpoints(this IEndpointRouteBuilder api)
    {
        MapProviders(api);
        MapStart(api);
        MapCallback(api);
        MapLogins(api);
        return api;
    }

    private static void MapProviders(IEndpointRouteBuilder api)
    {
        // Anonymous, and the reason the login page knows which buttons to draw. An
        // instance with no provider credentials is a supported deployment: the list is
        // simply empty and only the password form renders.
        api.MapGet("/auth/providers", (IOptions<ExternalAuthOptions> options) =>
            Results.Ok(options.Value.Configured()
                .Select(p => new ExternalProviderView(p.Name, DisplayName(p.Name)))
                .ToList()))
            .WithTags("Auth")
            .AllowAnonymous();
    }

    private static void MapStart(IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/auth/external/{provider}").WithTags("Auth").RequireRateLimiting("auth");

        group.MapGet("/", (
            string provider,
            string? next,
            string? invite,
            IOptions<ExternalAuthOptions> options) =>
        {
            if (!IsAvailable(provider, options.Value))
            {
                return Redirect($"/login?error={ProviderUnavailableError}");
            }

            var properties = new AuthenticationProperties
            {
                // Where the handler sends the browser once the provider has answered and
                // the external cookie is set — our own callback, not the SPA.
                RedirectUri = ExternalAuthExtensions.CompletionPath(provider),
            };
            // Carried through the OAuth `state` parameter, which the framework protects.
            // Only keys with a value: the state serializer writes a null as an empty
            // string, so an absent item is the only way to say "absent".
            Set(properties, "next", SafeNext(next));
            Set(properties, "invite", invite);

            return Results.Challenge(properties, [provider]);
        })
        .AllowAnonymous();

        // Linking has to be a top-level navigation — a challenge is a 302 to another
        // origin, which fetch cannot follow — so it is a GET the SPA sends the browser to,
        // not the POST the ticket sketched. The auth cookie rides along because it is
        // SameSite=Lax and this is a same-origin navigation.
        group.MapGet("/link", (
            string provider,
            string? next,
            ClaimsPrincipal principal,
            IOptions<ExternalAuthOptions> options) =>
        {
            if (!IsAvailable(provider, options.Value))
            {
                return Redirect($"/login?error={ProviderUnavailableError}");
            }

            var properties = new AuthenticationProperties
            {
                RedirectUri = ExternalAuthExtensions.CompletionPath(provider),
            };
            Set(properties, "next", SafeNext(next));
            // The account to attach this login to. Its presence is what turns the callback
            // from "sign in" into "link", so the callback never has to guess.
            Set(properties, "link", principal.FindFirstValue("sub"));

            return Results.Challenge(properties, [provider]);
        })
        .RequireAuthorization();
    }

    private static void MapCallback(IEndpointRouteBuilder api)
    {
        api.MapGet("/auth/external/{provider}/callback", async Task<IResult> (
            string provider,
            HttpContext http,
            UserManager<ApplicationUser> userManager,
            ITokenService tokenService,
            IOptions<JwtOptions> jwtOptions,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var result = await http.AuthenticateAsync(ExternalProviders.ExternalScheme);
            // Whatever happens next, the handshake cookie has done its job. Signing it out
            // first means an error path cannot leave a usable one behind.
            await http.SignOutAsync(ExternalProviders.ExternalScheme);

            if (!result.Succeeded || result.Principal is null)
            {
                return Redirect($"/login?error={ProviderFailedError}");
            }

            var items = result.Properties?.Items;
            var next = Item(items, "next");
            var invite = Item(items, "invite");
            var linkTo = Item(items, "link");

            if (ExternalIdentity.FromPrincipal(provider, result.Principal) is not { } external)
            {
                return Redirect($"/login?error={ProviderFailedError}");
            }

            if (!string.IsNullOrEmpty(linkTo))
            {
                return await LinkAsync(userManager, external, linkTo, next);
            }

            var (user, error) = await ResolveAsync(userManager, external, timeProvider);
            if (user is null)
            {
                return Redirect($"/login?error={error}");
            }

            // A browser is being redirected into the SPA: there is nowhere to put a bearer
            // token, so this is always cookie mode regardless of what the request looked like.
            var tokens = await tokenService.IssueAsync(user, cancellationToken);
            AuthCookies.Issue(http, tokens, jwtOptions.Value);

            // The invitation page runs next rather than the invitation being accepted here:
            // the person should see what they are joining, and that flow is already built
            // and tested.
            return Redirect(string.IsNullOrEmpty(invite) ? next ?? "/" : $"/invite/{invite}");
        })
        .WithTags("Auth")
        .AllowAnonymous();
    }

    private static void MapLogins(IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/me/logins").WithTags("Auth").RequireAuthorization();

        group.MapGet("/", async Task<IResult> (
            ClaimsPrincipal principal,
            UserManager<ApplicationUser> userManager) =>
        {
            var user = await FindAsync(principal, userManager);
            if (user is null)
            {
                return Results.Problem(title: "User not found.", statusCode: StatusCodes.Status404NotFound);
            }

            var logins = await userManager.GetLoginsAsync(user);
            var hasPassword = await userManager.HasPasswordAsync(user);

            return Results.Ok(logins
                .Select(login => new ExternalLoginView(
                    login.LoginProvider,
                    DisplayName(login.LoginProvider),
                    // Something else must remain to sign in with, or unlinking would lock
                    // them out of their own account.
                    hasPassword || logins.Count > 1))
                .ToList());
        })
        .RequireScope(Scopes.Read);

        group.MapDelete("/{provider}", async Task<IResult> (
            string provider,
            ClaimsPrincipal principal,
            UserManager<ApplicationUser> userManager) =>
        {
            var user = await FindAsync(principal, userManager);
            if (user is null)
            {
                return Results.Problem(title: "User not found.", statusCode: StatusCodes.Status404NotFound);
            }

            var logins = await userManager.GetLoginsAsync(user);
            var login = logins.FirstOrDefault(l =>
                string.Equals(l.LoginProvider, provider, StringComparison.OrdinalIgnoreCase));

            if (login is null)
            {
                return Results.Problem(
                    title: "Not found.",
                    detail: "That sign-in method is not linked to your account.",
                    statusCode: StatusCodes.Status404NotFound);
            }

            if (!await userManager.HasPasswordAsync(user) && logins.Count == 1)
            {
                return Results.Problem(
                    title: "That is your only way in.",
                    detail: "Set a password or link another provider before removing this one.",
                    type: ProblemTypes.Conflict,
                    statusCode: StatusCodes.Status409Conflict);
            }

            await userManager.RemoveLoginAsync(user, login.LoginProvider, login.ProviderKey);
            return Results.NoContent();
        })
        .RequireScope(Scopes.Write);
    }

    // ------------------------------------------------------------------------- resolving

    /// <summary>
    /// Which Aictiq account this provider identity is, creating one if it is nobody yet.
    ///
    /// The order matters and each step is a deliberate refusal to guess:
    /// <list type="number">
    /// <item>A login already linked is the whole answer — no email involved.</item>
    /// <item>Otherwise the provider must vouch for the address. An unverified one stops
    /// here: it is not evidence of anything.</item>
    /// <item>A local account with that address is linked only if <em>it</em> also proved
    /// the address. Two unproven claims do not make a match.</item>
    /// <item>Nobody has it: create the account. The address is confirmed because the
    /// provider verified it, so there is no second confirmation to ask for.</item>
    /// </list>
    /// </summary>
    private static async Task<(ApplicationUser? User, string? Error)> ResolveAsync(
        UserManager<ApplicationUser> userManager, ExternalIdentity external, TimeProvider timeProvider)
    {
        var linked = await userManager.FindByLoginAsync(external.Provider, external.ProviderKey);
        if (linked is not null)
        {
            return linked.IsActive ? (linked, null) : (null, ProviderFailedError);
        }

        if (string.IsNullOrWhiteSpace(external.Email) || !external.EmailVerified)
        {
            return (null, EmailUnverifiedError);
        }

        var existing = await userManager.FindByEmailAsync(external.Email);
        if (existing is not null)
        {
            if (!existing.EmailConfirmed || !existing.IsActive)
            {
                // Someone registered this address without confirming it. Linking now would
                // hand the account to whoever proved the address second.
                return (null, LinkRequiredError);
            }

            await userManager.AddLoginAsync(existing, Login(external));
            return (existing, null);
        }

        var created = new ApplicationUser
        {
            UserName = external.Email,
            Email = external.Email,
            // The provider verified it, so there is nothing left for us to confirm.
            EmailConfirmed = true,
            FirstName = external.FirstName ?? "",
            LastName = external.LastName ?? "",
            CreatedAt = timeProvider.GetUtcNow(),
        };

        // No password at all, rather than a random one: an account with a password nobody
        // knows is an account whose reset flow is the real way in.
        var result = await userManager.CreateAsync(created);
        if (!result.Succeeded)
        {
            return (null, ProviderFailedError);
        }

        await userManager.AddToRoleAsync(created, Roles.User);
        await userManager.AddLoginAsync(created, Login(external));
        return (created, null);
    }

    private static async Task<IResult> LinkAsync(
        UserManager<ApplicationUser> userManager, ExternalIdentity external, string userId, string? next)
    {
        var user = await userManager.FindByIdAsync(userId);
        if (user is null)
        {
            return Redirect($"/login?error={ProviderFailedError}");
        }

        var owner = await userManager.FindByLoginAsync(external.Provider, external.ProviderKey);
        if (owner is not null)
        {
            // Already attached — to them, which is a no-op, or to somebody else, which
            // must not be silently moved.
            return Redirect(owner.Id == user.Id
                ? next ?? "/"
                : $"{next ?? "/"}?error={LinkRequiredError}");
        }

        await userManager.AddLoginAsync(user, Login(external));
        return Redirect(next ?? "/");
    }

    private static UserLoginInfo Login(ExternalIdentity external) =>
        new(external.Provider, external.ProviderKey, DisplayName(external.Provider));

    private static bool IsAvailable(string provider, ExternalAuthOptions options) =>
        ExternalProviders.IsKnown(provider)
        && options.Configured().Any(p => p.Name == provider);

    private static async Task<ApplicationUser?> FindAsync(
        ClaimsPrincipal principal, UserManager<ApplicationUser> userManager)
    {
        var userId = principal.FindFirstValue("sub");
        return userId is null ? null : await userManager.FindByIdAsync(userId);
    }

    /// <summary>
    /// Only a path on this origin. `next` arrives from a query string that anyone can
    /// write, and an absolute URL there would turn sign-in into an open redirect —
    /// the classic way to make a phishing link look like it came from the product.
    /// </summary>
    private static string? SafeNext(string? next) =>
        !string.IsNullOrEmpty(next)
        && next.StartsWith('/')
        && !next.StartsWith("//", StringComparison.Ordinal)
        && !next.StartsWith("/\\", StringComparison.Ordinal)
            ? next
            : null;

    private static void Set(AuthenticationProperties properties, string key, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            properties.Items[key] = value;
        }
    }

    /// <summary>
    /// The state dictionary is <c>IDictionary</c>, which has no GetValueOrDefault — and an
    /// empty value has to read as absent, because that is what a round trip through the
    /// state serializer turns a null into.
    /// </summary>
    private static string? Item(IDictionary<string, string?>? items, string key) =>
        items is not null && items.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value)
            ? value
            : null;

    private static IResult Redirect(string location) => Results.Redirect(location);

    private static string DisplayName(string provider) => provider switch
    {
        ExternalProviders.Google => "Google",
        ExternalProviders.GitHub => "GitHub",
        _ => provider,
    };
}

using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Aictiq.Modules.Identity.Domain;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Authorization;

namespace Aictiq.Modules.Identity.Auth;

public static class PatDefaults
{
    /// <summary>The scheme a <c>aiq_</c> bearer is routed to.</summary>
    public const string Scheme = "Pat";

    /// <summary>
    /// The default scheme. It authenticates nothing itself — it looks at the bearer value
    /// and forwards to <see cref="Scheme"/> or to JWT, so one <c>Authorization</c> header
    /// can carry either kind of credential without every endpoint knowing which.
    /// </summary>
    public const string PolicyScheme = "Aictiq";

    /// <summary>Set when the token was rejected as revoked or expired, to sharpen the 401.</summary>
    public const string RevokedItemKey = "aictiq.auth.pat.revoked";

    /// <summary>
    /// The presented bearer token, if it is one of ours. Readable before authentication
    /// has run, which is what lets the rate limiter partition by token rather than by IP.
    /// </summary>
    public static string? ReadToken(HttpRequest request)
    {
        if (!request.Headers.TryGetValue("Authorization", out var header))
        {
            return null;
        }

        var value = header.ToString();
        const string bearer = "Bearer ";
        if (!value.StartsWith(bearer, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var token = value[bearer.Length..].Trim();
        return PersonalAccessToken.LooksLikeToken(token) ? token : null;
    }
}

/// <summary>
/// Authenticates a personal access token.
///
/// The principal it builds is deliberately the <em>same shape</em> as the JWT one — short
/// claim names, <c>sub</c>, <c>name</c>, <c>role</c> — plus the two things only a token
/// has: the organization it is bound to (<c>org</c>) and its scopes (<c>scp</c>). Every
/// endpoint, filter and audit row downstream therefore behaves identically whether the
/// caller is a browser, the CLI or an agent.
/// </summary>
public sealed class PatAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    IdentityDbContext db,
    UserManager<ApplicationUser> userManager,
    TimeProvider timeProvider)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (PatDefaults.ReadToken(Request) is not { } presented)
        {
            // Not ours. NoResult rather than Fail: the policy scheme should not have sent
            // it here, but saying "no opinion" keeps the other schemes usable.
            return AuthenticateResult.NoResult();
        }

        var hash = PersonalAccessToken.Hash(presented);
        var token = await db.PersonalAccessTokens
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.TokenHash == hash, Context.RequestAborted);

        if (token is null)
        {
            // Same answer as a malformed one: a distinct message here would say whether a
            // guessed token had ever existed.
            return AuthenticateResult.Fail("Invalid token.");
        }

        var now = timeProvider.GetUtcNow();
        if (!token.IsUsableAt(now))
        {
            // Worth saying plainly: no amount of retrying or refreshing fixes this, and an
            // agent looping on it needs to be told to stop.
            Context.Items[PatDefaults.RevokedItemKey] = true;
            return AuthenticateResult.Fail("The token was revoked or has expired.");
        }

        var user = await userManager.FindByIdAsync(token.UserId);
        if (user is null || !user.IsActive)
        {
            return AuthenticateResult.Fail("Invalid token.");
        }

        await TouchAsync(token, now);

        var claims = new List<Claim>
        {
            new("sub", user.Id),
            new("name", user.FullName),
            // The claim every rendering surface reads to label an action as an agent's.
            new(PrincipalClaims.PrincipalType,
                user.IsAgent ? PrincipalClaims.AgentPrincipalType : PrincipalClaims.UserPrincipalType),
        };

        if (token.OrganizationId is { } organizationId)
        {
            claims.Add(new Claim(PrincipalClaims.Organization, organizationId.ToString()));
        }

        // Scopes narrow, never grant — so the owner's roles come along unchanged and the
        // scope filter is what takes things away.
        claims.AddRange(token.Scopes.Select(scope => new Claim(PrincipalClaims.Scope, scope)));
        claims.AddRange((await userManager.GetRolesAsync(user)).Select(role => new Claim("role", role)));

        var identity = new ClaimsIdentity(claims, PatDefaults.Scheme, "name", "role");
        return AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), PatDefaults.Scheme));
    }

    /// <summary>
    /// problem+json rather than the bare 401 a custom scheme would otherwise produce, and
    /// a distinct <c>type</c> for a token that is dead rather than merely absent.
    /// </summary>
    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var revoked = Context.Items.ContainsKey(PatDefaults.RevokedItemKey);

        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.ContentType = "application/problem+json; charset=utf-8";
        await Response.WriteAsJsonAsync(new
        {
            type = revoked ? ProblemTypes.TokenRevoked : null,
            title = revoked ? "This access token no longer works." : "Not authenticated.",
            detail = revoked
                ? "It was revoked or has expired. Create a new one; retrying will not help."
                : "Present a valid access token.",
            status = StatusCodes.Status401Unauthorized,
        }, Context.RequestAborted);
    }

    /// <summary>
    /// Records that the token is alive, at most once every
    /// <see cref="PersonalAccessToken.LastUsedThrottle"/>. The condition is in the WHERE
    /// clause rather than in an <c>if</c> here so that concurrent requests with one token
    /// do not each decide to write.
    /// </summary>
    private async Task TouchAsync(PersonalAccessToken token, DateTimeOffset now)
    {
        if (token.LastUsedAt is { } lastUsed && now - lastUsed < PersonalAccessToken.LastUsedThrottle)
        {
            return;
        }

        var cutoff = now - PersonalAccessToken.LastUsedThrottle;
        await db.PersonalAccessTokens
            .Where(t => t.Id == token.Id && (t.LastUsedAt == null || t.LastUsedAt < cutoff))
            .ExecuteUpdateAsync(set => set.SetProperty(t => t.LastUsedAt, now), Context.RequestAborted);
    }
}


/// <summary>Registers the PAT scheme and the selector that routes a bearer to it or to JWT.</summary>
public static class PatAuthenticationExtensions
{
    public static AuthenticationBuilder AddPersonalAccessTokens(this AuthenticationBuilder builder)
    {
        builder.AddScheme<AuthenticationSchemeOptions, PatAuthenticationHandler>(
            PatDefaults.Scheme, configureOptions: null);

        // One Authorization header, several kinds of credential. The shape of the value is
        // enough to tell them apart, and doing it here means no endpoint has to.
        builder.AddPolicyScheme(PatDefaults.PolicyScheme, PatDefaults.PolicyScheme, options =>
        {
            options.ForwardDefaultSelector = context =>
            {
                if (PatDefaults.ReadToken(context.Request) is not null)
                {
                    return PatDefaults.Scheme;
                }

                // A credential another module owns (a runner's jrn_ secret) goes to
                // that module's handler. Matched by prefix and never falling through to JWT:
                // a value that claims to be one of those is refused by its own handler.
                if (BearerCredentialRoute.ReadBearer(context.Request) is { } bearer)
                {
                    foreach (var route in context.RequestServices.GetServices<BearerCredentialRoute>())
                    {
                        if (bearer.StartsWith(route.Prefix, StringComparison.Ordinal))
                        {
                            return route.Scheme;
                        }
                    }
                }

                return JwtBearerDefaults.AuthenticationScheme;
            };
        });

        return builder;
    }
}

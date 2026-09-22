using System.Security.Claims;
using System.Text.Encodings.Web;
using Aictiq.Modules.Automation.Domain;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aictiq.Modules.Automation.Auth;

public static class RunnerDefaults
{
    /// <summary>The scheme a <c>jrn_</c> bearer is forwarded to.</summary>
    public const string Scheme = "Runner";

    /// <summary>The authorization policy every runner-protocol route carries.</summary>
    public const string Policy = "Runner";

    /// <summary>
    /// The only surface a runner secret authenticates on. Everywhere else the handler has no
    /// opinion, so the request is simply unauthenticated: a runner secret cannot reach a
    /// person's endpoints even by accident, and a guessed secret learns nothing there either.
    /// </summary>
    public const string PathPrefix = "/api/v1/runner";

    public const string RevokedItemKey = "aictiq.auth.runner.revoked";

    /// <summary>The presented runner secret, if the bearer is shaped like one. Readable before authentication (the rate limiter).</summary>
    public static string? ReadToken(HttpRequest request) =>
        BearerCredentialRoute.ReadBearer(request) is { } bearer && RunnerCredential.LooksLikeToken(bearer) ? bearer : null;
}

/// <summary>
/// Authenticates a runner's <c>jrn_</c> secret.
///
/// The principal is deliberately <em>not</em> a user: it carries <c>typ = runner</c>, the
/// organization, the runner's id and the single scope <c>runner</c> - no <c>sub</c>. So
/// <c>RequireOrgRole</c> sees a non-member and answers 404, <c>/me</c> has nobody to describe,
/// and a scope check for <c>read</c> or <c>write</c> fails rather than reading an empty scope
/// set as a browser session.
/// </summary>
public sealed class RunnerAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    AutomationDbContext db,
    AmbientCurrentTenant tenant)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Path.StartsWithSegments(RunnerDefaults.PathPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        if (BearerCredentialRoute.ReadBearer(Request) is not { } bearer
            || !bearer.StartsWith(RunnerCredential.TokenPrefix, StringComparison.Ordinal))
        {
            return AuthenticateResult.NoResult();
        }

        if (!RunnerCredential.LooksLikeToken(bearer))
        {
            return AuthenticateResult.Fail("Invalid runner secret.");
        }

        var hash = RunnerCredential.Hash(bearer);
        Runner? runner;
        // One of the sanctioned pre-tenant reads: the secret is what decides the tenant. The
        // unique hash predicate is the guard, and RLS admits exactly this row while the hash
        // capability is in the session.
        using (tenant.UseRunnerTokenHash(hash))
        {
            runner = await db.Runners.IgnoreQueryFilters().AsNoTracking()
                .FirstOrDefaultAsync(r => r.TokenHash == hash, Context.RequestAborted);
        }

        if (runner is null)
        {
            return AuthenticateResult.Fail("Invalid runner secret.");
        }

        if (!runner.IsUsable)
        {
            // A disabled runner looping on its secret must be told to stop, exactly as an
            // agent on a revoked token is.
            Context.Items[RunnerDefaults.RevokedItemKey] = true;
            return AuthenticateResult.Fail("The runner was disabled or deleted.");
        }

        var identity = new ClaimsIdentity(
            [
                new Claim("name", runner.Name),
                new Claim(PrincipalClaims.PrincipalType, PrincipalClaims.RunnerPrincipalType),
                new Claim(PrincipalClaims.Organization, runner.OrganizationId.ToString()),
                new Claim(PrincipalClaims.Runner, runner.Id.ToString()),
                new Claim(PrincipalClaims.Scope, PrincipalClaims.RunnerScope),
            ],
            RunnerDefaults.Scheme, "name", "role");
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), RunnerDefaults.Scheme));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var revoked = Context.Items.ContainsKey(RunnerDefaults.RevokedItemKey);
        var runnerSurface = Request.Path.StartsWithSegments(RunnerDefaults.PathPrefix, StringComparison.OrdinalIgnoreCase);

        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.ContentType = "application/problem+json; charset=utf-8";
        await Response.WriteAsJsonAsync(new
        {
            type = revoked ? ProblemTypes.TokenRevoked : null,
            title = revoked ? "This runner no longer works." : "Not authenticated.",
            detail = revoked
                ? "It was disabled or deleted in Aictiq. Register a runner again; retrying will not help."
                : runnerSurface
                    ? "Present a valid runner secret."
                    : "A runner secret is accepted only by the runner protocol.",
            status = StatusCodes.Status401Unauthorized,
        }, Context.RequestAborted);
    }
}

public static class RunnerAuthenticationExtensions
{
    /// <summary>
    /// Registers the runner scheme, routes <c>jrn_</c> bearers to it through the default policy
    /// scheme, and adds the <see cref="RunnerDefaults.Policy"/> authorization policy.
    /// </summary>
    public static AuthenticationBuilder AddRunnerCredentials(this AuthenticationBuilder builder)
    {
        builder.AddScheme<AuthenticationSchemeOptions, RunnerAuthenticationHandler>(RunnerDefaults.Scheme, configureOptions: null);
        builder.Services.AddSingleton(new BearerCredentialRoute(RunnerCredential.TokenPrefix, RunnerDefaults.Scheme));
        builder.Services.Configure<AuthorizationOptions>(options =>
            options.AddPolicy(RunnerDefaults.Policy, policy => policy
                .AddAuthenticationSchemes(RunnerDefaults.Scheme)
                .RequireAuthenticatedUser()
                .RequireClaim(PrincipalClaims.PrincipalType, PrincipalClaims.RunnerPrincipalType)
                .RequireClaim(PrincipalClaims.Runner)
                .RequireClaim(PrincipalClaims.Organization)));
        return builder;
    }
}

using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Aictiq.Modules.Identity.Domain;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Contracts;

namespace Aictiq.Modules.Identity.Endpoints;

/// <param name="Display">
/// <c>aiq_a1b2c3d4…</c> — enough to tell two tokens apart in a list, and all that survives
/// of the secret.
/// </param>
public sealed record AccessTokenView(
    Guid Id, string Name, string Display, IReadOnlyList<string> Scopes, Guid? OrganizationId,
    DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, DateTimeOffset? LastUsedAt, bool IsExpired);

/// <param name="Token">The plaintext, returned exactly once. It cannot be shown again.</param>
public sealed record AccessTokenCreated(AccessTokenView Token, string Secret);

/// <param name="Scopes">
/// Empty means unscoped — the token may do whatever its owner may. Scopes only ever
/// narrow.
/// </param>
/// <param name="OrganizationId">
/// Optional. Set, and the token is refused against every other organization, which is what
/// makes an agent's credential useless outside the one it was made for.
/// </param>
public sealed record CreateAccessTokenRequest(
    string? Name, IReadOnlyList<string>? Scopes, Guid? OrganizationId, int? ExpiresInDays);

/// <summary>
/// Personal access tokens: the credential the CLI, the MCP server and agents use.
///
/// Creating one needs the <c>admin</c> scope, so a narrow token cannot mint a wider one —
/// a browser session carries no scopes and is unaffected, which is the point of an empty
/// scope set meaning "unscoped" rather than "permitted nothing".
/// </summary>
public static class TokenEndpoints
{
    /// <summary>A year. Long enough not to be a nuisance, short enough to be a rotation.</summary>
    public const int DefaultExpiryDays = 365;

    public const int MaxExpiryDays = 3650;

    public static IEndpointRouteBuilder MapTokenEndpoints(this IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/me/tokens").WithTags("Tokens").RequireAuthorization();

        group.MapGet("/", async (
            ClaimsPrincipal principal,
            IdentityDbContext db,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var userId = principal.FindFirstValue("sub");
            var tokens = await db.PersonalAccessTokens
                .AsNoTracking()
                // Revoked ones are gone, not greyed out: a revoked token is not a thing
                // anyone can act on, and a list of them is a list of nothing.
                .Where(t => t.UserId == userId && t.RevokedAt == null)
                .OrderByDescending(t => t.CreatedAt)
                .ToListAsync(cancellationToken);

            var now = timeProvider.GetUtcNow();
            return Results.Ok(tokens.Select(t => ToView(t, now)).ToList());
        })
        .RequireScope(Scopes.Read);

        group.MapPost("/", async (
            CreateAccessTokenRequest request,
            ClaimsPrincipal principal,
            ICurrentUser currentUser,
            IdentityDbContext db,
            IProjectAccess access,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            // An agent's credentials are issued by whoever is answerable for it, through
            // /orgs/{slug}/agents/{id}/tokens — always bound to its organization and always
            // visible to its owner. A token that could mint itself a successor here would be
            // one its owner never sees, possibly unbound, and revocation would stop being
            // "revoke what the agents screen lists".
            if (currentUser.IsAgent)
            {
                return Results.Problem(
                    title: "An agent cannot create its own tokens.",
                    detail: "Its owner or an organization admin issues them from the agents screen.",
                    type: ProblemTypes.InsufficientRole,
                    statusCode: StatusCodes.Status403Forbidden);
            }

            var userId = principal.FindFirstValue("sub")!;
            var errors = new Dictionary<string, string[]>();

            var name = request.Name?.Trim() ?? "";
            if (name.Length == 0)
            {
                errors["name"] = ["Name it after what will use it — 'laptop CLI', 'CI'."];
            }
            else if (name.Length > PersonalAccessToken.MaxNameLength)
            {
                errors["name"] = [$"Use {PersonalAccessToken.MaxNameLength} characters or fewer."];
            }

            var scopes = (request.Scopes ?? []).Select(s => s.Trim().ToLowerInvariant()).Distinct().ToList();
            if (scopes.Except(SharedKernel.Authorization.Scopes.All, StringComparer.Ordinal).Any())
            {
                errors["scopes"] = [$"Pick from: {string.Join(", ", SharedKernel.Authorization.Scopes.All)}."];
            }

            if (request.ExpiresInDays is { } days && days is < 1 or > MaxExpiryDays)
            {
                errors["expiresInDays"] = [$"Between 1 and {MaxExpiryDays} days, or leave it out."];
            }

            // Binding a token to an organization must not be a way to reach one you are
            // not in — it narrows, and a narrowing to somewhere you cannot go is nonsense.
            if (request.OrganizationId is { } organizationId
                && await access.GetOrgRoleAsync(userId, organizationId, cancellationToken) is null)
            {
                errors["organizationId"] = ["You are not a member of that organization."];
            }

            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors, type: ProblemTypes.Validation);
            }

            var now = timeProvider.GetUtcNow();
            var expiresAt = request.ExpiresInDays switch
            {
                { } explicitDays => now.AddDays(explicitDays),
                // Absent means the default rather than "never": a token that never expires
                // is a decision, and it should be made by asking for it.
                _ => now.AddDays(DefaultExpiryDays),
            };

            var token = PersonalAccessToken.Create(
                userId, request.OrganizationId, name, scopes, expiresAt, now, out var secret);

            db.PersonalAccessTokens.Add(token);
            // IAudited, so the creation lands in audit.audit_log in the same transaction —
            // with the hash excluded, because AuditingInterceptor treats TokenHash as
            // sensitive.
            await db.SaveChangesAsync(cancellationToken);

            return Results.Created($"/api/v1/me/tokens/{token.Id}",
                new AccessTokenCreated(ToView(token, now), secret));
        })
        // Admin, so a read-only or mcp-only token cannot mint a wider one. A browser
        // session has no scopes at all and is therefore unaffected.
        .RequireScope(Scopes.Admin);

        group.MapDelete("/{tokenId:guid}", async (
            Guid tokenId,
            ClaimsPrincipal principal,
            IdentityDbContext db,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var userId = principal.FindFirstValue("sub");
            var token = await db.PersonalAccessTokens
                .FirstOrDefaultAsync(t => t.Id == tokenId && t.UserId == userId, cancellationToken);

            if (token is null)
            {
                return Results.Problem(
                    title: "Not found.",
                    detail: "The token does not exist, or it is not yours.",
                    type: ProblemTypes.NotAMember,
                    statusCode: StatusCodes.Status404NotFound);
            }

            if (token.RevokedAt is null)
            {
                // Revoked, not deleted: the row is what the audit log's entries point at,
                // and retention prunes it later.
                token.RevokedAt = timeProvider.GetUtcNow();
                await db.SaveChangesAsync(cancellationToken);
            }

            return Results.NoContent();
        })
        .RequireScope(Scopes.Admin);

        return api;
    }

    internal static AccessTokenView ToView(PersonalAccessToken token, DateTimeOffset now) => new(
        token.Id, token.Name, token.Display, token.Scopes, token.OrganizationId,
        token.CreatedAt, token.ExpiresAt, token.LastUsedAt,
        token.ExpiresAt is { } expiry && expiry <= now);
}

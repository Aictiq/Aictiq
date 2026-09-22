using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Aictiq.Modules.Identity.Auth;
using Aictiq.Modules.Identity.Domain;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Authorization;

namespace Aictiq.Modules.Identity.Endpoints;

/// <param name="Id">The rotation family's id - what a person means by "this session".</param>
/// <param name="UserAgent">Verbatim, as the client sent it. Null for a sign-in with no browser.</param>
/// <param name="IsCurrent">
/// True for the session making this request, which is the one the UI must not offer to end
/// by accident.
/// </param>
public sealed record SessionView(
    Guid Id, string? UserAgent, DateTimeOffset StartedAt, DateTimeOffset LastSeenAt,
    DateTimeOffset ExpiresAt, bool IsCurrent);

/// <summary>
/// Where someone is signed in, and how to stop being signed in there.
///
/// A session is a refresh-token <em>family</em>: one sign-in and the chain of rotations
/// that followed it. That is already how reuse detection thinks about the world, so
/// "sign this device out" is exactly "revoke this family" - there is no second concept to
/// keep consistent with the first.
/// </summary>
public static class SessionEndpoints
{
    public static IEndpointRouteBuilder MapSessionEndpoints(this IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/me/sessions").WithTags("Profile").RequireAuthorization();

        group.MapGet("/", async Task<IResult> (
            ClaimsPrincipal principal,
            IdentityDbContext db,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var userId = principal.FindFirstValue("sub")!;
            var now = timeProvider.GetUtcNow();

            // Everything still capable of being part of a live session. Grouped in memory
            // rather than in SQL: the set is one person's own tokens inside the refresh
            // window, and the alternative is a correlated subquery per family to find the
            // newest row's user agent.
            var rows = await db.RefreshTokens.AsNoTracking()
                .Where(t => t.UserId == userId && t.RevokedAt == null && t.ExpiresAt > now)
                .Select(t => new { t.FamilyId, t.CreatedAt, t.ExpiresAt, t.UsedAt, t.UserAgent })
                .ToListAsync(cancellationToken);

            var current = CurrentFamily(principal);

            var sessions = rows
                .GroupBy(t => t.FamilyId)
                // A family whose every token has been spent is a chain that ended - the
                // successor was revoked or expired - not a place anyone is still signed in.
                .Where(g => g.Any(t => t.UsedAt is null))
                .Select(g => new SessionView(
                    g.Key,
                    g.OrderByDescending(t => t.CreatedAt).First().UserAgent,
                    g.Min(t => t.CreatedAt),
                    g.Max(t => t.CreatedAt),
                    g.Max(t => t.ExpiresAt),
                    g.Key == current))
                .OrderByDescending(s => s.IsCurrent)
                .ThenByDescending(s => s.LastSeenAt)
                .ToList();

            return Results.Ok(sessions);
        })
        .RequireScope(Scopes.Read);

        group.MapDelete("/{familyId:guid}", async Task<IResult> (
            Guid familyId,
            ClaimsPrincipal principal,
            HttpContext http,
            ITokenService tokens,
            CancellationToken cancellationToken) =>
        {
            var userId = principal.FindFirstValue("sub")!;
            var revoked = await tokens.RevokeFamilyForUserAsync(userId, familyId, cancellationToken);
            if (revoked == 0)
            {
                return Results.Problem(
                    title: "Not found.",
                    detail: "That session has already ended, or it is not yours.",
                    type: ProblemTypes.NotAMember,
                    statusCode: StatusCodes.Status404NotFound);
            }

            // Ending the session you are in is allowed - it is how you sign out a browser
            // you are about to hand to someone else - so the cookies have to go with it,
            // or the page keeps a refresh cookie that can only ever fail.
            if (CurrentFamily(principal) == familyId)
            {
                AuthCookies.Clear(http);
            }

            return Results.NoContent();
        })
        .RequireScope(Scopes.Write);

        group.MapDelete("/", async Task<IResult> (
            ClaimsPrincipal principal,
            ITokenService tokens,
            CancellationToken cancellationToken) =>
        {
            var userId = principal.FindFirstValue("sub")!;
            // Everywhere *else*. Signing out the browser that pressed the button reads as
            // an error rather than as a confirmation, and "sign out here" is the button
            // next to it.
            await tokens.RevokeAllForUserExceptAsync(userId, CurrentFamily(principal), cancellationToken);

            return Results.NoContent();
        })
        .RequireScope(Scopes.Write);

        return api;
    }

    /// <summary>
    /// Which session the caller is riding, from the access token's <c>sid</c>.
    ///
    /// Not from the refresh cookie: that cookie is scoped to the refresh endpoint and is
    /// therefore absent on every other request - including this one. A personal access
    /// token carries no <c>sid</c> at all, which is correct: a PAT is not a session and
    /// nothing in this list is the caller.
    /// </summary>
    internal static Guid? CurrentFamily(ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(PrincipalClaims.SessionId), out var familyId)
            ? familyId
            : null;
}

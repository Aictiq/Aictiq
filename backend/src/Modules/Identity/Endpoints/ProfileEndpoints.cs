using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Aictiq.Modules.Identity.Domain;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Http;
using Aictiq.SharedKernel.Storage;

namespace Aictiq.Modules.Identity.Endpoints;

/// <param name="HasPassword">
/// False for someone who only ever signed in with a provider. The security screen reads
/// it to offer "set a password" instead of "change password", and unlinking the last
/// provider depends on the same fact.
/// </param>
/// <param name="PendingEmail">
/// An address that has been asked for but not yet confirmed from its own inbox. Not on
/// the user row: an unconfirmed address belongs to nobody, and parking it there would
/// make "is this address taken" a question with two answers.
/// </param>
public sealed record ProfileView(
    string Id, string Email, string FirstName, string LastName, string FullName,
    string? TimeZone, string? AvatarKey, string? AvatarVersion, bool EmailConfirmed,
    bool HasPassword, bool IsAgent, IReadOnlyList<string> Roles, DateTimeOffset CreatedAt,
    string? PendingEmail);

/// <summary>
/// A null field is "leave it alone"; an empty string clears what can be cleared. Time zone
/// is the only one of the three that has an "unset" — a person with no zone of their own
/// reads times in their organization's.
/// </summary>
public sealed record UpdateProfileRequest(string? FirstName, string? LastName, string? TimeZone);

public sealed record AvatarUploadRequest(string? ContentType, long? ContentLength);

/// <param name="Key">Hand this back to the commit (<c>PUT /me/avatar</c>) once the PUT has succeeded.</param>
public sealed record AvatarUploadTicket(string Key, string UploadUrl, DateTimeOffset ExpiresAt, long MaxBytes);

public sealed record AvatarCommitRequest(string? Key);

/// <summary>
/// A person's own account: their name, their time zone, their picture.
///
/// The picture is the only part that touches the object store, and it follows the rule
/// that governs every upload in Aictiq — the bytes never pass through the API. The browser
/// asks for a presigned PUT, uploads to the store directly, then tells the API which key
/// to record. The commit is where the checks live, because a presigned URL fixes the
/// content type but cannot bound the size.
/// </summary>
public static class ProfileEndpoints
{
    public static IEndpointRouteBuilder MapProfileEndpoints(this IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/me").WithTags("Profile").RequireAuthorization();

        group.MapGet("/", async Task<IResult> (
            ClaimsPrincipal principal,
            UserManager<ApplicationUser> userManager,
            IdentityDbContext db,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var user = await AuthEndpoints.FindAsync(principal, userManager);
            if (user is null)
            {
                return NotFound();
            }

            return Results.Ok(await ToViewAsync(user, userManager, db, timeProvider, cancellationToken));
        })
        .RequireScope(Scopes.Read);

        group.MapPatch("/", async Task<IResult> (
            UpdateProfileRequest request,
            ClaimsPrincipal principal,
            UserManager<ApplicationUser> userManager,
            IdentityDbContext db,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var user = await AuthEndpoints.FindAsync(principal, userManager);
            if (user is null)
            {
                return NotFound();
            }

            if (AgentRefusal(user) is { } refusal)
            {
                return refusal;
            }

            var errors = new Dictionary<string, string[]>();

            var firstName = request.FirstName?.Trim();
            var lastName = request.LastName?.Trim();
            if (firstName is { Length: 0 }) errors["firstName"] = ["First name is required."];
            if (lastName is { Length: 0 }) errors["lastName"] = ["Last name is required."];
            if (firstName is { Length: > 100 }) errors["firstName"] = ["Use 100 characters or fewer."];
            if (lastName is { Length: > 100 }) errors["lastName"] = ["Use 100 characters or fewer."];

            // An empty string clears the override; anything else has to be a zone the
            // server can actually resolve, or every date this person reads is a guess.
            var timeZone = request.TimeZone?.Trim();
            if (timeZone is { Length: > 0 } && !TimeZoneInfo.TryFindSystemTimeZoneById(timeZone, out _))
            {
                errors["timeZone"] = ["Unknown time zone. Use an IANA id such as Europe/Sarajevo."];
            }

            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors, type: ProblemTypes.Validation);
            }

            if (firstName is not null) user.FirstName = firstName;
            if (lastName is not null) user.LastName = lastName;
            if (timeZone is not null) user.TimeZone = timeZone.Length == 0 ? null : timeZone;

            var result = await userManager.UpdateAsync(user);
            if (!result.Succeeded)
            {
                return Results.ValidationProblem(AuthEndpoints.ToFieldErrors(result), type: ProblemTypes.Validation);
            }

            return Results.Ok(await ToViewAsync(user, userManager, db, timeProvider, cancellationToken));
        })
        .RequireScope(Scopes.Write);

        group.MapPost("/avatar", async Task<IResult> (
            AvatarUploadRequest request,
            ClaimsPrincipal principal,
            UserManager<ApplicationUser> userManager,
            IBlobStorage storage,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var user = await AuthEndpoints.FindAsync(principal, userManager);
            if (user is null)
            {
                return NotFound();
            }

            if (AgentRefusal(user) is { } refusal)
            {
                return refusal;
            }

            var errors = new Dictionary<string, string[]>();
            if (!Avatar.IsAllowedContentType(request.ContentType))
            {
                errors["contentType"] = [$"Use one of: {string.Join(", ", Avatar.Extensions.Keys)}."];
            }

            // Checked here as well as at commit so the browser is told before it spends
            // the upload, not after. The commit check is the one that guarantees it.
            if (request.ContentLength is { } length && (length <= 0 || length > Avatar.MaxBytes))
            {
                errors["contentLength"] = [$"Pictures must be under {Avatar.MaxBytes / 1024 / 1024} MB."];
            }

            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors, type: ProblemTypes.Validation);
            }

            var contentType = request.ContentType!.Trim();
            var key = Avatar.NewKey(user.Id, contentType);
            var ttl = TimeSpan.FromMinutes(15);
            var url = await storage.PresignUploadAsync(
                key, contentType, Avatar.MaxBytes, ttl, cancellationToken);

            return Results.Ok(new AvatarUploadTicket(
                key, url.ToString(), timeProvider.GetUtcNow() + ttl, Avatar.MaxBytes));
        })
        .RequireScope(Scopes.Write)
        .RequireOperationRateLimit(OperationRateLimiter.Uploads);

        group.MapPut("/avatar", async Task<IResult> (
            AvatarCommitRequest request,
            ClaimsPrincipal principal,
            UserManager<ApplicationUser> userManager,
            IdentityDbContext db,
            IBlobStorage storage,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var user = await AuthEndpoints.FindAsync(principal, userManager);
            if (user is null)
            {
                return NotFound();
            }

            if (AgentRefusal(user) is { } refusal)
            {
                return refusal;
            }

            var key = request.Key?.Trim() ?? "";
            // The key must be one this account was given. Without the check, "commit my
            // avatar" would accept any key in the bucket and turn a profile picture into
            // a way to read somebody's attachment through a presigned URL.
            if (key.Length == 0 || !Avatar.BelongsTo(key, user.Id))
            {
                return Results.ValidationProblem(
                    new Dictionary<string, string[]> { ["key"] = ["That is not an upload you started."] },
                    type: ProblemTypes.Validation);
            }

            var metadata = await storage.HeadAsync(key, cancellationToken);
            if (metadata is null)
            {
                return Results.ValidationProblem(
                    new Dictionary<string, string[]> { ["key"] = ["The upload did not arrive. Try again."] },
                    type: ProblemTypes.Validation);
            }

            // The signature fixed the content type but could not bound the size, so this
            // is where the limit is actually enforced — and an object that broke it is
            // deleted rather than left paid for and unreferenced.
            if (metadata.ContentLength > Avatar.MaxBytes || !Avatar.IsAllowedContentType(metadata.ContentType))
            {
                await storage.DeleteAsync(key, cancellationToken);
                return Results.ValidationProblem(
                    new Dictionary<string, string[]>
                    {
                        ["key"] = [$"That file is not an image under {Avatar.MaxBytes / 1024 / 1024} MB."]
                    },
                    type: ProblemTypes.Validation);
            }

            var previous = user.AvatarKey;
            user.AvatarKey = key;

            var saved = await userManager.UpdateAsync(user);
            if (!saved.Succeeded)
            {
                // Checked rather than assumed: an unchecked save would be followed by the
                // delete below, leaving the row pointing at the old key and the old object
                // gone — a broken picture on every screen, and no way to put it back.
                return Results.ValidationProblem(
                    AuthEndpoints.ToFieldErrors(saved), type: ProblemTypes.Validation);
            }

            // After the row, not before: an orphaned object costs a few kilobytes, while a
            // row pointing at an object that is gone is a broken picture on every screen.
            if (previous is not null && previous != key)
            {
                await storage.DeleteAsync(previous, cancellationToken);
            }

            return Results.Ok(await ToViewAsync(user, userManager, db, timeProvider, cancellationToken));
        })
        .RequireScope(Scopes.Write)
        .RequireOperationRateLimit(OperationRateLimiter.Uploads);

        group.MapDelete("/avatar", async Task<IResult> (
            ClaimsPrincipal principal,
            UserManager<ApplicationUser> userManager,
            IBlobStorage storage,
            CancellationToken cancellationToken) =>
        {
            var user = await AuthEndpoints.FindAsync(principal, userManager);
            if (user is null)
            {
                return NotFound();
            }

            if (AgentRefusal(user) is { } refusal)
            {
                return refusal;
            }

            var previous = user.AvatarKey;
            if (previous is not null)
            {
                user.AvatarKey = null;
                var saved = await userManager.UpdateAsync(user);
                if (!saved.Succeeded)
                {
                    // Same order of operations as the commit: the object outlives the row
                    // that names it, never the other way round.
                    return Results.ValidationProblem(
                        AuthEndpoints.ToFieldErrors(saved), type: ProblemTypes.Validation);
                }

                await storage.DeleteAsync(previous, cancellationToken);
            }

            return Results.NoContent();
        })
        .RequireScope(Scopes.Write);

        MapAvatarDownload(api);
        return api;
    }

    /// <summary>
    /// Anyone's avatar, by user id, as a redirect to a presigned GET.
    ///
    /// A redirect rather than a proxy because bytes never pass through the API, and a
    /// redirect the browser may cache rather than one it may not: the key changes on every
    /// upload, so a cached redirect can only ever be stale about a picture that no longer
    /// exists. The signature outlives the cache window by design, so a redirect served from
    /// cache at the last second still resolves.
    /// </summary>
    private static void MapAvatarDownload(IEndpointRouteBuilder api)
    {
        api.MapGet("/users/{userId}/avatar", async Task<IResult> (
            string userId,
            HttpContext http,
            IdentityDbContext db,
            IBlobStorage storage,
            CancellationToken cancellationToken) =>
        {
            var key = await db.Users.AsNoTracking()
                .Where(u => u.Id == userId)
                .Select(u => u.AvatarKey)
                .FirstOrDefaultAsync(cancellationToken);

            // No avatar and no such person answer the same way: the caller renders
            // initials either way, and a distinct 404 would be an existence oracle for
            // the price of nothing.
            if (key is null)
            {
                return Results.NotFound();
            }

            var url = await storage.PresignDownloadAsync(
                key, fileName: null, Avatar.DownloadTtl, cancellationToken);

            // Private: this is one person's picture behind an authenticated route, so a
            // shared proxy must not serve it to the next caller.
            http.Response.Headers.CacheControl =
                $"private, max-age={(int)Avatar.CacheLifetime.TotalSeconds}";

            return Results.Redirect(url.ToString());
        })
        .WithTags("Profile")
        // The API puts no-store on every backend route by default. This is the one that
        // opts out, in writing, because an <img> on every screen must not re-ask each time.
        .WithMetadata(new CacheableResponseAttribute())
        .RequireAuthorization()
        .RequireScope(Scopes.Read);
    }

    /// <summary>
    /// An agent's account is managed by whoever is answerable for it, through
    /// <c>/orgs/{slug}/agents</c> — not by the agent itself with its own token. Reading
    /// <c>/me</c> stays open, because "who am I" is a question an agent legitimately asks.
    /// </summary>
    private static IResult? AgentRefusal(ApplicationUser user) =>
        user.IsAgent
            ? Results.Problem(
                title: "An agent cannot change its own profile.",
                detail: "Its owner manages it from the organization's agents screen.",
                type: ProblemTypes.InsufficientRole,
                statusCode: StatusCodes.Status403Forbidden)
            : null;

    private static IResult NotFound() =>
        Results.Problem(title: "User not found.", statusCode: StatusCodes.Status404NotFound);

    internal static async Task<ProfileView> ToViewAsync(
        ApplicationUser user,
        UserManager<ApplicationUser> userManager,
        IdentityDbContext db,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var pending = await db.UserSecurityTokens.AsNoTracking()
            .Where(t => t.UserId == user.Id
                && t.Purpose == SecurityTokenPurpose.EmailChange
                && t.UsedAt == null
                && t.ExpiresAt > now)
            .Select(t => t.NewEmail)
            .FirstOrDefaultAsync(cancellationToken);

        return new ProfileView(
            user.Id,
            user.Email!,
            user.FirstName,
            user.LastName,
            user.FullName,
            user.TimeZone,
            user.AvatarKey,
            Avatar.VersionOf(user.AvatarKey),
            user.EmailConfirmed,
            await userManager.HasPasswordAsync(user),
            user.IsAgent,
            [.. await userManager.GetRolesAsync(user)],
            user.CreatedAt,
            pending);
    }
}

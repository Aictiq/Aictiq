using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Aictiq.Modules.Identity.Domain;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Authorization;

namespace Aictiq.Modules.Identity.Endpoints;

/// <param name="UpdatedAt">
/// Null only in the synthesized view for a person who has no row yet — GET reads a
/// missing preference as <c>not_started</c> and never writes one into existence.
/// </param>
public sealed record OnboardingView(
    int TourVersion, string Status, string? LastStepId,
    DateTimeOffset? CompletedAt, DateTimeOffset? UpdatedAt, uint Version);

/// <summary>
/// The tour fields and the concurrency version, nothing else. The status is the one
/// required transition — starting, pausing, skipping and finishing are each just a
/// status with the step the tour stood on.
/// </summary>
public sealed record UpdateOnboardingRequest(string? Status, string? LastStepId, uint Version);

/// <summary>
/// A person's own tour state, on the authenticated <c>/me</c> surface.
///
/// A fresh account has no row; the first PATCH creates it against <c>version: 0</c>, and
/// two tabs racing to do that collide on the primary key — one 409, one reload, one row.
/// Every later write is a compare-and-swap on <c>xmin</c>, so a stale tab cannot regress
/// a completion or dismissal another tab already recorded.
/// </summary>
public static class OnboardingEndpoints
{
    public static IEndpointRouteBuilder MapOnboardingEndpoints(this IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/me").WithTags("Profile").RequireAuthorization();

        group.MapGet("/onboarding", async Task<IResult> (
            ClaimsPrincipal principal,
            IdentityDbContext db,
            CancellationToken cancellationToken) =>
        {
            var userId = principal.FindFirstValue("sub");
            if (userId is null)
            {
                return NotFound();
            }

            var row = await db.UserOnboarding
                .AsNoTracking()
                .SingleOrDefaultAsync(t => t.UserId == userId, cancellationToken);

            return Results.Ok(row is null ? NotStartedView() : ToView(row));
        })
        .RequireScope(Scopes.Read);

        group.MapPatch("/onboarding", async Task<IResult> (
            UpdateOnboardingRequest request,
            ClaimsPrincipal principal,
            IdentityDbContext db,
            UserManager<ApplicationUser> userManager,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var userId = principal.FindFirstValue("sub");
            if (userId is null)
            {
                return NotFound();
            }

            var user = await userManager.FindByIdAsync(userId);
            if (user is null)
            {
                return NotFound();
            }

            if (AgentRefusal(user) is { } refusal)
            {
                return refusal;
            }

            var errors = new Dictionary<string, string[]>();
            if (request.Status is null || !UserOnboarding.TourStatuses.IsKnown(request.Status))
            {
                errors["status"] = [$"Pick from: {string.Join(", ", UserOnboarding.TourStatuses.All)}."];
            }
            if (request.LastStepId is not null && !UserOnboarding.TourSteps.IsKnown(request.LastStepId))
            {
                errors["lastStepId"] = ["Unknown tour step."];
            }
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors, type: ProblemTypes.Validation);
            }

            var row = await db.UserOnboarding
                .SingleOrDefaultAsync(t => t.UserId == userId, cancellationToken);
            // Postgres keeps microseconds; .NET keeps 100-nanosecond ticks. Truncating
            // before the write is what makes the response and the row the same instant —
            // otherwise the first answer quotes a completion date the database rounded away,
            // and "finishing twice keeps the original date" is only true after a reload.
            var now = ToStorablePrecision(timeProvider.GetUtcNow());

            if (row is null)
            {
                if (request.Version != 0)
                {
                    return Conflict();
                }

                row = new UserOnboarding { UserId = userId, Status = request.Status!, LastStepId = request.LastStepId };
                db.UserOnboarding.Add(row);
            }
            else
            {
                if (request.Version != row.Version)
                {
                    return Conflict();
                }

                db.Entry(row).Property(t => t.Version).OriginalValue = request.Version;
                row.Status = request.Status!;
                row.LastStepId = request.LastStepId;
            }

            // Arriving at completed stamps it once — replaying the tour later must not move
            // the original date. Leaving it clears the timestamp, because the check
            // constraint pairs the two and a status the row no longer holds should not
            // keep claiming a completion.
            if (row.Status == UserOnboarding.TourStatuses.Completed)
            {
                row.CompletedAt ??= now;
            }
            else
            {
                row.CompletedAt = null;
            }

            row.UpdatedAt = now;

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                return Conflict();
            }

            return Results.Ok(ToView(row));
        })
        .RequireScope(Scopes.Write);

        return api;
    }

    /// <summary>Microsecond precision — what <c>timestamptz</c> can actually hold.</summary>
    private static DateTimeOffset ToStorablePrecision(DateTimeOffset value) =>
        new(value.Ticks - (value.Ticks % TimeSpan.TicksPerMicrosecond), value.Offset);

    private static OnboardingView NotStartedView() =>
        new(UserOnboarding.CurrentTourVersion, UserOnboarding.TourStatuses.NotStarted,
            null, null, null, 0);

    private static OnboardingView ToView(UserOnboarding row) =>
        new(row.TourVersion, row.Status, row.LastStepId, row.CompletedAt, row.UpdatedAt, row.Version);

    /// <summary>
    /// An agent's account is managed by whoever is answerable for it, through
    /// <c>/orgs/{slug}/agents</c> — a tour is for people. Reading stays open, because a
    /// preference row it cannot change tells an agent nothing it could act on.
    /// </summary>
    private static IResult? AgentRefusal(ApplicationUser user) =>
        user.IsAgent
            ? Results.Problem(
                title: "An agent cannot change its own onboarding.",
                detail: "Tours are for people; its owner manages the account.",
                type: ProblemTypes.InsufficientRole,
                statusCode: StatusCodes.Status403Forbidden)
            : null;

    private static IResult Conflict() => Results.Problem(
        title: "Conflict.",
        detail: "Your onboarding state changed elsewhere — it has been reloaded.",
        type: ProblemTypes.Conflict,
        statusCode: StatusCodes.Status409Conflict);

    private static IResult NotFound() =>
        Results.Problem(title: "User not found.", statusCode: StatusCodes.Status404NotFound);
}

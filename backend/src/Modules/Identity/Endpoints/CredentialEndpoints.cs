using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Microsoft.Extensions.Options;
using Aictiq.Modules.Identity.Auth;
using Aictiq.Modules.Identity.Domain;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Email;

namespace Aictiq.Modules.Identity.Endpoints;

/// <param name="CurrentPassword">
/// Required when the account has one. Omitted when it does not - somebody who only ever
/// signed in with Google is <em>setting</em> a password, and there is nothing to prove
/// beyond the session they are already holding.
/// </param>
public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);

public sealed record ChangeEmailRequest(string? NewEmail, string? CurrentPassword);

public sealed record ForgotPasswordRequest(string? Email);

public sealed record ResetPasswordRequest(string? Token, string? NewPassword);

public sealed record ConfirmEmailChangeRequest(string? Token);

/// <param name="EmailConfigured">
/// Whether this instance can send at all. Not a leak - it is a property of the deployment,
/// not of the address - and without it the screen would promise a mail that will never
/// arrive on a self-hosted instance with no relay.
/// </param>
public sealed record RecoveryAccepted(bool EmailConfigured);

/// <summary>
/// What a confirmed email change reports back. The user id is there because the link is
/// usually opened somewhere other than the session that asked for it - quite possibly a
/// browser signed in as somebody else - and without it a client cannot tell whether the
/// address it is holding is its own.
/// </summary>
public sealed record EmailChanged(string UserId, string Email);

/// <summary>
/// Everything that changes how someone proves who they are: their password and their
/// email address, from inside a session or - when they have lost the password - from a
/// one-time link mailed to the address itself.
///
/// The two mailed flows share one table and one rule (<see cref="UserSecurityToken"/>):
/// only the hash of the link is stored, and it is spent with a single conditional UPDATE.
/// Nothing here reveals whether an address has an account behind it; <c>/auth/forgot</c>
/// answers identically either way, because a "no such user" would turn the reset form into
/// a membership oracle for the whole instance.
/// </summary>
public static class CredentialEndpoints
{
    public static IEndpointRouteBuilder MapCredentialEndpoints(this IEndpointRouteBuilder api)
    {
        MapAccountEndpoints(api);
        MapRecoveryEndpoints(api);
        return api;
    }

    /// <summary>Changing a credential from inside a session.</summary>
    private static void MapAccountEndpoints(IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/me").WithTags("Profile").RequireAuthorization();

        group.MapPost("/password", async Task<IResult> (
            ChangePasswordRequest request,
            ClaimsPrincipal principal,
            UserManager<ApplicationUser> userManager,
            ITokenService tokens,
            CancellationToken cancellationToken) =>
        {
            var user = await AuthEndpoints.FindAsync(principal, userManager);
            if (user is null)
            {
                return NotFound();
            }

            // An agent has no password and must never acquire one - a password is a way
            // in, and "the agent cannot log in" is what makes revoking its token complete.
            if (user.IsAgent)
            {
                return AgentRefusal();
            }

            if (string.IsNullOrEmpty(request.NewPassword))
            {
                return Invalid("newPassword", "Choose a new password.");
            }

            var hasPassword = await userManager.HasPasswordAsync(user);
            if (hasPassword && string.IsNullOrEmpty(request.CurrentPassword))
            {
                return Invalid("currentPassword", "Enter your current password.");
            }

            IdentityResult result;
            if (hasPassword)
            {
                result = await userManager.ChangePasswordAsync(
                    user, request.CurrentPassword!, request.NewPassword);

                // Identity reports a wrong current password as a validation failure with
                // no field. Say which field, because the form has two.
                if (!result.Succeeded && result.Errors.Any(e => e.Code == "PasswordMismatch"))
                {
                    return Invalid("currentPassword", "That is not your current password.");
                }
            }
            else
            {
                result = await userManager.AddPasswordAsync(user, request.NewPassword);
            }

            if (!result.Succeeded)
            {
                return Results.ValidationProblem(
                    Rekey(AuthEndpoints.ToFieldErrors(result), "password", "newPassword"),
                    type: ProblemTypes.Validation);
            }

            // Everywhere else, not everywhere: the browser that just changed the password
            // stays signed in, and every other device has to prove itself again. Changing
            // a password is the closest thing the product has to "I think someone else
            // has my account".
            await tokens.RevokeAllForUserExceptAsync(
                user.Id, SessionEndpoints.CurrentFamily(principal), cancellationToken);

            return Results.NoContent();
        })
        // Admin, not write, and for the same reason creating a personal access token
        // needs it: this endpoint *mints a credential*. On an account with no password
        // there is nothing to prove but the caller's own token, so a narrowed `write`
        // token could otherwise give itself a password and become a full login.
        .RequireScope(Scopes.Admin);

        group.MapPost("/email", async Task<IResult> (
            ChangeEmailRequest request,
            HttpContext http,
            ClaimsPrincipal principal,
            UserManager<ApplicationUser> userManager,
            IdentityDbContext db,
            IEmailCapabilities email,
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

            var address = request.NewEmail?.Trim().ToLowerInvariant() ?? "";
            if (address.Length == 0 || !new EmailAddressAttribute().IsValid(address))
            {
                return Invalid("newEmail", "Enter a valid email address.");
            }

            if (string.Equals(address, user.Email, StringComparison.OrdinalIgnoreCase))
            {
                return Invalid("newEmail", "That is already your address.");
            }

            // The session alone is not enough to move where the recovery mail goes: an
            // unattended browser would otherwise be a way to take the account permanently.
            if (await userManager.HasPasswordAsync(user))
            {
                if (string.IsNullOrEmpty(request.CurrentPassword))
                {
                    return Invalid("currentPassword", "Enter your password to change your address.");
                }

                if (!await userManager.CheckPasswordAsync(user, request.CurrentPassword))
                {
                    return Invalid("currentPassword", "That is not your password.");
                }
            }

            if (await userManager.FindByEmailAsync(address) is not null)
            {
                // Said plainly rather than hidden: this is an authenticated, rate-limited
                // request, and registration already refuses a taken address in the same
                // words. Pretending otherwise would only send a confirmation link that
                // could never work.
                return Invalid("newEmail", "An account already uses that address.");
            }

            var now = timeProvider.GetUtcNow();
            var token = await IssueAsync(
                db, user.Id, SecurityTokenPurpose.EmailChange, now, cancellationToken, address);

            token.Entity.RequestEmail(address, UserSecurityToken.EmailChangeTemplate, new Dictionary<string, string>
            {
                ["recipientName"] = user.FirstName,
                ["newEmail"] = address,
                ["currentEmail"] = user.Email ?? "",
                ["confirmUrl"] = Link(http, emailOptions.Value, "confirm-email", token.Secret),
                ["expiresOn"] = Expiry(token.Entity.ExpiresAt),
            });

            if (!await CommitAsync(db, cancellationToken))
            {
                return Results.Problem(
                    title: "A confirmation is already on its way.",
                    detail: "Check that address, or cancel the pending change and ask again.",
                    type: ProblemTypes.Conflict,
                    statusCode: StatusCodes.Status409Conflict);
            }

            return Results.Accepted(value: new RecoveryAccepted(email.IsConfigured));
        })
        // Admin for the same reason as the password: this moves where every future reset
        // link is delivered, which on a provider-only account is the whole credential.
        .RequireScope(Scopes.Admin);

        group.MapDelete("/email", async Task<IResult> (
            ClaimsPrincipal principal,
            IdentityDbContext db,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var userId = principal.FindFirstValue("sub")!;
            var now = timeProvider.GetUtcNow();

            // Spent rather than deleted: the row is the record that an address was asked
            // for, and the partial unique index only cares that it is no longer live.
            await db.UserSecurityTokens
                .Where(t => t.UserId == userId
                    && t.Purpose == SecurityTokenPurpose.EmailChange
                    && t.UsedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.UsedAt, now), cancellationToken);

            return Results.NoContent();
        })
        .RequireScope(Scopes.Write);
    }

    /// <summary>The anonymous half: getting back in, and confirming a new address.</summary>
    private static void MapRecoveryEndpoints(IEndpointRouteBuilder api)
    {
        // Same limiter as sign-in: these are the endpoints someone would grind through an
        // address list with, and they are reachable without a credential.
        var group = api.MapGroup("/auth").WithTags("Auth").RequireRateLimiting("auth");

        group.MapPost("/forgot", async Task<IResult> (
            ForgotPasswordRequest request,
            HttpContext http,
            UserManager<ApplicationUser> userManager,
            IdentityDbContext db,
            IEmailCapabilities email,
            IOptions<EmailOptions> emailOptions,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var accepted = Results.Accepted(value: new RecoveryAccepted(email.IsConfigured));

            var address = request.Email?.Trim() ?? "";
            if (address.Length == 0)
            {
                return Invalid("email", "Enter your email address.");
            }

            var user = await userManager.FindByEmailAsync(address);

            // Every refusal below returns the same 202 as a success. A distinct answer for
            // "no such account" would make this form a way to ask, one address at a time,
            // who is on this instance - and an agent has no mailbox to send to anyway.
            if (user is null || !user.IsActive || user.IsAgent)
            {
                return accepted;
            }

            var now = timeProvider.GetUtcNow();
            var token = await IssueAsync(
                db, user.Id, SecurityTokenPurpose.PasswordReset, now, cancellationToken);

            token.Entity.RequestEmail(user.Email!, UserSecurityToken.PasswordResetTemplate,
                new Dictionary<string, string>
                {
                    ["recipientName"] = user.FirstName,
                    ["resetUrl"] = Link(http, emailOptions.Value, "reset-password", token.Secret),
                    ["expiresOn"] = Expiry(token.Entity.ExpiresAt),
                });

            // Whether this minted a link or lost a race with a request that already had,
            // the answer is the one every other path returns.
            await CommitAsync(db, cancellationToken);
            return accepted;
        });

        group.MapPost("/reset", async Task<IResult> (
            ResetPasswordRequest request,
            UserManager<ApplicationUser> userManager,
            IdentityDbContext db,
            ITokenService tokens,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrEmpty(request.NewPassword))
            {
                return Invalid("newPassword", "Choose a new password.");
            }

            var now = timeProvider.GetUtcNow();
            var token = await FindAsync(db, request.Token, SecurityTokenPurpose.PasswordReset, cancellationToken);
            var user = token is null ? null : await userManager.FindByIdAsync(token.UserId);
            if (token is null || user is null || !user.IsActive || user.IsAgent)
            {
                return LinkNoLongerValid();
            }

            // Validated before the token is spent. The other order would burn a one-time
            // link on a password the rules were always going to reject, and leave the
            // person with no way back in but to ask for another mail.
            foreach (var validator in userManager.PasswordValidators)
            {
                var validation = await validator.ValidateAsync(userManager, user, request.NewPassword);
                if (!validation.Succeeded)
                {
                    return Results.ValidationProblem(
                        Rekey(AuthEndpoints.ToFieldErrors(validation), "password", "newPassword"),
                        type: ProblemTypes.Validation);
                }
            }

            if (!await ConsumeAsync(db, token, now, cancellationToken))
            {
                return LinkNoLongerValid();
            }

            // Set in one write rather than remove-then-add: the two-step version has a
            // window in which the account has no password at all, and a failure inside it
            // would lock somebody out of their own account permanently.
            user.PasswordHash = userManager.PasswordHasher.HashPassword(user, request.NewPassword);
            // Rotating the stamp is what invalidates anything else Identity ever issued
            // for this account, and it saves the new hash in the same update.
            var written = await userManager.UpdateSecurityStampAsync(user);
            if (!written.Succeeded)
            {
                // The link is already spent, so a silent 204 here would report success on
                // a password that was never stored and leave no way back in but a new mail.
                return Results.Problem(
                    title: "The new password could not be saved.",
                    detail: "Nothing was changed. Ask for a new link and try again.",
                    statusCode: StatusCodes.Status500InternalServerError);
            }

            // Someone who forgot their password has usually just failed to guess it five
            // times. Leaving the lockout in place would make the reset appear not to work.
            await userManager.ResetAccessFailedCountAsync(user);
            await userManager.SetLockoutEndDateAsync(user, null);

            // Everywhere, this time: the premise of a reset is that the old password may
            // be in somebody else's hands, and so may the sessions it opened.
            await tokens.RevokeAllForUserExceptAsync(user.Id, null, cancellationToken);

            return Results.NoContent();
        });

        group.MapPost("/email-change", async Task<IResult> (
            ConfirmEmailChangeRequest request,
            UserManager<ApplicationUser> userManager,
            IdentityDbContext db,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var now = timeProvider.GetUtcNow();
            var token = await FindAsync(db, request.Token, SecurityTokenPurpose.EmailChange, cancellationToken);
            var user = token is null ? null : await userManager.FindByIdAsync(token.UserId);
            if (token?.NewEmail is null || user is null || !user.IsActive)
            {
                return LinkNoLongerValid();
            }

            if (!await ConsumeAsync(db, token, now, cancellationToken))
            {
                return LinkNoLongerValid();
            }

            // Every field in one update, not SetEmailAsync followed by SetUserNameAsync:
            // the first of those saves on its own, so a failure in the second would commit
            // half the change and then report a conflict about it. The token is already
            // spent by this point, so there is no second attempt to fall back on.
            var previous = user.Email;
            user.Email = token.NewEmail;
            user.NormalizedEmail = userManager.NormalizeEmail(token.NewEmail);
            if (string.Equals(user.UserName, previous, StringComparison.OrdinalIgnoreCase))
            {
                // Registration puts the address in both columns; an account created by a
                // provider may not have. Only the one that was the address moves.
                user.UserName = token.NewEmail;
                user.NormalizedUserName = userManager.NormalizeName(token.NewEmail);
            }

            // The link proved control of the mailbox, which is the whole question
            // confirmation exists to answer - so it arrives confirmed.
            user.EmailConfirmed = true;

            var result = await userManager.UpdateAsync(user);
            if (!result.Succeeded)
            {
                // The address was taken between asking and confirming. A conflict rather
                // than a validation error: nothing about the request was wrong when it
                // was made, and there is nothing to correct except to pick another.
                // ux_users_normalized_email catches the same race a moment later, and
                // GlobalExceptionHandler maps that to this status too.
                return Results.Problem(
                    title: "That address is no longer available.",
                    detail: "An account claimed it while this link was waiting. Ask for the change again.",
                    type: ProblemTypes.Conflict,
                    statusCode: StatusCodes.Status409Conflict);
            }

            return Results.Ok(new EmailChanged(user.Id, user.Email!));
        });
    }

    /// <summary>
    /// Mints a link, retiring whatever was outstanding for the same purpose.
    ///
    /// Two statements rather than one save, and in that order: the partial unique index
    /// refuses a moment in which two of a person's links are live, and EF does not promise
    /// the order of an UPDATE and an INSERT inside a batch. The row is added but not
    /// saved - the caller raises the email event on it first, so that the token and the
    /// message asking someone to click it commit together.
    /// </summary>
    private static async Task<(UserSecurityToken Entity, string Secret)> IssueAsync(
        IdentityDbContext db, string userId, SecurityTokenPurpose purpose, DateTimeOffset now,
        CancellationToken cancellationToken, string? newEmail = null)
    {
        await db.UserSecurityTokens
            .Where(t => t.UserId == userId && t.Purpose == purpose && t.UsedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.UsedAt, now), cancellationToken);

        var entity = UserSecurityToken.Create(userId, purpose, now, out var secret, newEmail);
        db.UserSecurityTokens.Add(entity);
        return (entity, secret);
    }

    /// <summary>
    /// Commits the mint and the mail together, under one transaction so a failure cannot
    /// leave someone's previous link retired with no replacement.
    ///
    /// The <c>ExecuteUpdateAsync</c> above runs on its own, so two simultaneous requests
    /// for the same address can both retire nothing and both insert; the loser breaks
    /// <c>ux_user_security_tokens_live</c>. Returning true either way is deliberate - a
    /// live link already exists, which is exactly what the caller asked for, and a 409
    /// here would be a difference between an address with an account and one without.
    /// </summary>
    private static async Task<bool> CommitAsync(IdentityDbContext db, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation
        })
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }
    }

    private static async Task<UserSecurityToken?> FindAsync(
        IdentityDbContext db, string? token, SecurityTokenPurpose purpose, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var hash = UserSecurityToken.Hash(token);
        return await db.UserSecurityTokens.AsNoTracking()
            .FirstOrDefaultAsync(t => t.TokenHash == hash && t.Purpose == purpose, cancellationToken);
    }

    /// <summary>
    /// Spends the link. Compare-and-swap, never read-check-write: two clicks on one mail
    /// arrive in parallel often enough to matter, and exactly one of them may win.
    /// </summary>
    private static async Task<bool> ConsumeAsync(
        IdentityDbContext db, UserSecurityToken token, DateTimeOffset now, CancellationToken cancellationToken) =>
        await db.UserSecurityTokens
            .Where(t => t.Id == token.Id && t.UsedAt == null && t.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.UsedAt, now), cancellationToken) == 1;

    /// <summary>
    /// Where the link in the mail points. <c>Email:BaseUrl</c> wins when it is set - the
    /// recipient's browser may not be able to reach the address the API was called on -
    /// with the request's own origin as the fallback.
    /// </summary>
    private static string Link(HttpContext http, EmailOptions options, string route, string token)
    {
        var origin = string.IsNullOrWhiteSpace(options.BaseUrl)
            ? $"{http.Request.Scheme}://{http.Request.Host}"
            : options.BaseUrl.TrimEnd('/');

        return $"{origin}/{route}/{token}";
    }

    private static string Expiry(DateTimeOffset at) =>
        at.UtcDateTime.ToString("d MMMM yyyy HH:mm 'UTC'", CultureInfo.InvariantCulture);

    /// <summary>
    /// Expired, already spent, revoked, or never real - all one answer. Which of them it
    /// was is not something the holder of a bad link is entitled to learn, and the remedy
    /// is the same in every case.
    /// </summary>
    private static IResult LinkNoLongerValid() =>
        Results.ValidationProblem(
            new Dictionary<string, string[]>
            {
                ["token"] = ["This link is no longer valid. Ask for a new one."]
            },
            type: ProblemTypes.Validation);

    private static IResult Invalid(string field, string message) =>
        Results.ValidationProblem(
            new Dictionary<string, string[]> { [field] = [message] }, type: ProblemTypes.Validation);

    private static IResult AgentRefusal() =>
        Results.Problem(
            title: "An agent has no password.",
            detail: "Its only credential is a personal access token, issued by its owner.",
            type: ProblemTypes.InsufficientRole,
            statusCode: StatusCodes.Status403Forbidden);

    private static IResult NotFound() =>
        Results.Problem(title: "User not found.", statusCode: StatusCodes.Status404NotFound);

    /// <summary>Identity keys password failures under "password"; these forms call it something else.</summary>
    private static Dictionary<string, string[]> Rekey(
        Dictionary<string, string[]> errors, string from, string to)
    {
        if (errors.Remove(from, out var messages))
        {
            errors[to] = messages;
        }

        return errors;
    }
}

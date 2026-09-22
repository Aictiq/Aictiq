using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Options;
using Npgsql;
using Aictiq.Modules.Tenancy.Access;
using Aictiq.Modules.Tenancy.Domain;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Email;
using Aictiq.SharedKernel.Tenancy;

namespace Aictiq.Modules.Tenancy.Endpoints;

/// <summary>One row of the pending list. No token - that is shown once, when it is minted.</summary>
/// <param name="ProjectKey">The project the invitation also joins, when it names one. Null once that project is gone.</param>
/// <param name="CanOperateFactory">What the membership will say about operating the AI factory.</param>
public sealed record InvitationView(
    Guid Id, string Email, OrgRole Role, Guid? ProjectId, ProjectRole? ProjectRole,
    string InvitedByName, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, InvitationStatus Status,
    string? ProjectKey, bool CanOperateFactory);

/// <param name="Token">
/// The plaintext link, returned exactly once - on creation and on resend. Aictiq stores
/// only its hash and cannot produce it again.
/// </param>
/// <param name="EmailSent">
/// False on an instance with no SMTP relay. Not an error: the deployment is supported, and
/// the caller shares <paramref name="AcceptUrl"/> themselves instead.
/// </param>
public sealed record InvitationLink(
    InvitationView Invitation, string Token, string AcceptUrl, bool EmailSent);

/// <summary>
/// What an anonymous visitor holding a link is told. Enough to decide whether to sign in;
/// not enough to be worth harvesting - the address is masked, because whoever opened the
/// link is not necessarily the person it was sent to.
/// </summary>
public sealed record InvitationPreview(
    string OrganizationName, string OrganizationSlug, string InvitedByName,
    string MaskedEmail, OrgRole Role, DateTimeOffset ExpiresAt, InvitationStatus Status);

/// <summary>Where the client should go next, and what it may do when it gets there.</summary>
/// <param name="ProjectKey">Where a project invitation should land the new member. Null otherwise.</param>
public sealed record AcceptedInvitation(
    Guid OrganizationId, string OrganizationSlug, string OrganizationName, OrgRole Role, bool AlreadyMember,
    string? ProjectKey = null);

/// <param name="ProjectId">Optional. Also adds them to this project, at <paramref name="ProjectRole"/>.</param>
/// <param name="CanOperateFactory">
/// Null takes the role's default: members and admins operate, guests never do. A stakeholder
/// is a member invited with <c>false</c>.
/// </param>
public sealed record CreateInvitationRequest(
    string? Email, OrgRole? Role, Guid? ProjectId, ProjectRole? ProjectRole, bool? CanOperateFactory = null);

/// <summary>
/// Invitations: the only way into an organization other than creating one.
///
/// Two halves with very different exposure. The management half lives under
/// <c>/orgs/{orgSlug}</c>, so the tenant middleware has already established the
/// organization and the caller's membership before anything here runs. The redemption
/// half - preview and accept - is reached by <b>token</b>, with no organization in the
/// URL and, for the preview, no principal at all: the token is what decides the tenant,
/// which is why those two are among the handful of deliberate
/// <c>IgnoreQueryFilters</c> calls in the codebase. The hash predicate, not the filter,
/// is what keeps them safe.
/// </summary>
public static class InvitationEndpoints
{
    public static IEndpointRouteBuilder MapInvitationEndpoints(this IEndpointRouteBuilder api)
    {
        MapManagement(api);
        MapRedemption(api);
        return api;
    }

    // ------------------------------------------------------------------------ management

    private static void MapManagement(IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/orgs/{orgSlug}/invitations")
            .WithTags("Invitations")
            .RequireAuthorization();

        group.MapGet("/", async (
            TenancyDbContext db,
            IUserDirectory directory,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            // Open ones only: accepted and revoked invitations are history, and the list
            // exists to answer "who have we asked and not heard from". Expired rows stay
            // - they are exactly the ones worth resending, and a link that silently
            // vanished on its seventh day looks like a bug from the inviter's side.
            var invitations = await db.Invitations
                .AsNoTracking()
                .Where(i => i.AcceptedAt == null && i.RevokedAt == null)
                .OrderByDescending(i => i.CreatedAt)
                .ToListAsync(cancellationToken);

            var inviters = await directory.GetAsync(
                [.. invitations.Select(i => i.InvitedBy).Distinct()], cancellationToken);
            var projectKeys = await ProjectKeysAsync(db, invitations, cancellationToken);

            var now = timeProvider.GetUtcNow();
            return Results.Ok(invitations.Select(i => ToView(i, inviters, projectKeys, now)).ToList());
        })
        .RequireOrgRole(OrgRole.Admin)
        .RequireScope(Scopes.Read);

        group.MapPost("/", async (
            CreateInvitationRequest request,
            HttpContext http,
            TenancyDbContext db,
            ICurrentTenant tenant,
            ICurrentUser user,
            IUserDirectory directory,
            IOrganizationLookup organizations,
            IEmailCapabilities email,
            IPlanLimits planLimits,
            IOptions<EmailOptions> emailOptions,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var organizationId = tenant.OrganizationId!.Value;

            var errors = new Dictionary<string, string[]>();
            var address = Validate(request, errors);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors, type: ProblemTypes.Validation);
            }

            var actorRole = await db.Members
                .AsNoTracking()
                .Where(m => m.UserId == user.UserId)
                .Select(m => (OrgRole?)m.Role)
                .FirstOrDefaultAsync(cancellationToken);

            if (actorRole is null)
            {
                return TenancyResults.NotFound();
            }

            if (!MembershipRules.CanInvite(actorRole.Value, request.Role!.Value))
            {
                return TenancyResults.Forbidden(request.Role.Value == OrgRole.Owner
                    ? "Ownership is handed to someone already here, not mailed to an address."
                    : "Admins can invite members and guests; only an owner can invite an admin.");
            }

            // Someone who is already here does not need an invitation, and sending one
            // would make an admin wait a week to learn nothing happened.
            var existing = await directory.FindByEmailAsync(address, cancellationToken);
            if (existing is not null
                && await db.Members.AnyAsync(m => m.UserId == existing.Id, cancellationToken))
            {
                return Conflict($"{existing.DisplayName} is already a member of this organization.");
            }

            if (await db.Invitations.AnyAsync(
                    i => i.Email == address && i.AcceptedAt == null && i.RevokedAt == null, cancellationToken))
            {
                return Conflict(
                    "There is already an open invitation for that address - resend or revoke it instead.");
            }

            // The id in the body is a request, not a grant: it has to be a project of this
            // organization, which the tenant filter on Projects is what establishes.
            string? projectKey = null;
            if (request.ProjectId is { } projectId)
            {
                var project = await db.Projects.AsNoTracking()
                    .Where(p => p.Id == projectId)
                    .Select(p => new { p.Key, Archived = p.ArchivedAt != null })
                    .FirstOrDefaultAsync(cancellationToken);
                if (project is null || project.Archived)
                {
                    return Results.ValidationProblem(
                        new Dictionary<string, string[]>
                        {
                            ["projectId"] = [project is null ? "No such project." : "That project is archived."],
                        },
                        type: ProblemTypes.Validation);
                }

                projectKey = project.Key;
            }

            var seat = await planLimits.CanAddHumanSeatAsync(organizationId, 1, cancellationToken);
            if (!seat.Allowed)
            {
                return PlanLimited(seat);
            }

            var now = timeProvider.GetUtcNow();
            var invitation = Invitation.Create(
                organizationId, address, request.Role.Value, request.ProjectId, request.ProjectRole,
                request.CanOperateFactory ?? request.Role.Value != OrgRole.Guest,
                user.UserId!, now, out var token);

            var organization = await organizations.FindByIdAsync(organizationId, cancellationToken);
            var inviter = await InviterNameAsync(directory, user, cancellationToken);
            var acceptUrl = AcceptUrl(http, emailOptions.Value, token);

            invitation.RequestEmail(EmailVariables(
                invitation, organization?.Name ?? "", inviter, existing?.DisplayName, acceptUrl));

            db.Invitations.Add(invitation);
            // The invitation row and the SendEmailRequested outbox row commit together:
            // an email about an invitation that does not exist is worse than no email.
            await db.SaveChangesAsync(cancellationToken);

            var inviters = new Dictionary<string, UserSummary>
            {
                [user.UserId!] = new(user.UserId!, inviter, null, user.IsAgent),
            };

            return Results.Created(
                $"/api/v1/orgs/{http.GetRouteValue("orgSlug")}/invitations/{invitation.Id}",
                new InvitationLink(
                    ToView(invitation, inviters, ProjectKeyMap(invitation, projectKey), now),
                    token, acceptUrl, email.IsConfigured));
        })
        .RequireOrgRole(OrgRole.Admin)
        .RequireScope(Scopes.Write);

        group.MapPost("/{invitationId:guid}/resend", async (
            Guid invitationId,
            HttpContext http,
            TenancyDbContext db,
            ICurrentTenant tenant,
            ICurrentUser user,
            IUserDirectory directory,
            IOrganizationLookup organizations,
            IEmailCapabilities email,
            IOptions<EmailOptions> emailOptions,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var invitation = await db.Invitations
                .FirstOrDefaultAsync(i => i.Id == invitationId, cancellationToken);

            if (invitation is null)
            {
                return TenancyResults.NotFound();
            }

            var now = timeProvider.GetUtcNow();
            var status = invitation.StatusAt(now);
            if (status is InvitationStatus.Accepted or InvitationStatus.Revoked)
            {
                return Conflict($"That invitation was already {status.ToString().ToLowerInvariant()}.");
            }

            // A fresh token, necessarily: only the hash of the old one was ever stored, so
            // "send them the same link again" is not something Aictiq can do. Retiring the
            // previous link is the right side of that trade anyway - the usual reason to
            // resend is that it went somewhere it should not have.
            invitation.Reissue(now, out var token);

            var organization = await organizations.FindByIdAsync(invitation.OrganizationId, cancellationToken);
            var inviter = await InviterNameAsync(directory, user, cancellationToken);
            var recipient = await directory.FindByEmailAsync(invitation.Email, cancellationToken);
            var acceptUrl = AcceptUrl(http, emailOptions.Value, token);

            invitation.RequestEmail(EmailVariables(
                invitation, organization?.Name ?? "", inviter, recipient?.DisplayName, acceptUrl));

            await db.SaveChangesAsync(cancellationToken);

            var inviters = await directory.GetAsync([invitation.InvitedBy], cancellationToken);
            var projectKeys = await ProjectKeysAsync(db, [invitation], cancellationToken);
            return Results.Ok(new InvitationLink(
                ToView(invitation, inviters, projectKeys, now), token, acceptUrl, email.IsConfigured));
        })
        .RequireOrgRole(OrgRole.Admin)
        .RequireScope(Scopes.Write);

        group.MapDelete("/{invitationId:guid}", async (
            Guid invitationId,
            TenancyDbContext db,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var invitation = await db.Invitations
                .FirstOrDefaultAsync(i => i.Id == invitationId, cancellationToken);

            if (invitation is null)
            {
                return TenancyResults.NotFound();
            }

            if (invitation.AcceptedAt is not null)
            {
                // Revoking an accepted invitation would look like it undid the join. It
                // does not: removing the person is what the members list is for.
                return Conflict("That invitation was already accepted - remove the member instead.");
            }

            // Revoked, not deleted: the row is the record that someone was asked, and the
            // partial unique index treats it as closed, so the address can be invited again.
            invitation.Revoke(timeProvider.GetUtcNow());
            await db.SaveChangesAsync(cancellationToken);

            return Results.NoContent();
        })
        .RequireOrgRole(OrgRole.Admin)
        .RequireScope(Scopes.Write);
    }

    // ------------------------------------------------------------------------ redemption

    private static void MapRedemption(IEndpointRouteBuilder api)
    {
        // No {orgSlug}: the token names the organization. The rate limiter is the auth
        // policy on purpose - this is an unauthenticated endpoint that says whether a
        // 256-bit secret exists, so it is throttled like a login attempt.
        var group = api.MapGroup("/invitations")
            .WithTags("Invitations")
            .RequireRateLimiting("auth");

        group.MapGet("/{token}", async (
            string token,
            TenancyDbContext db,
            AmbientCurrentTenant tenant,
            IOrganizationLookup organizations,
            IUserDirectory directory,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var invitation = await FindByTokenAsync(db, tenant, token, cancellationToken);
            if (invitation is null)
            {
                return TenancyResults.NotFound();
            }

            var organization = await organizations.FindByIdAsync(invitation.OrganizationId, cancellationToken);
            if (organization is null)
            {
                // The organization was deleted after the invitation went out. From the
                // visitor's side that is indistinguishable from a link that never existed,
                // and it should be.
                return TenancyResults.NotFound();
            }

            var inviters = await directory.GetAsync([invitation.InvitedBy], cancellationToken);

            return Results.Ok(new InvitationPreview(
                organization.Name,
                organization.Slug,
                inviters.TryGetValue(invitation.InvitedBy, out var inviter) ? inviter.DisplayName : "A colleague",
                MaskEmail(invitation.Email),
                invitation.Role,
                invitation.ExpiresAt,
                invitation.StatusAt(timeProvider.GetUtcNow())));
        })
        .AllowAnonymous();

        group.MapPost("/{token}/accept", async (
            string token,
            TenancyDbContext db,
            AmbientCurrentTenant tenant,
            ICurrentUser user,
            IOrganizationLookup organizations,
            IPlanLimits planLimits,
            HybridCache cache,
            NpgsqlDataSource dataSource,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var invitation = await FindByTokenAsync(db, tenant, token, cancellationToken, track: true);
            if (invitation is null)
            {
                return TenancyResults.NotFound();
            }

            var organization = await organizations.FindByIdAsync(invitation.OrganizationId, cancellationToken);
            if (organization is null)
            {
                return TenancyResults.NotFound();
            }

            var now = timeProvider.GetUtcNow();
            var seat = await planLimits.CanAddHumanSeatAsync(invitation.OrganizationId, 1, cancellationToken);
            if (!seat.Allowed)
            {
                return PlanLimited(seat);
            }

            // The token decided the tenant; from here everything is scoped to it, which is
            // also what lets the membership row be written at all.
            using (tenant.Use(invitation.OrganizationId))
            {
                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

                // Compare-and-swap, never read-then-write: two clicks on the same link
                // arrive as two transactions, and a version that checked first would let
                // both through and add the member twice - or, once projects land, grant
                // two different roles. One conditional UPDATE, and the loser sees zero rows.
                var claimed = await db.Invitations
                    .Where(i => i.Id == invitation.Id
                        && i.AcceptedAt == null
                        && i.RevokedAt == null
                        && i.ExpiresAt > now)
                    .ExecuteUpdateAsync(
                        set => set
                            .SetProperty(i => i.AcceptedAt, now)
                            .SetProperty(i => i.AcceptedBy, user.UserId),
                        cancellationToken);

                if (claimed == 0)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return Conflict(invitation.StatusAt(now) switch
                    {
                        InvitationStatus.Accepted => "That invitation has already been accepted.",
                        InvitationStatus.Revoked => "That invitation was revoked.",
                        _ => "That invitation has expired - ask for a new one.",
                    });
                }

                // Already a member: accept the invitation and leave their role alone. An
                // invitation is an offer to join, not an instruction to re-grade someone
                // who is already here - a Member holding an old "guest" link must not be
                // demoted by clicking it.
                var member = await db.Members
                    .FirstOrDefaultAsync(m => m.UserId == user.UserId, cancellationToken);

                var alreadyMember = member is not null;
                if (member is null)
                {
                    // The factory flag travels with the invitation, so a stakeholder is never,
                    // even for a moment, a member who could start AI work.
                    db.Members.Add(OrganizationMember.Create(
                        invitation.OrganizationId, user.UserId!, invitation.Role, now, invitation.CanOperateFactory));
                }

                // A project invitation also puts them on the project, in the same transaction
                // as the membership: a stakeholder of a private project who joined the
                // organization but not the project would see nothing and be told nothing.
                // Additive only, like the organization role above: an explicit membership they
                // already hold is left as it is. A project deleted since the invitation went
                // out simply is not joined.
                Project? project = null;
                if (invitation.ProjectId is { } projectId && invitation.ProjectRole is { } projectRole)
                {
                    project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
                    if (project is not null
                        && !await db.ProjectMembers.AnyAsync(
                            m => m.ProjectId == projectId && m.UserId == user.UserId, cancellationToken))
                    {
                        // Someone already here as a Guest (a forwarded link, another address)
                        // stays a Guest on the project too. ProjectAccessRules caps it on every
                        // read regardless, but the row should not say what the product refuses.
                        var organizationRole = member?.Role ?? invitation.Role;
                        db.ProjectMembers.Add(ProjectMember.Create(
                            invitation.OrganizationId, projectId, user.UserId!,
                            organizationRole == OrgRole.Guest ? ProjectRole.Guest : projectRole,
                            invitation.InvitedBy, now));
                    }
                }

                invitation.MarkAccepted(user.UserId!, now);

                // Membership row and InvitationAccepted outbox row, one transaction.
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);

                // Their role is cached by every instance's tenant middleware; until this
                // lands they are a member the API does not believe in.
                await TenancyCache.InvalidateAsync(
                    cache, dataSource, invitation.OrganizationId, organization.Slug, cancellationToken);
                if (project is not null)
                {
                    await TenancyCache.InvalidateProjectAsync(
                        cache, dataSource, invitation.OrganizationId, organization.Slug,
                        project.Id, project.Key, cancellationToken);
                }

                return Results.Ok(new AcceptedInvitation(
                    organization.Id, organization.Slug, organization.Name,
                    member?.Role ?? invitation.Role, alreadyMember, project?.Key));
            }
        })
        .RequireAuthorization()
        .RequireScope(Scopes.Write);
    }

    // ---------------------------------------------------------------------------- shared

    /// <summary>
    /// Looks a token up across every tenant, because at this point there is no tenant -
    /// resolving one is what the token is for. One of the sanctioned
    /// <c>IgnoreQueryFilters</c> uses; the unique hash predicate is the guard.
    /// </summary>
    private static async Task<Invitation?> FindByTokenAsync(
        TenancyDbContext db, AmbientCurrentTenant tenant, string token, CancellationToken cancellationToken, bool track = false)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 128)
        {
            return null;
        }

        var hash = Invitation.HashToken(token);
        // The token hash is a narrowly scoped database capability. The RLS policy only
        // permits this lookup while its exact SHA-256 value is in the session context;
        // it is cleared before any unrelated command can run.
        using var capability = tenant.UseInvitationTokenHash(Convert.ToHexString(hash));
        var query = db.Invitations.IgnoreQueryFilters();
        return await (track ? query : query.AsNoTracking())
            .FirstOrDefaultAsync(i => i.TokenHash == hash, cancellationToken);
    }

    /// <summary>Returns the normalized address; only meaningful once <paramref name="errors"/> is empty.</summary>
    private static string Validate(CreateInvitationRequest request, Dictionary<string, string[]> errors)
    {
        var address = Invitation.Normalize(request.Email ?? "");
        if (address.Length == 0)
        {
            errors["email"] = ["An email address is required."];
        }
        else if (address.Length > 320 || !IsPlausibleAddress(address))
        {
            errors["email"] = ["That does not look like an email address."];
        }

        if (request.Role is not { } role || !Enum.IsDefined(role))
        {
            errors["role"] = ["Pick admin, member or guest."];
        }
        else if (role == OrgRole.Owner)
        {
            errors["role"] = ["Owner cannot be invited - hand it over from the members list instead."];
        }

        // A project and a role on it come together or not at all; that the project exists in
        // this organization is checked by the caller, which has the database.
        if ((request.ProjectId is null) != (request.ProjectRole is null))
        {
            errors[request.ProjectId is null ? "projectId" : "projectRole"] =
                ["Name a project and a role on it together."];
        }
        else if (request.ProjectRole is { } projectRole)
        {
            if (!Enum.IsDefined(projectRole))
            {
                errors["projectRole"] = ["Pick admin, member or guest."];
            }
            else if (request.Role == OrgRole.Guest && projectRole != ProjectRole.Guest)
            {
                // ProjectAccessRules caps an organization Guest at project Guest anyway; an
                // invitation that promised more would be a promise the product does not keep.
                errors["projectRole"] = ["A guest of the organization is a guest on every project."];
            }
        }

        // The role decides the flag for everyone but a member, so asking for the
        // opposite is refused rather than quietly overridden.
        if (request.CanOperateFactory is { } operate && request.Role is { } invited && Enum.IsDefined(invited))
        {
            if (invited == OrgRole.Guest && operate)
            {
                errors["canOperateFactory"] = ["A guest never operates the factory."];
            }
            else if (invited == OrgRole.Admin && !operate)
            {
                errors["canOperateFactory"] = ["Admins always operate the factory."];
            }
        }

        return address;
    }

    /// <summary>
    /// Deliberately not a full RFC 5322 parser: the address is going to be posted to a
    /// relay, and the relay is the only thing that can really say. This catches the typo.
    /// </summary>
    private static bool IsPlausibleAddress(string address)
    {
        var at = address.IndexOf('@', StringComparison.Ordinal);
        return at > 0
            && at == address.LastIndexOf('@')
            && at < address.Length - 1
            && address.IndexOf('.', at) > at + 1
            && !address.Contains(' ', StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>a****@example.com</c>. Whoever opened the link is not necessarily who it was
    /// sent to, so the preview shows enough to recognise your own address and not enough
    /// to learn someone else's.
    /// </summary>
    private static string MaskEmail(string email)
    {
        var at = email.IndexOf('@', StringComparison.Ordinal);
        if (at <= 0)
        {
            return "•••";
        }

        var local = email[..at];
        var masked = local.Length <= 2 ? local[..1] + "•" : local[0] + new string('•', Math.Min(local.Length - 1, 6));
        return masked + email[at..];
    }

    private static async Task<string> InviterNameAsync(
        IUserDirectory directory, ICurrentUser user, CancellationToken cancellationToken)
    {
        var summaries = await directory.GetAsync([user.UserId!], cancellationToken);
        return summaries.TryGetValue(user.UserId!, out var summary) ? summary.DisplayName : "A colleague";
    }

    /// <summary>
    /// Where a recipient's browser should land. <c>Email:BaseUrl</c> wins when it is set,
    /// because the link mostly travels in email and the request that created it may have
    /// come through a proxy the recipient cannot reach; the request's own origin is the
    /// fallback, and is right for the copyable link an admin shares from that same browser.
    /// </summary>
    private static string AcceptUrl(HttpContext http, EmailOptions options, string token)
    {
        var origin = string.IsNullOrWhiteSpace(options.BaseUrl)
            ? $"{http.Request.Scheme}://{http.Request.Host}"
            : options.BaseUrl.TrimEnd('/');

        return $"{origin}/invite/{token}";
    }

    private static Dictionary<string, string> EmailVariables(
        Invitation invitation, string organizationName, string inviterName,
        string? recipientName, string acceptUrl) =>
        new()
        {
            ["organizationName"] = organizationName,
            ["inviterName"] = inviterName,
            ["recipientName"] = recipientName ?? "",
            ["roleName"] = invitation.Role.ToString().ToLowerInvariant(),
            ["roleArticle"] = invitation.Role == OrgRole.Admin ? "an" : "a",
            ["acceptUrl"] = acceptUrl,
            ["expiresOn"] = invitation.ExpiresAt.UtcDateTime.ToString("d MMMM yyyy", CultureInfo.InvariantCulture),
        };

    private static InvitationView ToView(
        Invitation invitation, IReadOnlyDictionary<string, UserSummary> inviters,
        IReadOnlyDictionary<Guid, string> projectKeys, DateTimeOffset now) =>
        new(invitation.Id,
            invitation.Email,
            invitation.Role,
            invitation.ProjectId,
            invitation.ProjectRole,
            inviters.TryGetValue(invitation.InvitedBy, out var inviter) ? inviter.DisplayName : "A colleague",
            invitation.CreatedAt,
            invitation.ExpiresAt,
            invitation.StatusAt(now),
            invitation.ProjectId is { } projectId ? projectKeys.GetValueOrDefault(projectId) : null,
            invitation.CanOperateFactory);

    /// <summary>One query for every project the listed invitations name, through the tenant filter.</summary>
    private static async Task<IReadOnlyDictionary<Guid, string>> ProjectKeysAsync(
        TenancyDbContext db, IReadOnlyCollection<Invitation> invitations, CancellationToken cancellationToken)
    {
        var ids = invitations.Where(i => i.ProjectId is not null).Select(i => i.ProjectId!.Value).Distinct().ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        return await db.Projects.AsNoTracking()
            .Where(p => ids.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.Key, cancellationToken);
    }

    private static Dictionary<Guid, string> ProjectKeyMap(Invitation invitation, string? projectKey) =>
        invitation.ProjectId is { } id && projectKey is not null ? new() { [id] = projectKey } : [];

    private static IResult Conflict(string detail) =>
        Results.Problem(
            title: "That is no longer possible.",
            detail: detail,
            type: ProblemTypes.Conflict,
            statusCode: StatusCodes.Status409Conflict);

    private static IResult PlanLimited(PlanLimitDecision decision) =>
        Results.Problem(
            title: "Plan limit reached.",
            detail: decision.Reason ?? "This organization cannot add another member on its current plan.",
            type: ProblemTypes.PlanLimit,
            statusCode: StatusCodes.Status402PaymentRequired,
            extensions: new Dictionary<string, object?> { ["limit"] = decision.Limit, ["upgradeUrl"] = decision.UpgradeUrl });
}

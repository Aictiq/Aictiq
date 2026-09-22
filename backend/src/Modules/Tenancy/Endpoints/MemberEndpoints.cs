using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Npgsql;
using Aictiq.Modules.Tenancy.Access;
using Aictiq.Modules.Tenancy.Domain;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Paging;

namespace Aictiq.Modules.Tenancy.Endpoints;

/// <param name="Email">
/// Null for a Guest reading the list - they see who is here without collecting the team's
/// addresses.
/// </param>
/// <param name="CanOperateFactory">
/// The effective answer, not the stored column: always true for an Owner or Admin,
/// always false for a Guest, and the Member's own flag otherwise.
/// </param>
public sealed record MemberView(
    string UserId, string DisplayName, string? Email, string? AvatarKey, bool IsAgent,
    OrgRole Role, DateTimeOffset JoinedAt, bool CanOperateFactory);

/// <summary>What a write returns: the membership, not the person. Identity did not change.</summary>
public sealed record MembershipView(string UserId, OrgRole Role, DateTimeOffset JoinedAt, bool CanOperateFactory);

/// <summary>Either field or both. A null leaves that half of the membership alone.</summary>
public sealed record UpdateMemberRequest(OrgRole? Role, bool? CanOperateFactory = null);

public static class MemberEndpoints
{
    public static IEndpointRouteBuilder MapMemberEndpoints(this IEndpointRouteBuilder api)
    {
        // {orgSlug} is what TenantResolutionMiddleware reads, so by the time any of these
        // run the organization exists, the caller belongs to it, and the query filter is
        // pinned to it. A non-member gets a 404 from the filter and never arrives here.
        var group = api.MapGroup("/orgs/{orgSlug}/members")
            .WithTags("Members")
            .RequireAuthorization();

        MapList(group);
        MapChangeRole(group);
        MapRemove(group);

        return api;
    }

    private static void MapList(RouteGroupBuilder group)
    {
        group.MapGet("/", async (
            TenancyDbContext db,
            IUserDirectory directory,
            ICurrentUser user,
            // Nullable, so the roster is a plain GET with no query string: a required
            // int would answer 400 to /members, which is the URL everything links to.
            int? page,
            int? pageSize,
            string? search,
            CancellationToken cancellationToken) =>
        {
            var paging = new PageRequest(page ?? 1, pageSize ?? 25);

            // The roster of one organization, bounded by its team size - this is the
            // tenant-filtered table, not the instance's user list.
            var memberships = await db.Members
                .AsNoTracking()
                .Select(m => new { m.UserId, m.Role, m.JoinedAt, m.CanOperateFactory })
                .ToListAsync(cancellationToken);

            var viewer = memberships.FirstOrDefault(m => m.UserId == user.UserId);
            if (viewer is null)
            {
                // Unreachable past RequireOrgRole; belt and braces, because the answer to
                // "who is in my organization" must never be given to someone who is not.
                return TenancyResults.NotFound();
            }

            // Names and email addresses live in Identity's schema, so the search runs
            // there against exactly these ids and comes back as one page.
            var found = await directory.SearchAsync(
                [.. memberships.Select(m => m.UserId)],
                search,
                paging.Skip,
                paging.NormalizedPageSize,
                cancellationToken);

            var showEmails = MembershipRules.CanSeeEmails(viewer.Role);
            var roles = memberships.ToDictionary(m => m.UserId);

            var items = found.Items
                // A membership whose user no longer exists is a row nothing can render;
                // it is dropped here rather than shown as a blank line with a menu.
                .Where(contact => roles.ContainsKey(contact.Id))
                .Select(contact => new MemberView(
                    contact.Id,
                    contact.DisplayName,
                    showEmails ? contact.Email : null,
                    contact.AvatarKey,
                    contact.IsAgent,
                    roles[contact.Id].Role,
                    roles[contact.Id].JoinedAt,
                    MembershipRules.CanOperateFactory(roles[contact.Id].Role, roles[contact.Id].CanOperateFactory)))
                .ToList();

            return Results.Ok(new PagedResult<MemberView>(
                items, paging.NormalizedPage, paging.NormalizedPageSize, found.TotalCount));
        })
        .RequireOrgRole(OrgRole.Guest)
        .RequireScope(Scopes.Read);
    }

    private static void MapChangeRole(RouteGroupBuilder group)
    {
        group.MapPut("/{userId}", async (
            string orgSlug,
            string userId,
            UpdateMemberRequest request,
            TenancyDbContext db,
            ICurrentUser user,
            HybridCache cache,
            NpgsqlDataSource dataSource,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            if (request.Role is null && request.CanOperateFactory is null)
            {
                return Results.ValidationProblem(
                    new Dictionary<string, string[]> { ["role"] = ["Pick owner, admin, member or guest."] },
                    type: ProblemTypes.Validation);
            }

            if (request.Role is { } named && !Enum.IsDefined(named))
            {
                return Results.ValidationProblem(
                    new Dictionary<string, string[]> { ["role"] = ["Pick owner, admin, member or guest."] },
                    type: ProblemTypes.Validation);
            }

            var (actor, target) = await LoadPairAsync(db, user.UserId, userId, cancellationToken);
            if (actor is null || target is null)
            {
                return TenancyResults.NotFound();
            }

            if (request.Role is { } desired)
            {
                if (!MembershipRules.CanAssign(actor.Role, target.Role, desired))
                {
                    return TenancyResults.Forbidden(
                        "Owners manage every role; admins manage members and guests.");
                }

                target.ChangeRole(desired, user.UserId!, timeProvider.GetUtcNow());
            }

            // Judged against the role the membership is about to have, so "demote to member and
            // take away the factory" is one request. Asking for what the role already decides
            // (true for an admin, false for a guest) is a no-op, not an error; asking for the
            // opposite is refused rather than silently ignored.
            if (request.CanOperateFactory is { } operate && operate != target.OperatesFactory)
            {
                if (!MembershipRules.IsFactoryFlagChoosable(target.Role))
                {
                    return Results.ValidationProblem(
                        new Dictionary<string, string[]>
                        {
                            ["canOperateFactory"] = [target.Role == OrgRole.Guest
                                ? "A guest never operates the factory. Make them a member first."
                                : "Owners and admins always operate the factory."],
                        },
                        type: ProblemTypes.Validation);
                }

                if (!MembershipRules.CanSetFactoryOperator(actor.Role, target.Role))
                {
                    return TenancyResults.Forbidden("Owners and admins decide who operates the factory.");
                }

                target.CanOperateFactory = operate;
            }

            // Demoting the last Owner is refused by tenancy.ensure_org_has_owner(), which
            // GlobalExceptionHandler turns into 409 last-owner. That check is the database's
            // because two Owners demoting each other at the same instant would each pass a
            // check made here - they cannot see one another's uncommitted work.
            await db.SaveChangesAsync(cancellationToken);
            await TenancyCache.InvalidateAsync(cache, dataSource, target.OrganizationId, orgSlug, cancellationToken);

            return Results.Ok(new MembershipView(target.UserId, target.Role, target.JoinedAt, target.OperatesFactory));
        })
        .RequireOrgRole(OrgRole.Admin)
        .RequireScope(Scopes.Write);
    }

    private static void MapRemove(RouteGroupBuilder group)
    {
        // Guest, not Admin: anyone may leave an organization they joined. Removing
        // *someone else* is checked below, where the caller's role can be compared with
        // the role of the person they are aiming at.
        group.MapDelete("/{userId}", async (
            string orgSlug,
            string userId,
            TenancyDbContext db,
            ICurrentUser user,
            HybridCache cache,
            NpgsqlDataSource dataSource,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var (actor, target) = await LoadPairAsync(db, user.UserId, userId, cancellationToken);
            if (actor is null || target is null)
            {
                return TenancyResults.NotFound();
            }

            var isSelf = string.Equals(actor.UserId, target.UserId, StringComparison.Ordinal);
            if (!isSelf && !MembershipRules.CanRemove(actor.Role, target.Role))
            {
                return TenancyResults.Forbidden(
                    "Owners can remove anyone; admins can remove members and guests.");
            }

            // Raised before the delete so the event and the row leave together: the
            // last-Owner trigger rolls this back, and a consumer must not hear about a
            // departure that did not happen.
            target.MarkRemoved(user.UserId!, timeProvider.GetUtcNow());
            db.Members.Remove(target);

            await db.SaveChangesAsync(cancellationToken);
            // Not an optimisation: their cached role is what would keep letting them in.
            await TenancyCache.InvalidateAsync(cache, dataSource, target.OrganizationId, orgSlug, cancellationToken);

            return Results.NoContent();
        })
        .RequireOrgRole(OrgRole.Guest)
        .RequireScope(Scopes.Write);
    }

    /// <summary>
    /// The caller's membership and their target's, in one round trip. Both come through
    /// the tenant query filter, so a user id from another organization simply is not found
    /// - which is the same answer as a user id that does not exist.
    /// </summary>
    private static async Task<(OrganizationMember? Actor, OrganizationMember? Target)> LoadPairAsync(
        TenancyDbContext db, string? actorId, string targetId, CancellationToken cancellationToken)
    {
        var rows = await db.Members
            .Where(m => m.UserId == actorId || m.UserId == targetId)
            .ToListAsync(cancellationToken);

        return (rows.FirstOrDefault(m => m.UserId == actorId),
                rows.FirstOrDefault(m => m.UserId == targetId));
    }
}

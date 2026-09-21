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
using Aictiq.SharedKernel.Tenancy;

namespace Aictiq.Modules.Tenancy.Endpoints;

/// <param name="IsImplicit">
/// True for someone who is here by virtue of their organization role or the project's
/// visibility rather than by being named on it. The distinction matters at the controls:
/// there is no explicit membership to remove, so the menu must not offer to.
/// </param>
public sealed record ProjectMemberView(
    string UserId, string DisplayName, string? Email, string? AvatarKey, bool IsAgent,
    ProjectRole Role, bool IsImplicit, DateTimeOffset? AddedAt);

public sealed record UpdateProjectMemberRequest(ProjectRole? Role);

public static class ProjectMemberEndpoints
{
    public static IEndpointRouteBuilder MapProjectMemberEndpoints(this IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/orgs/{orgSlug}/projects/{projectKey}/members")
            .WithTags("Projects")
            .RequireAuthorization();

        MapList(group);
        MapSetRole(group);
        MapRemove(group);

        return api;
    }

    private static void MapList(RouteGroupBuilder group)
    {
        group.MapGet("/", async (
            HttpContext http,
            TenancyDbContext db,
            IUserDirectory directory,
            IProjectAccess access,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            var projectId = http.ResolvedProjectId()!.Value;

            var project = await db.Projects
                .AsNoTracking()
                .Where(p => p.Id == projectId)
                .Select(p => new { p.Visibility })
                .FirstOrDefaultAsync(cancellationToken);

            if (project is null)
            {
                return TenancyResults.NotFound();
            }

            var organizationMembers = await db.Members
                .AsNoTracking()
                .Select(m => new { m.UserId, m.Role })
                .ToListAsync(cancellationToken);

            var explicitMembers = await db.ProjectMembers
                .AsNoTracking()
                .Where(m => m.ProjectId == projectId)
                .Select(m => new { m.UserId, m.Role, m.AddedAt })
                .ToListAsync(cancellationToken);

            var explicitByUser = explicitMembers.ToDictionary(m => m.UserId);

            // Everyone the project actually admits, with the role they actually hold —
            // the same rule the authorization filter applies, so the list can never
            // disagree with what the API will let each of them do.
            var rows = organizationMembers
                .Select(member => (
                    member.UserId,
                    role: ProjectAccessRules.Effective(
                        member.Role,
                        project.Visibility,
                        explicitByUser.TryGetValue(member.UserId, out var explicitRole) ? explicitRole.Role : null),
                    isExplicit: explicitByUser.ContainsKey(member.UserId)))
                .Where(row => row.role is not null)
                .ToList();

            // Same rule as the organization roster: a Guest sees who they are working with
            // without leaving with the team's address book.
            var viewer = organizationMembers.FirstOrDefault(m => m.UserId == user.UserId);
            var showEmails = viewer is not null && MembershipRules.CanSeeEmails(viewer.Role);

            var contacts = await directory.SearchAsync(
                [.. rows.Select(r => r.UserId)], null, 0, rows.Count, cancellationToken);
            var byId = contacts.Items.ToDictionary(c => c.Id);

            var items = rows
                .Where(row => byId.ContainsKey(row.UserId))
                .Select(row => ToView(
                    byId[row.UserId], row.role!.Value, row.isExplicit, showEmails,
                    explicitByUser.TryGetValue(row.UserId, out var added) ? added.AddedAt : null))
                .OrderBy(row => row.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            return Results.Ok(items);
        })
        .RequireProjectRole(ProjectRole.Guest)
        .RequireScope(Scopes.Read);
    }

    private static void MapSetRole(RouteGroupBuilder group)
    {
        group.MapPut("/{userId}", async (
            string orgSlug,
            string userId,
            UpdateProjectMemberRequest request,
            HttpContext http,
            TenancyDbContext db,
            IUserDirectory directory,
            ICurrentTenant tenant,
            ICurrentUser user,
            HybridCache cache,
            NpgsqlDataSource dataSource,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            if (request.Role is not { } desired || !Enum.IsDefined(desired))
            {
                return Results.ValidationProblem(
                    new Dictionary<string, string[]> { ["role"] = ["Pick admin, member or guest."] },
                    type: ProblemTypes.Validation);
            }

            var project = http.ResolvedProject()!;
            var organizationId = tenant.OrganizationId!.Value;

            // A project member has to be an organization member first. There is no foreign
            // key to lean on — user ids live in Identity's schema — so this is the check,
            // and it is also what makes a user id from another organization a 404.
            if (!await db.Members.AnyAsync(m => m.UserId == userId, cancellationToken))
            {
                return TenancyResults.NotFound();
            }

            var now = timeProvider.GetUtcNow();
            var member = await db.ProjectMembers
                .FirstOrDefaultAsync(m => m.ProjectId == project.Id && m.UserId == userId, cancellationToken);

            if (member is null)
            {
                // PUT, so it creates or updates: naming a role for someone who is only
                // here implicitly is how they become an explicit member, and a client
                // should not have to know which of the two it is doing.
                member = ProjectMember.Create(organizationId, project.Id, userId, desired, user.UserId!, now);
                db.ProjectMembers.Add(member);
            }
            else
            {
                member.ChangeRole(desired, user.UserId!, now);
            }

            await db.SaveChangesAsync(cancellationToken);
            await TenancyCache.InvalidateProjectAsync(
                cache, dataSource, organizationId, orgSlug, project.Id, project.Key, cancellationToken);

            var contacts = await directory.GetAsync([userId], cancellationToken);
            var summary = contacts.GetValueOrDefault(userId);

            return Results.Ok(new ProjectMemberView(
                userId, summary?.DisplayName ?? userId, null, summary?.AvatarKey,
                summary?.IsAgent ?? false, member.Role, false, member.AddedAt));
        })
        .RequireProjectRole(ProjectRole.Admin)
        .RequireProjectWritable()
        .RequireScope(Scopes.Write);
    }

    private static void MapRemove(RouteGroupBuilder group)
    {
        // Guest, not Admin: leaving a project you were added to is always yours to do.
        // Removing someone else is checked inside, where the caller can be compared.
        group.MapDelete("/{userId}", async (
            string orgSlug,
            string userId,
            HttpContext http,
            TenancyDbContext db,
            ICurrentTenant tenant,
            ICurrentUser user,
            IProjectAccess access,
            HybridCache cache,
            NpgsqlDataSource dataSource,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var project = http.ResolvedProject()!;

            var member = await db.ProjectMembers
                .FirstOrDefaultAsync(m => m.ProjectId == project.Id && m.UserId == userId, cancellationToken);

            if (member is null)
            {
                // Either they were never named on this project or they are only here
                // implicitly. Both are "there is no membership to remove" — and removing
                // an implicit one would mean removing them from the organization.
                return TenancyResults.NotFound();
            }

            var isSelf = string.Equals(user.UserId, userId, StringComparison.Ordinal);
            if (!isSelf)
            {
                var callerRole = await access.GetProjectRoleAsync(user.UserId!, project.Id, cancellationToken);
                if (callerRole != ProjectRole.Admin)
                {
                    return TenancyResults.Forbidden("Only a project admin can remove someone else.");
                }
            }

            member.MarkRemoved(user.UserId!, timeProvider.GetUtcNow());
            db.ProjectMembers.Remove(member);

            await db.SaveChangesAsync(cancellationToken);
            await TenancyCache.InvalidateProjectAsync(
                cache, dataSource, tenant.OrganizationId!.Value, orgSlug, project.Id, project.Key,
                cancellationToken);

            return Results.NoContent();
        })
        .RequireProjectRole(ProjectRole.Guest)
        .RequireProjectWritable()
        .RequireScope(Scopes.Write);
    }

    private static ProjectMemberView ToView(
        UserContact contact, ProjectRole role, bool isExplicit, bool showEmails, DateTimeOffset? addedAt) =>
        new(contact.Id, contact.DisplayName, showEmails ? contact.Email : null, contact.AvatarKey,
            contact.IsAgent, role, !isExplicit, addedAt);
}

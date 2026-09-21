using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Npgsql;
using Aictiq.Modules.Tenancy.Access;
using Aictiq.Modules.Tenancy.Domain;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Outbox;
using Aictiq.SharedKernel.Persistence;
using Aictiq.SharedKernel.Tenancy;

namespace Aictiq.Modules.Tenancy.Endpoints;

/// <param name="Role">
/// The caller's <b>effective</b> role here — the better of what their organization role
/// grants implicitly and what an explicit membership grants. It is what the client greys
/// its controls by.
/// </param>
public sealed record ProjectView(
    Guid Id, string Key, string Name, string? Description, ProjectVisibility Visibility,
    string? Icon, string? Color, bool IsArchived, DateTimeOffset CreatedAt,
    ProjectRole Role, uint Version);

/// <param name="Key">Optional — suggested from the name, uniquified, when omitted.</param>
public sealed record CreateProjectRequest(
    string? Name, string? Key, string? Description, ProjectVisibility? Visibility,
    string? Icon, string? Color);

/// <param name="Description">
/// Absent (null) leaves it alone; an empty string clears it. Same for
/// <paramref name="Icon"/> and <paramref name="Color"/> — a PATCH has to be able to say
/// "remove this", and null already means "not mentioned".
/// </param>
public sealed record UpdateProjectRequest(
    string? Name, string? Description, ProjectVisibility? Visibility,
    string? Icon, string? Color, uint Version);

/// <param name="Name">The project's name, typed out, as for deleting an organization.</param>
public sealed record DeleteProjectRequest(string? Name);

public static class ProjectEndpoints
{
    public static IEndpointRouteBuilder MapProjectEndpoints(this IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/orgs/{orgSlug}/projects")
            .WithTags("Projects")
            .RequireAuthorization();

        MapList(group);
        MapCreate(group);
        MapScoped(group);

        return api;
    }

    private static void MapList(RouteGroupBuilder group)
    {
        group.MapGet("/", async (
            TenancyDbContext db,
            ICurrentTenant tenant,
            ICurrentUser user,
            IProjectAccess access,
            bool? includeArchived,
            CancellationToken cancellationToken) =>
        {
            var organizationId = tenant.OrganizationId!.Value;
            var organizationRole = await access.GetOrgRoleAsync(user.UserId!, organizationId, cancellationToken);
            if (organizationRole is null)
            {
                return TenancyResults.NotFound();
            }

            var projects = await db.Projects
                .AsNoTracking()
                .Where(p => includeArchived == true || p.ArchivedAt == null)
                .OrderBy(p => p.Name)
                .ToListAsync(cancellationToken);

            // One query for every explicit membership the caller has in this organization,
            // rather than one per project. The list is bounded by the organization's own
            // project count, so this is two round trips whatever its size.
            var explicitRoles = await db.ProjectMembers
                .AsNoTracking()
                .Where(m => m.UserId == user.UserId)
                .ToDictionaryAsync(m => m.ProjectId, m => m.Role, cancellationToken);

            var visible = projects
                .Select(project => (project, role: ProjectAccessRules.Effective(
                    organizationRole,
                    project.Visibility,
                    explicitRoles.TryGetValue(project.Id, out var explicitRole) ? explicitRole : null)))
                // A private project the caller is not on simply is not in the list — the
                // same answer they would get from its URL, and for the same reason.
                .Where(row => row.role is not null)
                .Select(row => ToView(row.project, row.role!.Value))
                .ToList();

            return Results.Ok(visible);
        })
        .RequireOrgRole(OrgRole.Guest)
        .RequireScope(Scopes.Read);
    }

    private static void MapCreate(RouteGroupBuilder group)
    {
        group.MapPost("/", async (
            CreateProjectRequest request,
            string orgSlug,
            TenancyDbContext db,
            ICurrentTenant tenant,
            ICurrentUser user,
            IProjectAccess access,
            IPlanLimits planLimits,
            HybridCache cache,
            NpgsqlDataSource dataSource,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var organizationId = tenant.OrganizationId!.Value;

            var organization = await db.Organizations
                .AsNoTracking()
                .FirstOrDefaultAsync(o => o.Id == organizationId, cancellationToken);

            if (organization is null)
            {
                return TenancyResults.NotFound();
            }

            var organizationRole = await access.GetOrgRoleAsync(user.UserId!, organizationId, cancellationToken);
            if (organizationRole is null)
            {
                return TenancyResults.NotFound();
            }

            // Whether ordinary members may start a project is the organization's own
            // decision; admins and owners are never subject to it.
            if (organizationRole == OrgRole.Member && !organization.Settings.MembersCanCreateProjects)
            {
                return TenancyResults.Forbidden(
                    "This organization only lets admins create projects.");
            }

            var plan = await planLimits.CanCreateProjectAsync(organizationId, cancellationToken);
            if (!plan.Allowed)
            {
                return Results.Problem(plan.Reason, title: "Plan limit reached.", type: ProblemTypes.PlanLimit,
                    statusCode: StatusCodes.Status402PaymentRequired,
                    extensions: new Dictionary<string, object?> { ["limit"] = plan.Limit, ["upgradeUrl"] = plan.UpgradeUrl });
            }

            var errors = new Dictionary<string, string[]>();
            var name = request.Name?.Trim() ?? "";
            if (name.Length == 0)
            {
                errors["name"] = ["A name is required."];
            }
            else if (name.Length > Project.MaxNameLength)
            {
                errors["name"] = [$"Use {Project.MaxNameLength} characters or fewer."];
            }
            else if (await NameTakenAsync(db, name, null, cancellationToken))
            {
                errors["name"] = ["A project here already has that name."];
            }

            string? key = null;
            if (!string.IsNullOrWhiteSpace(request.Key))
            {
                key = request.Key.Trim().ToUpperInvariant();
                if (!KeyFormat.IsWellFormed(key))
                {
                    errors["key"] =
                    [
                        $"Use {Project.MinKeyLength}-{Project.MaxKeyLength} letters and digits, starting with a letter."
                    ];
                }
                else if (await db.Projects.AnyAsync(p => p.Key == key, cancellationToken))
                {
                    errors["key"] = ["That key is already in use here."];
                }
            }

            ValidateAppearance(request.Icon, request.Color, errors);

            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors, type: ProblemTypes.Validation);
            }

            key ??= await DeriveKeyAsync(db, name, cancellationToken);

            var now = timeProvider.GetUtcNow();
            var project = Project.Create(
                organizationId, key, name, Blank(request.Description),
                // Organization-visible by default: that is what a team of ten wants, and
                // private is the deliberate choice rather than the accidental one.
                request.Visibility ?? ProjectVisibility.Organization,
                Blank(request.Icon), Blank(request.Color)?.ToLowerInvariant(),
                user.UserId!, now);

            db.Projects.Add(project);
            // The creator is named on it explicitly, so a private project is never one
            // nobody can open, and so the person who made it keeps Admin if they are later
            // demoted to an ordinary member.
            db.ProjectMembers.Add(ProjectMember.Create(
                organizationId, project.Id, user.UserId!, ProjectRole.Admin, user.UserId!, now));

            // The default team, in the same transaction as the project. Not through the
            // ProjectCreated event, even though one is raised: Tenancy owns both tables in
            // one schema, so a transaction gives the stronger invariant the acceptance
            // criterion asks for — every project has exactly one default team, with no
            // window in which it has none. The event stays for the modules that cannot
            // share this transaction.
            db.Teams.Add(Team.Create(
                organizationId, project.Id,
                name.Length > Team.MaxNameLength ? name[..Team.MaxNameLength] : name,
                key, isDefault: true, now));

            await db.SaveChangesAsync(cancellationToken);

            // Evicts the negative "no such key" answer as well as the project's own tags.
            await TenancyCache.InvalidateProjectAsync(
                cache, dataSource, organizationId, orgSlug, project.Id, project.Key, cancellationToken);

            return Results.Created(
                $"/api/v1/orgs/{orgSlug}/projects/{project.Key}",
                ToView(project, ProjectRole.Admin));
        })
        // Member, not Admin: whether that is enough is the organization's setting, checked
        // above where the setting can actually be read.
        .RequireOrgRole(OrgRole.Member)
        .RequireScope(Scopes.Write);
    }

    private static void MapScoped(RouteGroupBuilder parent)
    {
        // {projectKey} is what RequireProjectRole reads. By the time these run the project
        // exists in this organization, the caller can see it, and the resolved project is
        // on the HttpContext for the writable filter and the endpoint alike.
        var group = parent.MapGroup("/{projectKey}");

        group.MapGet("/", async (
            HttpContext http,
            TenancyDbContext db,
            ICurrentUser user,
            IProjectAccess access,
            CancellationToken cancellationToken) =>
        {
            var projectId = http.ResolvedProjectId()!.Value;
            var project = await db.Projects
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);

            if (project is null)
            {
                return TenancyResults.NotFound();
            }

            var role = await access.GetProjectRoleAsync(user.UserId!, projectId, cancellationToken);
            return role is null ? TenancyResults.NotFound() : Results.Ok(ToView(project, role.Value));
        })
        .RequireProjectRole(ProjectRole.Guest)
        .RequireScope(Scopes.Read);

        group.MapPatch("/", async (
            UpdateProjectRequest request,
            string orgSlug,
            HttpContext http,
            TenancyDbContext db,
            ICurrentTenant tenant,
            HybridCache cache,
            NpgsqlDataSource dataSource,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var projectId = http.ResolvedProjectId()!.Value;
            var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
            if (project is null)
            {
                return TenancyResults.NotFound();
            }

            var errors = new Dictionary<string, string[]>();
            var name = request.Name?.Trim();
            if (name is not null)
            {
                if (name.Length == 0)
                {
                    errors["name"] = ["A name is required."];
                }
                else if (name.Length > Project.MaxNameLength)
                {
                    errors["name"] = [$"Use {Project.MaxNameLength} characters or fewer."];
                }
                else if (await NameTakenAsync(db, name, project.Id, cancellationToken))
                {
                    errors["name"] = ["A project here already has that name."];
                }
            }

            if (request.Description is { Length: > Project.MaxDescriptionLength })
            {
                errors["description"] = [$"Use {Project.MaxDescriptionLength} characters or fewer."];
            }

            ValidateAppearance(request.Icon, request.Color, errors);

            if (request.Visibility is { } visibility && !Enum.IsDefined(visibility))
            {
                errors["visibility"] = ["Pick private or organization."];
            }

            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors, type: ProblemTypes.Validation);
            }

            // The client echoes the xmin it read; a stale one throws
            // DbUpdateConcurrencyException, which GlobalExceptionHandler turns into 409.
            db.Entry(project).Property(p => p.Version).OriginalValue = request.Version;

            project.Update(
                name ?? project.Name,
                Patch(request.Description, project.Description),
                request.Visibility ?? project.Visibility,
                Patch(request.Icon, project.Icon),
                Patch(request.Color, project.Color)?.ToLowerInvariant(),
                timeProvider.GetUtcNow());

            await db.SaveChangesAsync(cancellationToken);

            // Visibility is half of every project-role answer, so this is not an
            // optimisation: a cached role computed against the old visibility is exactly
            // the stale entry that would keep letting someone in.
            await TenancyCache.InvalidateProjectAsync(
                cache, dataSource, tenant.OrganizationId!.Value, orgSlug, project.Id, project.Key,
                cancellationToken);

            return Results.Ok(ToView(project, ProjectRole.Admin));
        })
        .RequireProjectRole(ProjectRole.Admin)
        .RequireProjectWritable()
        .RequireScope(Scopes.Write);

        MapArchival(group, archive: true);
        MapArchival(group, archive: false);
        MapDelete(group);
    }

    /// <summary>
    /// Deletes the project for good. Tenancy's rows — the project, its memberships, its teams
    /// and theirs — go in this transaction; items, wiki, attachments, integrations, analytics
    /// and notifications follow from the <see cref="ProjectDeleted"/> committed with them.
    /// Archiving is the reversible way to retire a project; this is not.
    ///
    /// Not <c>RequireProjectWritable</c>: an archived project is exactly the kind a team
    /// finally decides to delete.
    /// </summary>
    private static void MapDelete(RouteGroupBuilder group)
    {
        group.MapDelete("/", async (
            [FromBody] DeleteProjectRequest request,
            string orgSlug,
            HttpContext http,
            TenancyDbContext db,
            ICurrentTenant tenant,
            ICurrentUser user,
            HybridCache cache,
            NpgsqlDataSource dataSource,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var projectId = http.ResolvedProjectId()!.Value;
            var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
            if (project is null)
            {
                return TenancyResults.NotFound();
            }

            if (!string.Equals(request.Name?.Trim(), project.Name, StringComparison.OrdinalIgnoreCase))
            {
                return Results.ValidationProblem(
                    new Dictionary<string, string[]> { ["name"] = ["Type the project's name exactly to confirm deletion."] },
                    type: ProblemTypes.Validation);
            }

            var organizationId = project.OrganizationId;
            await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
            {
                var teamIds = await db.Teams.Where(t => t.ProjectId == project.Id).Select(t => t.Id).ToListAsync(cancellationToken);

                // An invitation to the organization that also offered this project still
                // invites to the organization; only the part naming the project goes.
                await db.Invitations.Where(i => i.ProjectId == project.Id).ExecuteUpdateAsync(set => set
                    .SetProperty(i => i.ProjectId, (Guid?)null)
                    .SetProperty(i => i.ProjectRole, (ProjectRole?)null), cancellationToken);
                await db.Database.ExecuteSqlAsync($"DELETE FROM tenancy.projects WHERE id = {project.Id}", cancellationToken);

                // Audit rows carry what the project and its teams were called and who was on
                // them. A member's key is {projectId}/{userId}, which the project's id matches.
                await AuditPurge.EntitiesAsync(db, organizationId, AuditEntityTypes.Project, [project.Id], cancellationToken);
                await AuditPurge.EntitiesAsync(db, organizationId, AuditEntityTypes.ProjectMember, [project.Id], cancellationToken);
                await AuditPurge.EntitiesAsync(db, organizationId, AuditEntityTypes.Team, teamIds, cancellationToken);
                await AuditPurge.EntitiesAsync(db, organizationId, AuditEntityTypes.TeamMember, teamIds, cancellationToken);
                db.Set<AuditLogEntry>().Add(AuditPurge.Tombstone(organizationId, AuditEntityTypes.Project, project.Id,
                    project.Key, user.UserId, timeProvider.GetUtcNow()));
                db.Set<OutboxMessage>().Add(OutboxMessage.From(new ProjectDeleted(
                    organizationId, project.Id, project.Key, teamIds, user.UserId!)));
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }

            await TenancyCache.InvalidateProjectAsync(
                cache, dataSource, tenant.OrganizationId!.Value, orgSlug, project.Id, project.Key, cancellationToken);

            return Results.NoContent();
        })
        .RequireProjectRole(ProjectRole.Admin)
        .RequireScope(Scopes.Admin);
    }

    private static void MapArchival(RouteGroupBuilder group, bool archive)
    {
        // Neither carries RequireProjectWritable: archiving is a write *to* a live project
        // and un-archiving is by definition a write to an archived one.
        group.MapPost(archive ? "/archive" : "/unarchive", async (
            string orgSlug,
            HttpContext http,
            TenancyDbContext db,
            ICurrentTenant tenant,
            ICurrentUser user,
            HybridCache cache,
            NpgsqlDataSource dataSource,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var projectId = http.ResolvedProjectId()!.Value;
            var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
            if (project is null)
            {
                return TenancyResults.NotFound();
            }

            var now = timeProvider.GetUtcNow();
            // Both are idempotent: archiving an archived project raises nothing and
            // changes nothing, which is the right answer to a double-click.
            if (archive)
            {
                project.Archive(user.UserId!, now);
            }
            else
            {
                project.Unarchive(user.UserId!, now);
            }

            await db.SaveChangesAsync(cancellationToken);

            // Archived-ness is cached with the project's identity, and it is what every
            // write filter reads.
            await TenancyCache.InvalidateProjectAsync(
                cache, dataSource, tenant.OrganizationId!.Value, orgSlug, project.Id, project.Key,
                cancellationToken);

            return Results.Ok(ToView(project, ProjectRole.Admin));
        })
        .RequireProjectRole(ProjectRole.Admin)
        .RequireScope(Scopes.Write);
    }

    // --------------------------------------------------------------------------- helpers

    /// <summary>
    /// Case-insensitive, like the unique index behind it: two projects called "Website"
    /// and "website" in one organization would be indistinguishable in every list.
    /// </summary>
    private static Task<bool> NameTakenAsync(
        TenancyDbContext db, string name, Guid? excluding, CancellationToken cancellationToken) =>
        db.Projects.AnyAsync(
            p => p.Id != excluding && p.Name.ToLower() == name.ToLower(), cancellationToken);

    /// <summary>
    /// Turns a display name into a free key. Bounded, like the slug derivation it mirrors:
    /// a loop that asks the database forever is worse than a key with a digit on it.
    /// </summary>
    private static async Task<string> DeriveKeyAsync(
        TenancyDbContext db, string name, CancellationToken cancellationToken)
    {
        var stem = KeyFormat.Suggest(name);
        if (stem.Length < Project.MinKeyLength)
        {
            stem = "PRJ";
        }

        var candidate = stem;
        for (var suffix = 2; suffix <= 50; suffix++)
        {
            if (KeyFormat.IsWellFormed(candidate)
                && !await db.Projects.AnyAsync(p => p.Key == candidate, cancellationToken))
            {
                return candidate;
            }

            var room = Project.MaxKeyLength - suffix.ToString().Length;
            candidate = $"{stem[..Math.Min(stem.Length, room)]}{suffix}";
        }

        return $"{stem[..Math.Min(stem.Length, Project.MaxKeyLength - 4)]}{Random.Shared.Next(0x100, 0xFFF):X}";
    }

    private static void ValidateAppearance(string? icon, string? color, Dictionary<string, string[]> errors)
    {
        // A grapheme, not a character count: most emoji are two UTF-16 units and some are
        // a great deal more, so "one emoji" cannot be expressed as a length in chars.
        if (!string.IsNullOrEmpty(icon)
            && System.Globalization.StringInfo.GetTextElementEnumerator(icon).MoveNext()
            && new System.Globalization.StringInfo(icon).LengthInTextElements > 1)
        {
            errors["icon"] = ["Use a single emoji."];
        }

        if (!string.IsNullOrEmpty(color)
            && !System.Text.RegularExpressions.Regex.IsMatch(
                color, "^#[0-9a-fA-F]{6}$", System.Text.RegularExpressions.RegexOptions.None,
                TimeSpan.FromMilliseconds(100)))
        {
            errors["color"] = ["Use a hex colour such as #4f46e5."];
        }
    }

    /// <summary>PATCH semantics: null is "not mentioned", empty is "clear it".</summary>
    private static string? Patch(string? incoming, string? current) =>
        incoming is null ? current : Blank(incoming);

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    internal static ProjectView ToView(Project project, ProjectRole role) => new(
        project.Id, project.Key, project.Name, project.Description, project.Visibility,
        project.Icon, project.Color, project.IsArchived, project.CreatedAt, role, project.Version);
}

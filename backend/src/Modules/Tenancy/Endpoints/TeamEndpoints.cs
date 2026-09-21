using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Aictiq.Modules.Tenancy.Domain;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Tenancy;

namespace Aictiq.Modules.Tenancy.Endpoints;

/// <param name="IsLead">Whether the <b>caller</b> leads this team — what the UI enables its controls by.</param>
public sealed record TeamView(
    Guid Id, string Name, string Key, int SprintLengthDays, IReadOnlyList<int> WorkingDays,
    EstimationUnit EstimationUnit, string? TimeZone, bool IsDefault, int MemberCount,
    bool IsLead, uint Version);

public sealed record TeamMemberView(
    string UserId, string DisplayName, string? Email, string? AvatarKey, bool IsAgent,
    bool IsLead, decimal? CapacityHoursPerDay, DateTimeOffset AddedAt);

/// <param name="Key">Optional — suggested from the name, uniquified, when omitted.</param>
public sealed record CreateTeamRequest(string? Name, string? Key);

/// <param name="TimeZone">
/// Absent (null) leaves it alone; an empty string clears the override and returns the team
/// to the organization's zone. The same convention the project PATCH uses.
/// </param>
/// <param name="IsDefault">
/// Only <c>true</c> is meaningful: it promotes this team and demotes the current default
/// in one transaction. There is no way to say "no default" — every project has one.
/// </param>
public sealed record UpdateTeamRequest(
    string? Name, int? SprintLengthDays, int[]? WorkingDays, EstimationUnit? EstimationUnit,
    string? TimeZone, bool? IsDefault, uint Version);

public sealed record UpdateTeamMemberRequest(bool? IsLead, decimal? CapacityHoursPerDay);

/// <summary>
/// Teams inside a project: the backlog slice a board and a sprint actually belong to.
///
/// Reading needs project Guest. Writing needs project Admin <b>or</b> the team's own lead
/// — a lead runs their team's roster and planning settings without being handed the whole
/// project. That is why <c>is_lead</c> is a flag rather than a project role: it grants
/// authority over the team, not over the work.
/// </summary>
public static class TeamEndpoints
{
    public static IEndpointRouteBuilder MapTeamEndpoints(this IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/orgs/{orgSlug}/projects/{projectKey}/teams")
            .WithTags("Teams")
            .RequireAuthorization();

        MapList(group);
        MapCreate(group);
        MapUpdate(group);
        MapDelete(group);
        MapMembers(group);

        return api;
    }

    private static void MapList(RouteGroupBuilder group)
    {
        group.MapGet("/", async (
            HttpContext http,
            TenancyDbContext db,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            var projectId = http.ResolvedProjectId()!.Value;

            var teams = await db.Teams
                .AsNoTracking()
                .Where(t => t.ProjectId == projectId)
                // The default first, then alphabetically: the default is where work lands,
                // so it is the one people look for.
                .OrderByDescending(t => t.IsDefault)
                .ThenBy(t => t.Name)
                .ToListAsync(cancellationToken);

            var counts = await CountsAsync(db, teams, cancellationToken);
            var leading = await LeadingAsync(db, teams, user.UserId, cancellationToken);

            return Results.Ok(teams.Select(t => ToView(t, counts, leading)).ToList());
        })
        .RequireProjectRole(ProjectRole.Guest)
        .RequireScope(Scopes.Read);

        group.MapGet("/{teamId:guid}", async (
            Guid teamId,
            HttpContext http,
            TenancyDbContext db,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            var team = await FindAsync(db, http, teamId, cancellationToken);
            if (team is null)
            {
                return TenancyResults.NotFound();
            }

            var counts = await CountsAsync(db, [team], cancellationToken);
            var leading = await LeadingAsync(db, [team], user.UserId, cancellationToken);

            return Results.Ok(ToView(team, counts, leading));
        })
        .RequireProjectRole(ProjectRole.Guest)
        .RequireScope(Scopes.Read);
    }

    private static void MapCreate(RouteGroupBuilder group)
    {
        group.MapPost("/", async (
            CreateTeamRequest request,
            string orgSlug,
            string projectKey,
            HttpContext http,
            TenancyDbContext db,
            ICurrentTenant tenant,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var projectId = http.ResolvedProjectId()!.Value;

            var errors = new Dictionary<string, string[]>();
            var name = request.Name?.Trim() ?? "";
            await ValidateNameAsync(db, projectId, name, null, errors, cancellationToken);

            string? key = null;
            if (!string.IsNullOrWhiteSpace(request.Key))
            {
                key = request.Key.Trim().ToUpperInvariant();
                if (!KeyFormat.IsWellFormed(key))
                {
                    errors["key"] =
                    [
                        $"Use {KeyFormat.MinLength}-{KeyFormat.MaxLength} letters and digits, starting with a letter."
                    ];
                }
                else if (await db.Teams.AnyAsync(t => t.ProjectId == projectId && t.Key == key, cancellationToken))
                {
                    errors["key"] = ["Another team in this project already uses that key."];
                }
            }

            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors, type: ProblemTypes.Validation);
            }

            key ??= await DeriveKeyAsync(db, projectId, name, cancellationToken);

            var now = timeProvider.GetUtcNow();
            // Never the default: a project already has one, and promoting is a separate,
            // deliberate act that demotes the incumbent in the same transaction.
            var team = Team.Create(tenant.OrganizationId!.Value, projectId, name, key, false, now);

            db.Teams.Add(team);
            await db.SaveChangesAsync(cancellationToken);

            return Results.Created(
                $"/api/v1/orgs/{orgSlug}/projects/{projectKey}/teams/{team.Id}",
                ToView(team, new Dictionary<Guid, int>(), []));
        })
        .RequireProjectRole(ProjectRole.Admin)
        .RequireProjectWritable()
        .RequireScope(Scopes.Write);
    }

    private static void MapUpdate(RouteGroupBuilder group)
    {
        group.MapPatch("/{teamId:guid}", async (
            Guid teamId,
            UpdateTeamRequest request,
            HttpContext http,
            TenancyDbContext db,
            ICurrentUser user,
            IProjectAccess access,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var team = await FindAsync(db, http, teamId, cancellationToken, track: true);
            if (team is null)
            {
                return TenancyResults.NotFound();
            }

            if (!await CanManageAsync(db, access, user, http, teamId, cancellationToken))
            {
                return TenancyResults.Forbidden("Only a project admin or this team's lead can change it.");
            }

            var errors = new Dictionary<string, string[]>();

            var name = request.Name?.Trim();
            if (name is not null)
            {
                await ValidateNameAsync(db, team.ProjectId, name, team.Id, errors, cancellationToken);
            }

            if (request.SprintLengthDays is { } days && !Team.IsValidSprintLength(days))
            {
                errors["sprintLengthDays"] =
                [
                    $"A sprint is between {Team.MinSprintLengthDays} and {Team.MaxSprintLengthDays} days."
                ];
            }

            if (request.WorkingDays is { } workingDays && !Team.IsValidWorkingDays(workingDays))
            {
                errors["workingDays"] = ["Pick between one and seven distinct days, Sunday (0) to Saturday (6)."];
            }

            if (request.EstimationUnit is { } unit && !Enum.IsDefined(unit))
            {
                errors["estimationUnit"] = ["Estimate in points or hours."];
            }

            // Empty clears the override; anything else has to be a zone this machine knows,
            // or it would fail later at conversion time inside whatever request needed it.
            if (!string.IsNullOrEmpty(request.TimeZone)
                && !OrganizationSettings.IsKnownTimeZone(request.TimeZone))
            {
                errors["timeZone"] = ["Unknown time zone. Use an IANA identifier, e.g. Europe/Sarajevo."];
            }

            if (request.IsDefault == false && team.IsDefault)
            {
                errors["isDefault"] = ["Promote another team instead — a project always has a default."];
            }

            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors, type: ProblemTypes.Validation);
            }

            db.Entry(team).Property(t => t.Version).OriginalValue = request.Version;

            var now = timeProvider.GetUtcNow();
            team.Update(
                name ?? team.Name,
                request.SprintLengthDays ?? team.SprintLengthDays,
                request.WorkingDays ?? team.WorkingDays,
                request.EstimationUnit ?? team.EstimationUnit,
                request.TimeZone is null ? team.TimeZone : Blank(request.TimeZone),
                now);

            if (request.IsDefault == true && !team.IsDefault)
            {
                // Two statements, in this order, inside one transaction: the partial unique
                // index would refuse a moment in which two teams claim the default, and EF
                // does not promise the order of the updates in a batch.
                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

                await db.Teams
                    .Where(t => t.ProjectId == team.ProjectId && t.IsDefault)
                    .ExecuteUpdateAsync(set => set.SetProperty(t => t.IsDefault, false), cancellationToken);

                team.IsDefault = true;
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            else
            {
                await db.SaveChangesAsync(cancellationToken);
            }

            var counts = await CountsAsync(db, [team], cancellationToken);
            var leading = await LeadingAsync(db, [team], user.UserId, cancellationToken);
            return Results.Ok(ToView(team, counts, leading));
        })
        // Guest at the project level, because a team lead need not be a project admin;
        // the real check is CanManageAsync, which can see both.
        .RequireProjectRole(ProjectRole.Guest)
        .RequireProjectWritable()
        .RequireScope(Scopes.Write);
    }

    private static void MapDelete(RouteGroupBuilder group)
    {
        group.MapDelete("/{teamId:guid}", async (
            Guid teamId,
            HttpContext http,
            TenancyDbContext db,
            ITeamUsage usage,
            CancellationToken cancellationToken) =>
        {
            var team = await FindAsync(db, http, teamId, cancellationToken, track: true);
            if (team is null)
            {
                return TenancyResults.NotFound();
            }

            if (team.IsDefault)
            {
                return Conflict(
                    "This is the project's default team. Make another team the default first.");
            }

            // Asked, not answered: the work items that would be orphaned live in another
            // module's schema, and deleting under them would leave rows pointing at nothing.
            if (await usage.HasItemsAsync(teamId, cancellationToken))
            {
                return Conflict(
                    "Items are still assigned to this team. Move them to another team first.");
            }

            db.Teams.Remove(team);
            await db.SaveChangesAsync(cancellationToken);

            return Results.NoContent();
        })
        // Deleting a team is a project decision, not a team one: a lead cannot delete the
        // team they lead out from under the project.
        .RequireProjectRole(ProjectRole.Admin)
        .RequireProjectWritable()
        .RequireScope(Scopes.Write);
    }

    private static void MapMembers(RouteGroupBuilder group)
    {
        group.MapGet("/{teamId:guid}/members", async (
            Guid teamId,
            HttpContext http,
            TenancyDbContext db,
            IUserDirectory directory,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            if (await FindAsync(db, http, teamId, cancellationToken) is null)
            {
                return TenancyResults.NotFound();
            }

            var members = await db.TeamMembers
                .AsNoTracking()
                .Where(m => m.TeamId == teamId)
                .ToListAsync(cancellationToken);

            var viewerRole = await db.Members
                .AsNoTracking()
                .Where(m => m.UserId == user.UserId)
                .Select(m => (OrgRole?)m.Role)
                .FirstOrDefaultAsync(cancellationToken);

            var showEmails = viewerRole is { } role && MembershipRules.CanSeeEmails(role);
            var contacts = await directory.SearchAsync(
                [.. members.Select(m => m.UserId)], null, 0, Math.Max(members.Count, 1), cancellationToken);
            var byId = contacts.Items.ToDictionary(c => c.Id);

            return Results.Ok(members
                .Where(m => byId.ContainsKey(m.UserId))
                .Select(m => new TeamMemberView(
                    m.UserId, byId[m.UserId].DisplayName, showEmails ? byId[m.UserId].Email : null,
                    byId[m.UserId].AvatarKey, byId[m.UserId].IsAgent,
                    m.IsLead, m.CapacityHoursPerDay, m.AddedAt))
                // Leads first: they are who you look for on a team page.
                .OrderByDescending(m => m.IsLead)
                .ThenBy(m => m.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList());
        })
        .RequireProjectRole(ProjectRole.Guest)
        .RequireScope(Scopes.Read);

        group.MapPut("/{teamId:guid}/members/{userId}", async (
            Guid teamId,
            string userId,
            UpdateTeamMemberRequest request,
            HttpContext http,
            TenancyDbContext db,
            IUserDirectory directory,
            ICurrentTenant tenant,
            ICurrentUser user,
            IProjectAccess access,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var team = await FindAsync(db, http, teamId, cancellationToken);
            if (team is null)
            {
                return TenancyResults.NotFound();
            }

            if (!await CanManageAsync(db, access, user, http, teamId, cancellationToken))
            {
                return TenancyResults.Forbidden("Only a project admin or this team's lead can change its roster.");
            }

            // A team member must be able to see the project the team belongs to. There is
            // no foreign key across schemas to lean on, so this is the check — recorded as
            // a known gap for the RLS pass.
            if (await access.GetProjectRoleAsync(userId, team.ProjectId, cancellationToken) is null)
            {
                return TenancyResults.NotFound();
            }

            if (request.CapacityHoursPerDay is { } capacity && capacity is < 0 or > 24)
            {
                return Results.ValidationProblem(
                    new Dictionary<string, string[]>
                    {
                        ["capacityHoursPerDay"] = ["A working day is between 0 and 24 hours."]
                    },
                    type: ProblemTypes.Validation);
            }

            var now = timeProvider.GetUtcNow();
            var member = await db.TeamMembers
                .FirstOrDefaultAsync(m => m.TeamId == teamId && m.UserId == userId, cancellationToken);

            if (member is null)
            {
                member = TeamMember.Create(
                    tenant.OrganizationId!.Value, teamId, userId,
                    request.IsLead ?? false, request.CapacityHoursPerDay, now);
                db.TeamMembers.Add(member);
            }
            else
            {
                // PUT, so absent fields keep their value: a capacity edit must not silently
                // demote a lead.
                member.IsLead = request.IsLead ?? member.IsLead;
                member.CapacityHoursPerDay = request.CapacityHoursPerDay ?? member.CapacityHoursPerDay;
            }

            await db.SaveChangesAsync(cancellationToken);

            var contacts = await directory.GetAsync([userId], cancellationToken);
            var summary = contacts.GetValueOrDefault(userId);

            return Results.Ok(new TeamMemberView(
                userId, summary?.DisplayName ?? userId, null, summary?.AvatarKey,
                summary?.IsAgent ?? false, member.IsLead, member.CapacityHoursPerDay, member.AddedAt));
        })
        .RequireProjectRole(ProjectRole.Guest)
        .RequireProjectWritable()
        .RequireScope(Scopes.Write);

        group.MapDelete("/{teamId:guid}/members/{userId}", async (
            Guid teamId,
            string userId,
            HttpContext http,
            TenancyDbContext db,
            ICurrentUser user,
            IProjectAccess access,
            CancellationToken cancellationToken) =>
        {
            if (await FindAsync(db, http, teamId, cancellationToken) is null)
            {
                return TenancyResults.NotFound();
            }

            var isSelf = string.Equals(user.UserId, userId, StringComparison.Ordinal);
            if (!isSelf && !await CanManageAsync(db, access, user, http, teamId, cancellationToken))
            {
                return TenancyResults.Forbidden("Only a project admin or this team's lead can change its roster.");
            }

            var member = await db.TeamMembers
                .FirstOrDefaultAsync(m => m.TeamId == teamId && m.UserId == userId, cancellationToken);

            if (member is null)
            {
                return TenancyResults.NotFound();
            }

            db.TeamMembers.Remove(member);
            await db.SaveChangesAsync(cancellationToken);

            return Results.NoContent();
        })
        .RequireProjectRole(ProjectRole.Guest)
        .RequireProjectWritable()
        .RequireScope(Scopes.Write);
    }

    // --------------------------------------------------------------------------- helpers

    /// <summary>
    /// A team inside the project the route named. Scoping by <c>ProjectId</c> is what makes
    /// a team id from another project a 404 rather than a leak — the id alone is enough to
    /// find a row, and the tenant filter only narrows it to the organization.
    /// </summary>
    private static Task<Team?> FindAsync(
        TenancyDbContext db, HttpContext http, Guid teamId, CancellationToken cancellationToken,
        bool track = false)
    {
        var projectId = http.ResolvedProjectId()!.Value;
        var query = track ? db.Teams : db.Teams.AsNoTracking();
        return query.FirstOrDefaultAsync(t => t.Id == teamId && t.ProjectId == projectId, cancellationToken);
    }

    /// <summary>Project admin, or this team's own lead. The two authorities teams have.</summary>
    private static async Task<bool> CanManageAsync(
        TenancyDbContext db, IProjectAccess access, ICurrentUser user, HttpContext http,
        Guid teamId, CancellationToken cancellationToken)
    {
        var projectId = http.ResolvedProjectId()!.Value;
        if (await access.GetProjectRoleAsync(user.UserId!, projectId, cancellationToken) == ProjectRole.Admin)
        {
            return true;
        }

        return await db.TeamMembers
            .AsNoTracking()
            .AnyAsync(m => m.TeamId == teamId && m.UserId == user.UserId && m.IsLead, cancellationToken);
    }

    private static async Task ValidateNameAsync(
        TenancyDbContext db, Guid projectId, string name, Guid? excluding,
        Dictionary<string, string[]> errors, CancellationToken cancellationToken)
    {
        if (name.Length == 0)
        {
            errors["name"] = ["A name is required."];
        }
        else if (name.Length > Team.MaxNameLength)
        {
            errors["name"] = [$"Use {Team.MaxNameLength} characters or fewer."];
        }
        else if (await db.Teams.AnyAsync(
            t => t.ProjectId == projectId && t.Id != excluding && t.Name.ToLower() == name.ToLower(),
            cancellationToken))
        {
            errors["name"] = ["Another team in this project already has that name."];
        }
    }

    private static async Task<string> DeriveKeyAsync(
        TenancyDbContext db, Guid projectId, string name, CancellationToken cancellationToken)
    {
        var stem = KeyFormat.Suggest(name);
        if (stem.Length < KeyFormat.MinLength)
        {
            stem = "TEAM";
        }

        var candidate = stem;
        for (var suffix = 2; suffix <= 50; suffix++)
        {
            if (KeyFormat.IsWellFormed(candidate)
                && !await db.Teams.AnyAsync(
                    t => t.ProjectId == projectId && t.Key == candidate, cancellationToken))
            {
                return candidate;
            }

            var room = KeyFormat.MaxLength - suffix.ToString().Length;
            candidate = $"{stem[..Math.Min(stem.Length, room)]}{suffix}";
        }

        return $"{stem[..Math.Min(stem.Length, KeyFormat.MaxLength - 4)]}{Random.Shared.Next(0x100, 0xFFF):X}";
    }

    private static async Task<Dictionary<Guid, int>> CountsAsync(
        TenancyDbContext db, IReadOnlyCollection<Team> teams, CancellationToken cancellationToken)
    {
        if (teams.Count == 0)
        {
            return [];
        }

        var ids = teams.Select(t => t.Id).ToArray();
        return await db.TeamMembers
            .AsNoTracking()
            .Where(m => ids.Contains(m.TeamId))
            .GroupBy(m => m.TeamId)
            .Select(g => new { TeamId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(row => row.TeamId, row => row.Count, cancellationToken);
    }

    private static async Task<HashSet<Guid>> LeadingAsync(
        TenancyDbContext db, IReadOnlyCollection<Team> teams, string? userId,
        CancellationToken cancellationToken)
    {
        if (teams.Count == 0 || userId is null)
        {
            return [];
        }

        var ids = teams.Select(t => t.Id).ToArray();
        var led = await db.TeamMembers
            .AsNoTracking()
            .Where(m => ids.Contains(m.TeamId) && m.UserId == userId && m.IsLead)
            .Select(m => m.TeamId)
            .ToListAsync(cancellationToken);

        return [.. led];
    }

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static TeamView ToView(Team team, IReadOnlyDictionary<Guid, int> counts, HashSet<Guid> leading) =>
        new(team.Id, team.Name, team.Key, team.SprintLengthDays, team.WorkingDays,
            team.EstimationUnit, team.TimeZone, team.IsDefault,
            counts.TryGetValue(team.Id, out var count) ? count : 0,
            leading.Contains(team.Id), team.Version);

    private static IResult Conflict(string detail) =>
        Results.Problem(
            title: "That is no longer possible.",
            detail: detail,
            type: ProblemTypes.Conflict,
            statusCode: StatusCodes.Status409Conflict);
}

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Aictiq.Modules.Tenancy;
using Aictiq.Modules.WorkItems.Domain;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Tenancy;

namespace Aictiq.Modules.WorkItems.Endpoints;

public sealed record SprintProgress(int TotalItems, int CompletedItems, decimal PointsTotal, decimal PointsDone, decimal RemainingHours);
public sealed record SprintView(Guid Id, Guid TeamId, string Name, string Goal, DateOnly StartsOn, DateOnly EndsOn,
    SprintState State, bool AutoCreateNext, int DaysLeft, SprintProgress Progress, uint Version);
public sealed record CreateSprintRequest(string? Name, string? Goal, DateOnly StartsOn, DateOnly EndsOn, bool AutoCreateNext = false);
public sealed record UpdateSprintRequest(string? Name, string? Goal, DateOnly? StartsOn, DateOnly? EndsOn, bool? AutoCreateNext, uint Version);
public sealed record CompleteSprintRequest(Guid? MoveUnfinishedTo, bool MoveUnfinishedToBacklog = false);
public sealed record SprintCapacityMemberRequest(string? UserId, decimal HoursPerDay, int DaysOff);
public sealed record UpdateSprintCapacityRequest(IReadOnlyList<SprintCapacityMemberRequest>? Members);
public sealed record SprintCapacityMemberView(string UserId, string DisplayName, bool IsAgent, decimal HoursPerDay,
    int DaysOff, int WorkingDays, decimal CapacityHours, decimal AssignedRemainingHours, decimal UtilizationPercent);
public sealed record SprintCapacityView(Guid SprintId, int WorkingDays, decimal CapacityHours, decimal AssignedRemainingHours,
    decimal UtilizationPercent, IReadOnlyList<SprintCapacityMemberView> Members);

public static class SprintEndpoints
{
    public static IEndpointRouteBuilder MapSprintEndpoints(this IEndpointRouteBuilder api)
    {
        var teams = api.MapGroup("/orgs/{orgSlug}/teams").WithTags("Sprints").RequireAuthorization();
        teams.MapGet("/{teamId:guid}/sprints", List).RequireOrgRole(OrgRole.Guest).RequireScope(Scopes.Read);
        teams.MapPost("/{teamId:guid}/sprints", Create).RequireOrgRole(OrgRole.Member).RequireScope(Scopes.Write);
        var sprints = api.MapGroup("/orgs/{orgSlug}/sprints").WithTags("Sprints").RequireAuthorization();
        sprints.MapGet("/{sprintId:guid}", Get).RequireOrgRole(OrgRole.Guest).RequireScope(Scopes.Read);
        sprints.MapPatch("/{sprintId:guid}", Update).RequireOrgRole(OrgRole.Member).RequireScope(Scopes.Write);
        sprints.MapPost("/{sprintId:guid}/start", Start).RequireOrgRole(OrgRole.Member).RequireScope(Scopes.Write);
        sprints.MapPost("/{sprintId:guid}/complete", Complete).RequireOrgRole(OrgRole.Member).RequireScope(Scopes.Write);
        sprints.MapGet("/{sprintId:guid}/capacity", Capacity).RequireOrgRole(OrgRole.Guest).RequireScope(Scopes.Read);
        sprints.MapPut("/{sprintId:guid}/capacity", UpdateCapacity).RequireOrgRole(OrgRole.Member).RequireScope(Scopes.Write);
        return api;
    }

    /// <summary>
    /// Sprint routes name a team or a sprint, not a project, so <c>RequireProjectWritable</c>
    /// cannot run on them; this is the same refusal for the project the team belongs to.
    /// </summary>
    internal static async Task<IResult?> WriteRefusalAsync(HttpContext http, TenancyDbContext tenancy, Guid projectId, CancellationToken ct)
    {
        var project = await tenancy.Projects.AsNoTracking().Where(x => x.Id == projectId)
            .Select(x => new { x.Key, IsArchived = x.ArchivedAt != null }).FirstOrDefaultAsync(ct);
        return project is null ? Results.NotFound() : await AuthorizationFilters.ProjectWriteRefusalAsync(http, project.Key, project.IsArchived);
    }

    private static async Task<IResult> List(Guid teamId, WorkItemsDbContext db, TenancyDbContext tenancy, IProjectAccess access, ICurrentUser user, CancellationToken ct)
    {
        var team = await tenancy.Teams.AsNoTracking().FirstOrDefaultAsync(x => x.Id == teamId, ct);
        if (team is null || await access.GetProjectRoleAsync(user.UserId!, team.ProjectId, ct) is null) return Results.NotFound();
        return Results.Ok(await ViewsAsync(db, await db.Sprints.Where(x => x.TeamId == teamId).OrderByDescending(x => x.StartsOn).ToListAsync(ct), ct));
    }

    // A sprint id travels on its own - in a link, a CLI argument, an agent's notes - with
    // no team in hand, so reading one back must not require knowing which team owns it.
    // Visibility is still the project's: a non-member gets 404, never the sprint's name.
    private static async Task<IResult> Get(Guid sprintId, WorkItemsDbContext db, TenancyDbContext tenancy,
        IProjectAccess access, ICurrentUser user, CancellationToken ct)
    {
        var sprint = await db.Sprints.AsNoTracking().FirstOrDefaultAsync(x => x.Id == sprintId, ct);
        if (sprint is null) return Results.NotFound();
        var team = await tenancy.Teams.AsNoTracking().FirstOrDefaultAsync(x => x.Id == sprint.TeamId, ct);
        if (team is null || await access.GetProjectRoleAsync(user.UserId!, team.ProjectId, ct) is null) return Results.NotFound();
        return Results.Ok((await ViewsAsync(db, [sprint], ct))[0]);
    }

    private static async Task<IResult> Create(Guid teamId, HttpContext http, CreateSprintRequest request, WorkItemsDbContext db, TenancyDbContext tenancy,
        IProjectAccess access, ICurrentUser user, ICurrentTenant tenant, TimeProvider clock, CancellationToken ct)
    {
        var team = await tenancy.Teams.AsNoTracking().FirstOrDefaultAsync(x => x.Id == teamId, ct);
        if (team is null || await access.GetProjectRoleAsync(user.UserId!, team.ProjectId, ct) is not { } role || !role.Satisfies(ProjectRole.Member)) return Results.NotFound();
        if (await WriteRefusalAsync(http, tenancy, team.ProjectId, ct) is { } readOnly) return readOnly;
        if (Invalid(request.Name, request.StartsOn, request.EndsOn) is { } invalid) return invalid;
        var sprint = new Sprint { OrganizationId = tenant.OrganizationId!.Value, TeamId = teamId, Name = request.Name!.Trim(), Goal = request.Goal?.Trim() ?? "", StartsOn = request.StartsOn, EndsOn = request.EndsOn, AutoCreateNext = request.AutoCreateNext, State = SprintState.Planned, CreatedAt = clock.GetUtcNow() };
        sprint.Changed(team.ProjectId, user.UserId!);
        db.Sprints.Add(sprint);
        try { await db.SaveChangesAsync(ct); } catch (DbUpdateException) { return Results.Problem("This sprint overlaps another sprint for the team.", statusCode: 409); }
        return Results.Created($"/api/v1/orgs/{tenant.OrganizationId}/sprints/{sprint.Id}", (await ViewsAsync(db, [sprint], ct))[0]);
    }

    private static async Task<IResult> Update(Guid sprintId, HttpContext http, UpdateSprintRequest request, WorkItemsDbContext db, TenancyDbContext tenancy,
        IProjectAccess access, ICurrentUser user, CancellationToken ct)
    {
        var sprint = await db.Sprints.FirstOrDefaultAsync(x => x.Id == sprintId, ct); if (sprint is null) return Results.NotFound();
        var team = await tenancy.Teams.AsNoTracking().FirstOrDefaultAsync(x => x.Id == sprint.TeamId, ct);
        if (team is null || await access.GetProjectRoleAsync(user.UserId!, team.ProjectId, ct) is not { } role || !role.Satisfies(ProjectRole.Member)) return Results.NotFound();
        if (await WriteRefusalAsync(http, tenancy, team.ProjectId, ct) is { } readOnly) return readOnly;
        if (sprint.State != SprintState.Planned) return Results.Problem("Only a planned sprint can be edited.", statusCode: 409);
        if (sprint.Version != request.Version) return Conflict();
        var starts = request.StartsOn ?? sprint.StartsOn; var ends = request.EndsOn ?? sprint.EndsOn;
        if (Invalid(request.Name ?? sprint.Name, starts, ends) is { } invalid) return invalid;
        db.Entry(sprint).Property(x => x.Version).OriginalValue = request.Version;
        sprint.Name = request.Name?.Trim() ?? sprint.Name; sprint.Goal = request.Goal?.Trim() ?? sprint.Goal; sprint.StartsOn = starts; sprint.EndsOn = ends; sprint.AutoCreateNext = request.AutoCreateNext ?? sprint.AutoCreateNext; sprint.Changed(team.ProjectId, user.UserId!);
        await db.SaveChangesAsync(ct); return Results.Ok((await ViewsAsync(db, [sprint], ct))[0]);
    }

    private static async Task<IResult> Start(Guid sprintId, HttpContext http, WorkItemsDbContext db, TenancyDbContext tenancy, IProjectAccess access, ICurrentUser user, TimeProvider clock, CancellationToken ct)
    {
        var sprint = await db.Sprints.FirstOrDefaultAsync(x => x.Id == sprintId, ct); if (sprint is null) return Results.NotFound();
        var team = await tenancy.Teams.AsNoTracking().FirstOrDefaultAsync(x => x.Id == sprint.TeamId, ct);
        if (team is null || await access.GetProjectRoleAsync(user.UserId!, team.ProjectId, ct) is not { } role || !role.Satisfies(ProjectRole.Member)) return Results.NotFound();
        if (await WriteRefusalAsync(http, tenancy, team.ProjectId, ct) is { } readOnly) return readOnly;
        var changed = await db.Sprints.Where(x => x.Id == sprintId && x.State == SprintState.Planned && !db.Sprints.Any(other => other.TeamId == x.TeamId && other.State == SprintState.Active))
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.State, SprintState.Active).SetProperty(x => x.StartedAt, clock.GetUtcNow()), ct);
        if (changed == 0) return Results.Problem("A sprint is already active or this sprint changed.", statusCode: 409);
        db.ChangeTracker.Clear(); var started = await db.Sprints.SingleAsync(x => x.Id == sprintId, ct); started.Started(); started.Changed(team.ProjectId, user.UserId!); await db.SaveChangesAsync(ct);
        return Results.Ok((await ViewsAsync(db, [started], ct))[0]);
    }

    private static async Task<IResult> Complete(Guid sprintId, HttpContext http, CompleteSprintRequest request, WorkItemsDbContext db, TenancyDbContext tenancy,
        IProjectAccess access, ICurrentUser user, TimeProvider clock, CancellationToken ct)
    {
        var sprint = await db.Sprints.FirstOrDefaultAsync(x => x.Id == sprintId, ct); if (sprint is null) return Results.NotFound();
        var team = await tenancy.Teams.AsNoTracking().FirstOrDefaultAsync(x => x.Id == sprint.TeamId, ct);
        if (team is null || await access.GetProjectRoleAsync(user.UserId!, team.ProjectId, ct) is not { } role || !role.Satisfies(ProjectRole.Member)) return Results.NotFound();
        if (await WriteRefusalAsync(http, tenancy, team.ProjectId, ct) is { } readOnly) return readOnly;
        if (sprint.State != SprintState.Active) return Results.Problem("Only the active sprint can be completed.", statusCode: 409);
        if (request.MoveUnfinishedTo is not null && request.MoveUnfinishedToBacklog) return Results.ValidationProblem(new Dictionary<string, string[]> { ["moveUnfinishedTo"] = ["Choose a sprint or backlog, not both."] });
        Sprint? target = null;
        if (request.MoveUnfinishedTo is { } targetId) { target = await db.Sprints.FirstOrDefaultAsync(x => x.Id == targetId && x.TeamId == sprint.TeamId && x.State != SprintState.Completed, ct); if (target is null) return Results.ValidationProblem(new Dictionary<string, string[]> { ["moveUnfinishedTo"] = ["Choose an open sprint for the same team."] }); }
        var now = clock.GetUtcNow(); await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (target is null && !request.MoveUnfinishedToBacklog && sprint.AutoCreateNext)
        {
            var startsOn = sprint.EndsOn;
            target = await db.Sprints.FirstOrDefaultAsync(x => x.TeamId == sprint.TeamId && x.StartsOn == startsOn && x.State != SprintState.Completed, ct);
            if (target is null)
            {
                target = new Sprint { OrganizationId = sprint.OrganizationId, TeamId = sprint.TeamId, Name = $"{team.Key} {startsOn:yyyy-MM-dd}", StartsOn = startsOn, EndsOn = startsOn.AddDays(team.SprintLengthDays), State = SprintState.Planned, AutoCreateNext = true, CreatedAt = now };
                db.Sprints.Add(target);
            }
        }
        var completedStates = await db.WorkflowStates.Where(x => x.Category == WorkflowStateCategory.Completed).Select(x => x.Id).ToListAsync(ct);
        var unfinished = await db.Items.Where(x => x.SprintId == sprint.Id && !completedStates.Contains(x.StateId)).ToListAsync(ct);
        foreach (var item in unfinished) { db.SprintScopeLog.Add(new SprintScopeLog { OrganizationId = item.OrganizationId, SprintId = sprint.Id, ItemId = item.Id, Change = SprintScopeChange.Removed, Points = item.Points, RemainingHours = item.RemainingHours, At = now }); item.SprintId = target?.Id; if (target is not null) db.SprintScopeLog.Add(new SprintScopeLog { OrganizationId = item.OrganizationId, SprintId = target.Id, ItemId = item.Id, Change = SprintScopeChange.Added, Points = item.Points, RemainingHours = item.RemainingHours, At = now }); }
        sprint.State = SprintState.Completed; sprint.CompletedAt = now; sprint.Complete(); sprint.Changed(team.ProjectId, user.UserId!);
        await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
        return Results.Ok((await ViewsAsync(db, [sprint], ct))[0]);
    }

    private static async Task<IResult> Capacity(Guid sprintId, WorkItemsDbContext db, TenancyDbContext tenancy,
        IProjectAccess access, ICurrentUser user, IUserDirectory directory, CancellationToken ct)
    {
        var sprint = await db.Sprints.AsNoTracking().FirstOrDefaultAsync(x => x.Id == sprintId, ct);
        if (sprint is null) return Results.NotFound();
        var team = await tenancy.Teams.AsNoTracking().FirstOrDefaultAsync(x => x.Id == sprint.TeamId, ct);
        if (team is null || await access.GetProjectRoleAsync(user.UserId!, team.ProjectId, ct) is null) return Results.NotFound();
        return Results.Ok(await CapacityViewAsync(db, tenancy, directory, sprint, team, ct));
    }

    private static async Task<IResult> UpdateCapacity(Guid sprintId, HttpContext http, UpdateSprintCapacityRequest request,
        WorkItemsDbContext db, TenancyDbContext tenancy, IProjectAccess access, ICurrentUser user,
        IUserDirectory directory, ICurrentTenant tenant, CancellationToken ct)
    {
        var sprint = await db.Sprints.FirstOrDefaultAsync(x => x.Id == sprintId, ct);
        if (sprint is null) return Results.NotFound();
        var team = await tenancy.Teams.AsNoTracking().FirstOrDefaultAsync(x => x.Id == sprint.TeamId, ct);
        if (team is null || await access.GetProjectRoleAsync(user.UserId!, team.ProjectId, ct) is not { } role || !role.Satisfies(ProjectRole.Member)) return Results.NotFound();
        if (await WriteRefusalAsync(http, tenancy, team.ProjectId, ct) is { } readOnly) return readOnly;

        var input = request.Members ?? [];
        var errors = new Dictionary<string, string[]>();
        if (input.Any(x => string.IsNullOrWhiteSpace(x.UserId))) errors["members"] = ["Every capacity entry needs a userId."];
        if (input.GroupBy(x => x.UserId, StringComparer.Ordinal).Any(x => x.Count() > 1)) errors["members"] = ["Each user may appear only once."];
        if (input.Any(x => x.HoursPerDay is < 0 or > 24)) errors["hoursPerDay"] = ["A working day is between 0 and 24 hours."];
        if (input.Any(x => x.DaysOff < 0 || x.DaysOff > WorkingDayCount(sprint, team))) errors["daysOff"] = ["Days off must be between zero and the sprint's working days."];
        var userIds = input.Select(x => x.UserId!.Trim()).ToHashSet(StringComparer.Ordinal);
        var members = await tenancy.TeamMembers.Where(x => x.TeamId == sprint.TeamId).ToListAsync(ct);
        if (userIds.Except(members.Select(x => x.UserId), StringComparer.Ordinal).Any()) errors["members"] = ["Capacity can only be set for team members."];
        if (errors.Count > 0) return Results.ValidationProblem(errors);

        var existing = await db.SprintCapacities.Where(x => x.SprintId == sprintId).ToListAsync(ct);
        foreach (var capacity in existing.Where(x => !userIds.Contains(x.UserId))) db.SprintCapacities.Remove(capacity);
        foreach (var entry in input)
        {
            var userId = entry.UserId!.Trim();
            var capacity = existing.FirstOrDefault(x => x.UserId == userId);
            if (capacity is null)
            {
                db.SprintCapacities.Add(new SprintCapacity { OrganizationId = tenant.OrganizationId!.Value, SprintId = sprintId, UserId = userId, HoursPerDay = entry.HoursPerDay, DaysOff = entry.DaysOff });
            }
            else
            {
                capacity.HoursPerDay = entry.HoursPerDay;
                capacity.DaysOff = entry.DaysOff;
            }
        }
        await db.SaveChangesAsync(ct);
        return Results.Ok(await CapacityViewAsync(db, tenancy, directory, sprint, team, ct));
    }

    private static async Task<SprintCapacityView> CapacityViewAsync(WorkItemsDbContext db, TenancyDbContext tenancy,
        IUserDirectory directory, Sprint sprint, Aictiq.Modules.Tenancy.Domain.Team team, CancellationToken ct)
    {
        var members = await tenancy.TeamMembers.AsNoTracking().Where(x => x.TeamId == sprint.TeamId).ToListAsync(ct);
        var capacities = await db.SprintCapacities.AsNoTracking().Where(x => x.SprintId == sprint.Id).ToDictionaryAsync(x => x.UserId, ct);
        var contacts = await directory.GetAsync([.. members.Select(x => x.UserId)], ct);
        var remaining = await db.Items.AsNoTracking().Where(x => x.SprintId == sprint.Id && x.AssigneeId != null)
            .GroupBy(x => x.AssigneeId!).Select(x => new { UserId = x.Key, Hours = x.Sum(y => y.RemainingHours ?? 0) }).ToDictionaryAsync(x => x.UserId, x => x.Hours, ct);
        var workingDays = WorkingDayCount(sprint, team);
        var rows = members.Select(member =>
        {
            var contact = contacts.GetValueOrDefault(member.UserId);
            var isAgent = contact?.IsAgent ?? false;
            var saved = capacities.GetValueOrDefault(member.UserId);
            // Eight hours is the team's normal human day until a per-member or per-sprint
            // value says otherwise. Agents never inherit it: their zero default prevents
            // a roster of automations from inflating a team's forecast.
            var hoursPerDay = saved?.HoursPerDay ?? member.CapacityHoursPerDay ?? (isAgent ? 0 : 8);
            var daysOff = saved?.DaysOff ?? 0;
            var available = Math.Max(0, workingDays - daysOff) * hoursPerDay;
            var assigned = remaining.GetValueOrDefault(member.UserId);
            return new SprintCapacityMemberView(member.UserId, contact?.DisplayName ?? member.UserId, isAgent,
                hoursPerDay, daysOff, workingDays, available, assigned, available == 0 ? (assigned == 0 ? 0 : 100) : Math.Round(assigned / available * 100, 1));
        }).OrderBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
        var totalCapacity = rows.Sum(x => x.CapacityHours);
        var assignedHours = rows.Sum(x => x.AssignedRemainingHours);
        return new SprintCapacityView(sprint.Id, workingDays, totalCapacity, assignedHours,
            totalCapacity == 0 ? (assignedHours == 0 ? 0 : 100) : Math.Round(assignedHours / totalCapacity * 100, 1), rows);
    }

    private static int WorkingDayCount(Sprint sprint, Aictiq.Modules.Tenancy.Domain.Team team) =>
        Enumerable.Range(0, sprint.EndsOn.DayNumber - sprint.StartsOn.DayNumber)
            .Count(offset => team.WorkingDays.Contains((int)sprint.StartsOn.AddDays(offset).DayOfWeek));

    internal static Task LogScopeAsync(WorkItemsDbContext db, WorkItem item, Guid? oldSprintId, Guid? newSprintId, DateTimeOffset now, CancellationToken ct)
    { if (oldSprintId == newSprintId) return Task.CompletedTask; if (oldSprintId is { } oldId) { db.SprintScopeLog.Add(new SprintScopeLog { OrganizationId = item.OrganizationId, SprintId = oldId, ItemId = item.Id, Change = SprintScopeChange.Removed, Points = item.Points, RemainingHours = item.RemainingHours, At = now }); item.SprintScopeChanged(oldId, false); } if (newSprintId is { } newId) { db.SprintScopeLog.Add(new SprintScopeLog { OrganizationId = item.OrganizationId, SprintId = newId, ItemId = item.Id, Change = SprintScopeChange.Added, Points = item.Points, RemainingHours = item.RemainingHours, At = now }); item.SprintScopeChanged(newId, true); } return Task.CompletedTask; }
    private static IResult? Invalid(string? name, DateOnly starts, DateOnly ends) => string.IsNullOrWhiteSpace(name) || name.Trim().Length > 100 ? Results.ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["A sprint name of 1-100 characters is required."] }) : ends <= starts ? Results.ValidationProblem(new Dictionary<string, string[]> { ["endsOn"] = ["The end date must be after the start date."] }) : null;
    private static IResult Conflict() => Results.Problem("The sprint was modified by someone else.", statusCode: 409);
    private static async Task<List<SprintView>> ViewsAsync(WorkItemsDbContext db, IReadOnlyList<Sprint> sprints, CancellationToken ct) { if (sprints.Count == 0) return []; var ids = sprints.Select(x => x.Id).ToArray(); var items = await db.Items.AsNoTracking().Where(x => x.SprintId != null && ids.Contains(x.SprintId.Value)).Select(x => new { x.SprintId, x.Points, x.RemainingHours, x.StateId }).ToListAsync(ct); var done = await db.WorkflowStates.AsNoTracking().Where(x => x.Category == WorkflowStateCategory.Completed).Select(x => x.Id).ToListAsync(ct); var today = DateOnly.FromDateTime(DateTime.UtcNow); return sprints.Select(s => { var scoped = items.Where(x => x.SprintId == s.Id).ToList(); return new SprintView(s.Id, s.TeamId, s.Name, s.Goal, s.StartsOn, s.EndsOn, s.State, s.AutoCreateNext, Math.Max(0, s.EndsOn.DayNumber - today.DayNumber), new SprintProgress(scoped.Count, scoped.Count(x => done.Contains(x.StateId)), scoped.Sum(x => x.Points ?? 0), scoped.Where(x => done.Contains(x.StateId)).Sum(x => x.Points ?? 0), scoped.Sum(x => x.RemainingHours ?? 0)), s.Version); }).ToList(); }
}

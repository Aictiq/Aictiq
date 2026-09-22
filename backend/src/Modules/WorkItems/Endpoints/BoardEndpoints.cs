using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Aictiq.Modules.Tenancy;
using Aictiq.Modules.Tenancy.Domain;
using Aictiq.Modules.WorkItems.Domain;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Tenancy;

namespace Aictiq.Modules.WorkItems.Endpoints;

public sealed record BoardColumnView(Guid Id, string Name, IReadOnlyList<Guid> StateIds, BoardGeneralState GeneralState, int? WipLimit, int Count, bool WipExceeded, IReadOnlyList<WorkItemView> Cards);
public sealed record BoardView(Guid Id, Guid TeamId, BoardKind Kind, string Swimlane, IReadOnlyList<string> CardFields, IReadOnlyList<WorkItemType> Types, IReadOnlyList<BoardColumnView> Columns, uint Version);
public sealed record UpdateBoardRequest(IReadOnlyList<BoardColumnConfig>? Columns, string? Swimlane, IReadOnlyList<string>? CardFields, IReadOnlyList<WorkItemType>? Types, uint Version);
public sealed record TaskboardCellView(string Key, string Name, WorkflowStateCategory? Category, decimal RemainingHours, IReadOnlyList<WorkItemView> Tasks);
public sealed record TaskboardRowView(string? ParentKey, string Name, decimal RemainingHours, IReadOnlyList<TaskboardCellView> Cells);
public sealed record TaskboardView(Guid SprintId, IReadOnlyList<TaskboardRowView> Rows, IReadOnlyList<TaskboardCellView> UnparentedTasks);

public static class BoardEndpoints
{
    private static readonly HashSet<string> Swimlanes = new(StringComparer.OrdinalIgnoreCase) { "none", "epic", "feature", "assignee", "priority" };

    public static IEndpointRouteBuilder MapBoardEndpoints(this IEndpointRouteBuilder api)
    {
        var teams = api.MapGroup("/orgs/{orgSlug}/teams").WithTags("Boards").RequireAuthorization();
        teams.MapGet("/{teamId:guid}/board", Get).RequireOrgRole(OrgRole.Guest).RequireScope(Scopes.Read);
        teams.MapPut("/{teamId:guid}/board", Put).RequireOrgRole(OrgRole.Member).RequireScope(Scopes.Write);
        var sprints = api.MapGroup("/orgs/{orgSlug}/sprints").WithTags("Boards").RequireAuthorization();
        sprints.MapGet("/{sprintId:guid}/taskboard", Taskboard).RequireOrgRole(OrgRole.Guest).RequireScope(Scopes.Read);
        return api;
    }

    internal const int DefaultColumnTake = 50;
    internal const int MaxColumnTake = 500;

    // Columns are paged, not the board: every matching card's placement is read as a narrow
    // projection (so counts and WIP stay exact), but full cards - labels, rollups, watchers -
    // are built only for the first `take` of each column in rank order. `expand` lets one
    // column grow ("colId:n,colId:n") without re-paging the others.
    private static async Task<IResult> Get(Guid teamId, string? filter, string? q, string? assigneeIds, int? take, string? expand, WorkItemsDbContext db, TenancyDbContext tenancy, IProjectAccess access, IUserDirectory directory, ICurrentUser user, ICurrentTenant tenant, CancellationToken ct)
    {
        var team = await VisibleTeam(teamId, tenancy, access, user, ct); if (team is null) return Results.NotFound();
        var board = await GetOrCreateAsync(db, tenant.OrganizationId!.Value, team, ct);
        var parsed = ItemFilter.Parse(filter); if (parsed.Error is not null) return Results.ValidationProblem(new Dictionary<string, string[]> { ["filter"] = [parsed.Error] });
        if (take is < 0 or > MaxColumnTake) return Results.ValidationProblem(new Dictionary<string, string[]> { ["take"] = [$"Use a number from 0 to {MaxColumnTake}."] });
        var expanded = ParseExpand(expand); if (expanded is null) return Results.ValidationProblem(new Dictionary<string, string[]> { ["expand"] = [$"Use a comma-separated list of columnId:count, each count from 0 to {MaxColumnTake}."] });
        var config = board.Config;
        var types = config.Types is { Count: > 0 } ? config.Types : [WorkItemType.Story, WorkItemType.Bug];
        var (query, filterError) = await ItemQueries.ApplyFilterAsync(db.Items.AsNoTracking().Where(x => x.TeamId == teamId && types.Contains(x.Type)), parsed, team.ProjectId, db, directory, user.UserId, ct);
        if (filterError is not null) return Results.ValidationProblem(new Dictionary<string, string[]> { ["filter"] = [filterError] });
        query = await ItemQueries.ApplySearchAsync(query, q, team.ProjectId, db, ct);
        var assignees = assigneeIds?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (assignees is { Length: > 0 }) query = query.Where(x => x.AssigneeId != null && assignees.Contains(x.AssigneeId));
        var placements = await query.OrderBy(x => x.Rank).ThenBy(x => x.Number).Select(x => new { x.Id, x.StateId, x.BoardColumnId }).ToListAsync(ct);

        var idsByColumn = config.Columns.ToDictionary(x => x.Id, _ => new List<Guid>());
        foreach (var placement in placements)
            if (ColumnFor(board, placement.StateId, placement.BoardColumnId) is { } column) idsByColumn[column.Id].Add(placement.Id);
        var counts = idsByColumn.ToDictionary(x => x.Key, x => x.Value.Count);
        var pageIds = idsByColumn.SelectMany(x => x.Value.Take(expanded.GetValueOrDefault(x.Key, take ?? DefaultColumnTake))).ToList();
        var loaded = await db.Items.AsNoTracking().Where(x => pageIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        var items = pageIds.Select(id => loaded.GetValueOrDefault(id)).OfType<WorkItem>().ToList();

        var columnStateIds = config.Columns.SelectMany(x => x.StateIds).Distinct().ToArray();
        var states = await db.WorkflowStates.AsNoTracking().Where(x => columnStateIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        foreach (var (id, state) in await WorkItemEndpoints.StatesAsync(db, items, ct)) states.TryAdd(id, state);
        var cards = await WorkItemEndpoints.ViewsAsync(db, user.UserId!, items, states, ct);
        return Results.Ok(ToView(board, items, cards, states, counts));
    }

    private static async Task<IResult> Put(Guid teamId, UpdateBoardRequest request, HttpContext http, WorkItemsDbContext db, TenancyDbContext tenancy, IProjectAccess access, ICurrentUser user, ICurrentTenant tenant, CancellationToken ct)
    {
        var team = await VisibleTeam(teamId, tenancy, access, user, ct); if (team is null) return Results.NotFound();
        if (!await CanManageAsync(team, tenancy, access, user, ct)) return Results.Forbid();
        if (await SprintEndpoints.WriteRefusalAsync(http, tenancy, team.ProjectId, ct) is { } readOnly) return readOnly;
        var board = await GetOrCreateAsync(db, tenant.OrganizationId!.Value, team, ct);
        if (board.Version != request.Version) return Results.Problem("The board was modified by someone else.", type: ProblemTypes.Conflict, statusCode: 409);
        var config = new BoardConfig(NormalizeColumnIds(request.Columns ?? board.Config.Columns), request.Swimlane ?? board.Config.Swimlane,
            request.CardFields ?? board.Config.CardFields, request.Types ?? board.Config.Types);
        if (await ValidateConfigAsync(db, team.ProjectId, config, ct) is { } errors) return Results.ValidationProblem(errors);
        db.Entry(board).Property(x => x.Version).OriginalValue = request.Version; board.Config = config;
        try { await db.SaveChangesAsync(ct); } catch (DbUpdateConcurrencyException) { return Results.Problem("The board was modified by someone else.", type: ProblemTypes.Conflict, statusCode: 409); }
        var stateIds = config.Columns.SelectMany(x => x.StateIds).Distinct().ToArray();
        var states = await db.WorkflowStates.Where(x => stateIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        return Results.Ok(ToView(board, [], [], states));
    }

    private static async Task<IResult> Taskboard(Guid sprintId, string? stateIds, WorkItemsDbContext db, TenancyDbContext tenancy, IProjectAccess access, ICurrentUser user, CancellationToken ct)
    {
        var sprint = await db.Sprints.AsNoTracking().FirstOrDefaultAsync(x => x.Id == sprintId, ct); if (sprint is null) return Results.NotFound();
        var team = await VisibleTeam(sprint.TeamId, tenancy, access, user, ct); if (team is null) return Results.NotFound();
        var requested = ParseStateIds(stateIds); if (requested is null && stateIds is not null) return Results.ValidationProblem(new Dictionary<string, string[]> { ["stateIds"] = ["Use a comma-separated list of state ids."] });
        var all = await db.Items.AsNoTracking().Where(x => x.SprintId == sprintId && (x.Type == WorkItemType.Story || x.Type == WorkItemType.Bug || x.Type == WorkItemType.Task)).OrderBy(x => x.Rank).ThenBy(x => x.Number).ToListAsync(ct);
        var states = await WorkItemEndpoints.StatesAsync(db, all, ct);
        if (requested is { Count: > 0 } && requested.Any(id => !states.ContainsKey(id))) return Results.ValidationProblem(new Dictionary<string, string[]> { ["stateIds"] = ["One or more states do not belong to this taskboard."] });
        var views = (await WorkItemEndpoints.ViewsAsync(db, user.UserId!, all, states, ct)).ToDictionary(x => x.Id);
        var parents = all.Where(x => x.Type is WorkItemType.Story or WorkItemType.Bug).ToList();
        var tasks = all.Where(x => x.Type == WorkItemType.Task).ToList();
        var grouped = tasks.Where(x => x.ParentId is not null).GroupBy(x => x.ParentId!.Value).ToDictionary(g => g.Key, g => g.ToList());
        var rows = parents.Select(parent => new TaskboardRowView(parent.Key, parent.Title, grouped.GetValueOrDefault(parent.Id, []).Sum(x => x.RemainingHours ?? 0), Cells(grouped.GetValueOrDefault(parent.Id, []), views, states, requested))).ToList();
        var parentIds = parents.Select(x => x.Id).ToHashSet();
        var unparented = Cells(tasks.Where(x => x.ParentId is null || !parentIds.Contains(x.ParentId.Value)).ToList(), views, states, requested);
        return Results.Ok(new TaskboardView(sprint.Id, rows, unparented));
    }

    internal static async Task<Board?> FindAsync(WorkItemsDbContext db, Guid teamId, CancellationToken ct)
    {
        var board = await db.Boards.FirstOrDefaultAsync(x => x.TeamId == teamId && x.Kind == BoardKind.Kanban, ct);
        if (board is not null && EnsureColumnIds(board)) await db.SaveChangesAsync(ct);
        return board;
    }
    internal static bool ContainsState(Board board, Guid stateId) => board.Config.Columns.Any(x => x.StateIds.Contains(stateId));
    internal static BoardColumnConfig? ColumnFor(Board board, Guid stateId) => board.Config.Columns.FirstOrDefault(x => x.StateIds.Contains(stateId));
    internal static BoardColumnConfig? ColumnFor(Board board, Guid? columnId) => columnId is null ? null : board.Config.Columns.FirstOrDefault(x => x.Id == columnId);
    internal static async Task<bool> CanManageAsync(Team team, TenancyDbContext tenancy, IProjectAccess access, ICurrentUser user, CancellationToken ct) =>
        await access.GetProjectRoleAsync(user.UserId!, team.ProjectId, ct) is { } role && role.Satisfies(ProjectRole.Admin)
        || await tenancy.TeamMembers.AnyAsync(x => x.TeamId == team.Id && x.UserId == user.UserId && x.IsLead, ct);

    private static async Task<Team?> VisibleTeam(Guid teamId, TenancyDbContext tenancy, IProjectAccess access, ICurrentUser user, CancellationToken ct)
    { var team = await tenancy.Teams.AsNoTracking().FirstOrDefaultAsync(x => x.Id == teamId, ct); return team is not null && await access.GetProjectRoleAsync(user.UserId!, team.ProjectId, ct) is not null ? team : null; }
    internal static async Task<Board> GetOrCreateAsync(WorkItemsDbContext db, Guid orgId, Team team, CancellationToken ct)
    {
        var board = await FindAsync(db, team.Id, ct); if (board is not null) return board;
        var states = await db.WorkflowStates.Where(x => db.Workflows.Any(w => w.Id == x.WorkflowId && w.ProjectId == team.ProjectId)).OrderBy(x => x.Position).ToListAsync(ct);
        board = new Board { OrganizationId = orgId, TeamId = team.Id, Kind = BoardKind.Kanban, Config = new BoardConfig(states.Select(s => new BoardColumnConfig(s.Name, [s.Id], null, Guid.NewGuid(), GeneralState: GeneralStateFor(s.Category))).ToList()) };
        db.Boards.Add(board); await db.SaveChangesAsync(ct); return board;
    }
    private static IReadOnlyList<BoardColumnConfig> NormalizeColumnIds(IReadOnlyList<BoardColumnConfig> columns) => columns.Select(c => c.Id == Guid.Empty ? c with { Id = Guid.NewGuid() } : c).ToList();
    private static bool EnsureColumnIds(Board board)
    {
        var columns = NormalizeColumnIds(board.Config.Columns);
        if (columns.Zip(board.Config.Columns).All(pair => pair.First.Id == pair.Second.Id)) return false;
        board.Config = board.Config with { Columns = columns };
        return true;
    }
    internal static BoardColumnConfig? ColumnFor(WorkItem item, Board board) => ColumnFor(board, item.StateId, item.BoardColumnId);
    private static BoardColumnConfig? ColumnFor(Board board, Guid stateId, Guid? columnId)
    {
        var assigned = ColumnFor(board, columnId);
        return assigned is not null && assigned.StateIds.Contains(stateId) ? assigned : ColumnFor(board, stateId);
    }
    private static BoardGeneralState GeneralStateFor(WorkflowStateCategory category) => category switch
    {
        WorkflowStateCategory.Proposed => BoardGeneralState.New,
        WorkflowStateCategory.Completed or WorkflowStateCategory.Removed => BoardGeneralState.Done,
        _ => BoardGeneralState.Doing,
    };
    // Cards are assigned to one board column. Pre-column assignments (existing data) fall
    // back to the first compatible column once, avoiding the old duplicate-card rendering.
    private static BoardView ToView(Board board, IReadOnlyList<WorkItem> items, IReadOnlyList<WorkItemView> views, IReadOnlyDictionary<Guid, WorkflowState> states, IReadOnlyDictionary<Guid, int>? counts = null)
    {
        var viewById = views.ToDictionary(x => x.Id);
        var cardsByColumn = board.Config.Columns.ToDictionary(x => x.Id, _ => new List<WorkItemView>());
        foreach (var item in items)
            if (ColumnFor(item, board) is { } column && viewById.TryGetValue(item.Id, out var view)) cardsByColumn[column.Id].Add(view);
        return new BoardView(board.Id, board.TeamId, board.Kind, board.Config.Swimlane, board.Config.CardFields ?? [], board.Config.Types ?? [WorkItemType.Story, WorkItemType.Bug], board.Config.Columns.Select(c =>
        {
            var cards = cardsByColumn[c.Id]; var count = counts?.GetValueOrDefault(c.Id) ?? cards.Count; var generalState = c.GeneralState ?? (states.TryGetValue(c.StateIds.FirstOrDefault(), out var state) ? GeneralStateFor(state.Category) : BoardGeneralState.Doing);
            return new BoardColumnView(c.Id, c.Name, c.StateIds, generalState, c.WipLimit, count, c.WipLimit is { } limit && count > limit, cards);
        }).ToList(), board.Version);
    }
    private static async Task<Dictionary<string, string[]>?> ValidateConfigAsync(WorkItemsDbContext db, Guid projectId, BoardConfig config, CancellationToken ct)
    { var errors = new Dictionary<string, string[]>(); if (config.Columns.Count == 0) errors["columns"] = ["At least one column is required."]; if (!Swimlanes.Contains(config.Swimlane)) errors["swimlane"] = ["Use none, epic, feature, assignee, or priority."]; if (config.Columns.Any(x => string.IsNullOrWhiteSpace(x.Name) || x.StateIds.Count == 0 || x.WipLimit < 1)) errors["columns"] = ["Columns need a name, one or more states, and a positive WIP limit."];
        var ids = config.Columns.SelectMany(x => x.StateIds).ToArray(); var distinctIds = ids.Distinct().ToArray(); if (config.Columns.Any(x => x.Id == Guid.Empty) || config.Columns.Select(x => x.Id).Distinct().Count() != config.Columns.Count || config.Columns.Any(x => x.StateIds.Distinct().Count() != x.StateIds.Count) || await db.WorkflowStates.CountAsync(x => distinctIds.Contains(x.Id) && db.Workflows.Any(w => w.Id == x.WorkflowId && w.ProjectId == projectId), ct) != distinctIds.Length) errors["columns"] = ["Each column needs a unique id, and each state must belong to this project and appear only once in that column."];
        if (config.Types is { Count: > 0 } && config.Types.Any(x => x is not (WorkItemType.Story or WorkItemType.Bug))) errors["types"] = ["Boards may show Stories and Bugs."]; return errors.Count == 0 ? null : errors; }
    private static Dictionary<Guid, int>? ParseExpand(string? raw)
    {
        var result = new Dictionary<Guid, int>();
        foreach (var entry in (raw ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = entry.Split(':');
            if (parts.Length != 2 || !Guid.TryParse(parts[0], out var columnId) || !int.TryParse(parts[1], out var count) || count is < 0 or > MaxColumnTake) return null;
            result[columnId] = count;
        }
        return result;
    }
    private static List<Guid>? ParseStateIds(string? raw) => raw is null ? [] : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => Guid.TryParse(x, out var id) ? (Guid?)id : null).All(x => x is not null) ? raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Guid.Parse).ToList() : null;
    private static IReadOnlyList<TaskboardCellView> Cells(List<WorkItem> tasks, IReadOnlyDictionary<Guid, WorkItemView> views, IReadOnlyDictionary<Guid, WorkflowState> states, List<Guid>? explicitStates)
    { IEnumerable<(string Key, string Name, WorkflowStateCategory? Category, IEnumerable<WorkItem> Tasks)> groups = explicitStates is { Count: > 0 } ? explicitStates.Select(id => (id.ToString(), states[id].Name, (WorkflowStateCategory?)null, tasks.Where(x => x.StateId == id))) : [ ("proposed", "Proposed", (WorkflowStateCategory?)WorkflowStateCategory.Proposed, tasks.Where(x => states[x.StateId].Category == WorkflowStateCategory.Proposed)), ("active", "Active", (WorkflowStateCategory?)WorkflowStateCategory.Active, tasks.Where(x => states[x.StateId].Category is WorkflowStateCategory.Active or WorkflowStateCategory.Resolved)), ("completed", "Completed", (WorkflowStateCategory?)WorkflowStateCategory.Completed, tasks.Where(x => states[x.StateId].Category == WorkflowStateCategory.Completed)) ]; return groups.Select(g => { var values = g.Tasks.Select(x => views[x.Id]).ToList(); return new TaskboardCellView(g.Key, g.Name, g.Category, values.Sum(x => x.RemainingHours ?? 0), values); }).ToList(); }
}

using System.Text.Json;
using System.Text.RegularExpressions;
using Markdig;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Aictiq.Modules.WorkItems.Domain;
using Aictiq.Modules.WorkItems.Contracts;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Paging;
using Aictiq.SharedKernel.Outbox;
using Aictiq.SharedKernel.Persistence;
using Aictiq.Modules.Tenancy;

namespace Aictiq.Modules.WorkItems.Endpoints;

public sealed record ItemRollup(int TotalCount, int CompletedCount, decimal PointsTotal, decimal PointsCompleted, decimal RemainingHours);
public sealed record WorkItemSummaryView(string Key, string Title, WorkflowStateCategory StateCategory, string? AssigneeId);
/// <param name="DueDate">
/// Read back so a client can echo it. <c>PATCH</c> assigns the nullable fields absolutely
/// — an omitted <c>dueDate</c> clears the stored one — so a field the view withholds is a
/// field every update silently erases.
/// </param>
public sealed record WorkItemView(Guid Id, string Key, WorkItemType Type, string Title, string DescriptionMarkdown, string DescriptionHtml, Guid StateId, Guid? BoardColumnId, WorkflowStateCategory StateCategory, WorkItemPriority Priority, string? AssigneeId, Guid? TeamId, Guid? SprintId, Guid? ParentId, decimal? Points, decimal? EstimateHours, decimal? RemainingHours, decimal? CompletedHours, DateOnly? DueDate, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, uint Version, ItemRollup Rollup, IReadOnlyList<ItemLabelView> Labels, bool Blocked, bool IsWatching, int WatcherCount, string? ClaimedBy, DateTimeOffset? ClaimedAt, DateTimeOffset? ClaimHeartbeatAt);
public sealed record CreateWorkItemRequest(WorkItemType Type, string? Title, string? DescriptionMarkdown, Guid? StateId, WorkItemPriority? Priority, string? AssigneeId, Guid? TeamId, Guid? ParentId, decimal? Points, decimal? EstimateHours, decimal? RemainingHours, decimal? CompletedHours, DateOnly? DueDate, IReadOnlyList<Guid>? LabelIds);
public sealed record UpdateWorkItemRequest(WorkItemType? Type, string? Title, string? DescriptionMarkdown, Guid? StateId, WorkItemPriority? Priority, string? AssigneeId, Guid? TeamId, Guid? ParentId, decimal? Points, decimal? EstimateHours, decimal? RemainingHours, decimal? CompletedHours, DateOnly? DueDate, IReadOnlyList<Guid>? LabelIds, uint Version);
public sealed record TransitionRequest(Guid ToStateId, uint Version);
public sealed record ClaimRequest(uint Version);
public sealed record LogTimeRequest(decimal Hours, uint Version);
public sealed record ReparentRequest(string? ParentKey, uint Version);
public sealed record MoveItemRequest(string? AfterKey, string? BeforeKey, Guid? SprintId, Guid? TeamId, string? ParentKey, uint Version, bool RemoveSprint = false);
public sealed record BoardMoveRequest(Guid ToStateId, string? AfterKey, uint Version, bool Force = false, Guid? ToColumnId = null);
public sealed record BacklogNode(WorkItemView Item, IReadOnlyList<BacklogNode> Children);
/// <summary>
/// Each section is paged independently; the list holds what was loaded and the matching
/// <c>*Count</c> is the whole section, so "is there more" is <c>list.Count &lt; count</c>.
/// </summary>
public sealed record TeamBacklogView(IReadOnlyList<WorkItemView> CurrentSprint, IReadOnlyList<WorkItemView> NextSprint, IReadOnlyList<WorkItemView> Backlog, int CurrentSprintCount, int NextSprintCount, int BacklogCount);
public sealed record BulkItemSet(Guid? StateId, string? AssigneeId, WorkItemPriority? Priority, Guid? TeamId,
    Guid? SprintId, IReadOnlyList<Guid>? AddLabels, IReadOnlyList<Guid>? RemoveLabels);
public sealed record BulkUpdateItemsRequest(IReadOnlyList<string>? Keys, BulkItemSet? Set, IReadOnlyDictionary<string, uint>? Versions);
public sealed record BulkItemResult(string Key, int Status, string? Detail, WorkItemView? Item);
public sealed record BulkUpdateItemsResponse(IReadOnlyList<BulkItemResult> Results);
public sealed record ItemHistoryChangeView(Guid Id, string Field, JsonElement OldValue, JsonElement NewValue);
public sealed record ItemHistoryEventView(Guid EventId, string ItemKey, UserSummary? Actor, DateTimeOffset At, IReadOnlyList<ItemHistoryChangeView> Changes);
/// <param name="Descendants">Every item below the one being deleted, in tree order; <c>Depth</c> 1 is a direct child.</param>
public sealed record ItemDeletePreviewView(IReadOnlyList<ItemDeletePreviewItem> Descendants, int Comments, int Attachments, int Links, int Relations);
public sealed record ItemDeletePreviewItem(string Key, string Title, WorkItemType Type, int Depth);
internal sealed record HistoryRow(Guid Id, Guid EventId, Guid ItemId, string ItemKey, string ActorId, DateTimeOffset At, string Field, string? OldValue, string? NewValue);

public static class WorkItemEndpoints
{
    // DisableHtml: raw HTML in a description or comment is rendered as text, never as
    // markup, so the only tags in DescriptionHtml are the ones Markdig itself emits. The
    // regex pass below is kept as a second line for the link and image attributes Markdig
    // does write. Before this an <img onerror=…> with an attachment src, or an unquoted
    // href=javascript:, walked past a whitelist that only knew the quoted forms.
    private static readonly MarkdownPipeline Markdown = new MarkdownPipelineBuilder().UseAdvancedExtensions().DisableHtml().Build();

    public static IEndpointRouteBuilder MapWorkItemEndpoints(this IEndpointRouteBuilder api)
    {
        var projects = api.MapGroup("/orgs/{orgSlug}/projects/{projectKey}/items").WithTags("Work items").RequireAuthorization();
        projects.MapGet("/", List).RequireProjectRole(ProjectRole.Guest).RequireScope(Scopes.Read);
        projects.MapPost("/", Create).RequireProjectRole(ProjectRole.Member).RequireProjectWritable().RequireScope(Scopes.Write);
        projects.MapPost("/bulk", BulkUpdate).RequireProjectRole(ProjectRole.Member).RequireProjectWritable().RequireScope(Scopes.Write);
        projects.MapGet("/summary", Summary).RequireProjectRole(ProjectRole.Guest).RequireScope(Scopes.Read);
        var items = api.MapGroup("/orgs/{orgSlug}/items").WithTags("Work items").RequireAuthorization();
        items.MapGet("/{itemKey}", Get).RequireOrgRole(OrgRole.Guest).RequireScope(Scopes.Read);
        // No {projectKey} here, so RequireItemProjectWritable stands in for RequireProjectWritable.
        items.MapPatch("/{itemKey}", Update).RequireOrgRole(OrgRole.Member).RequireItemProjectWritable().RequireScope(Scopes.Write);
        items.MapDelete("/{itemKey}", Delete).RequireOrgRole(OrgRole.Member).RequireItemProjectWritable().RequireScope(Scopes.Write);
        items.MapGet("/{itemKey}/delete-preview", DeletePreview).RequireOrgRole(OrgRole.Member).RequireScope(Scopes.Read);
        items.MapPost("/{itemKey}/transition", Transition).RequireOrgRole(OrgRole.Member).RequireItemProjectWritable().RequireScope(Scopes.Write);
        items.MapPost("/{itemKey}/claim", Claim).RequireOrgRole(OrgRole.Member).RequireItemProjectWritable().RequireScope(Scopes.Write);
        items.MapPost("/{itemKey}/release", Release).RequireOrgRole(OrgRole.Member).RequireItemProjectWritable().RequireScope(Scopes.Write);
        items.MapPost("/{itemKey}/heartbeat", Heartbeat).RequireOrgRole(OrgRole.Member).RequireItemProjectWritable().RequireScope(Scopes.Write);
        items.MapPost("/{itemKey}/log-time", LogTime).RequireOrgRole(OrgRole.Member).RequireItemProjectWritable().RequireScope(Scopes.Write);
        items.MapPost("/{itemKey}/reparent", Reparent).RequireOrgRole(OrgRole.Member).RequireItemProjectWritable().RequireScope(Scopes.Write);
        items.MapPost("/{itemKey}/move", Move).RequireOrgRole(OrgRole.Member).RequireItemProjectWritable().RequireScope(Scopes.Write);
        items.MapPost("/{itemKey}/board-move", BoardMove).RequireOrgRole(OrgRole.Member).RequireItemProjectWritable().RequireScope(Scopes.Write);
        items.MapGet("/{itemKey}/children", Children).RequireOrgRole(OrgRole.Guest).RequireScope(Scopes.Read);
        items.MapGet("/{itemKey}/ancestors", Ancestors).RequireOrgRole(OrgRole.Guest).RequireScope(Scopes.Read);
        items.MapGet("/{itemKey}/history", History).RequireOrgRole(OrgRole.Guest).RequireScope(Scopes.Read);
        api.MapGroup("/orgs/{orgSlug}/projects/{projectKey}").WithTags("Work items").RequireAuthorization()
            .MapGet("/activity", ProjectActivity).RequireProjectRole(ProjectRole.Guest).RequireScope(Scopes.Read);
        api.MapGroup("/orgs/{orgSlug}/projects/{projectKey}").WithTags("Backlog").RequireAuthorization()
            .MapGet("/backlog", Backlog).RequireProjectRole(ProjectRole.Guest).RequireScope(Scopes.Read);
        api.MapGroup("/orgs/{orgSlug}/teams").WithTags("Backlog").RequireAuthorization()
            .MapGet("/{teamId:guid}/backlog", TeamBacklog).RequireOrgRole(OrgRole.Guest).RequireScope(Scopes.Read);
        return api;
    }

    private static async Task<IResult> Summary(HttpContext http, WorkItemsDbContext db, string? keys, CancellationToken ct)
    {
        var requested = (keys ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.ToUpperInvariant()).Distinct(StringComparer.OrdinalIgnoreCase).Take(200).ToArray();
        if (requested.Length == 0) return Results.Ok(Array.Empty<WorkItemSummaryView>());
        var projectId = http.ResolvedProjectId()!.Value;
        var items = await db.Items.AsNoTracking().Where(x => x.ProjectId == projectId)
            .Join(db.WorkflowStates.AsNoTracking(), item => item.StateId, state => state.Id, (item, state) => new { item, state })
            .ToListAsync(ct);
        return Results.Ok(items.Where(x => requested.Contains(x.item.Key, StringComparer.OrdinalIgnoreCase))
            .Select(x => new WorkItemSummaryView(x.item.Key, x.item.Title, x.state.Category, x.item.AssigneeId)));
    }

    private static async Task<IResult> List(HttpContext http, WorkItemsDbContext db, ICurrentUser user, IUserDirectory directory, string? filter, string? sort, string? q, int page = 1, int pageSize = 25, CancellationToken ct = default)
    {
        var projectId = http.ResolvedProjectId()!.Value; var parsed = ItemFilter.Parse(filter);
        if (parsed.Error is not null) return Results.ValidationProblem(new Dictionary<string, string[]> { ["filter"] = [parsed.Error] });
        var (query, error) = await ItemQueries.ApplyFilterAsync(db.Items.AsNoTracking().Where(x => x.ProjectId == projectId), parsed, projectId, db, directory, user.UserId, ct);
        if (error is not null) return Results.ValidationProblem(new Dictionary<string, string[]> { ["filter"] = [error] });
        query = await ItemQueries.ApplySearchAsync(query, q, projectId, db, ct);
        var pinned = SearchQuery.Normalize(q) is { } search ? SearchQuery.ItemNumber(search) : null;
        if (!ItemQueries.TrySort(query, sort, db, out query, out var sortError, pinned)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["sort"] = [sortError!] });
        var take = Math.Clamp(pageSize == 0 ? 25 : pageSize, 1, 100); var total = await query.CountAsync(ct);
        var rows = await query.Skip(Math.Max(0, page - 1) * take).Take(take).ToListAsync(ct);
        var states = await StatesAsync(db, rows, ct); return Results.Ok(new Aictiq.SharedKernel.Paging.PagedResult<WorkItemView>(await ViewsAsync(db, user.UserId!, rows, states, ct), Math.Max(1, page), take, total));
    }
    private static async Task<IResult> Backlog(HttpContext http, WorkItemsDbContext db, ICurrentUser user, string? level, CancellationToken ct)
    {
        if (level is not null && !Enum.TryParse<WorkItemType>(level, true, out _)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["level"] = ["level must be epic, feature, or story."] });
        var projectId = http.ResolvedProjectId()!.Value; var items = await db.Items.AsNoTracking().Where(x => x.ProjectId == projectId).OrderBy(x => x.Rank).ThenBy(x => x.Number).ToListAsync(ct);
        var views = (await ViewsAsync(db, user.UserId!, items, await StatesAsync(db, items, ct), ct)).ToDictionary(x => x.Id);
        IReadOnlyList<BacklogNode> Build(Guid? parentId) => items.Where(x => x.ParentId == parentId).Select(x => new BacklogNode(views[x.Id], Build(x.Id))).ToList();
        return Results.Ok(Build(null));
    }
    internal const int DefaultBacklogTake = 50;
    // Higher than a board column's cap: a sprint or the unplanned backlog is scrolled to its
    // end far more often than a column is, and a section that cannot be fully shown is a bug.
    internal const int MaxBacklogTake = 2000;
    private static readonly string[] BacklogSections = ["current", "next", "backlog"];

    // Sections are paged, not the backlog: every team item's (id, parent, sprint) is read as a
    // narrow projection so counts stay exact, but full views are built only for a page of each
    // section. A page is a prefix of the section's tree in display order (pre-order, siblings by
    // rank, orphans at root), so an item never arrives before its parent and jumps under it on
    // the next page. The page is returned in rank order, as before. `expand` grows one section
    // ("backlog:150") without re-paging the others.
    private static async Task<IResult> TeamBacklog(Guid teamId, int? take, string? expand, string? filter, string? q, WorkItemsDbContext db, TenancyDbContext tenancy, IProjectAccess access, IUserDirectory directory, ICurrentUser user, CancellationToken ct)
    {
        var team = await tenancy.Teams.AsNoTracking().FirstOrDefaultAsync(x => x.Id == teamId, ct); if (team is null || await access.GetProjectRoleAsync(user.UserId!, team.ProjectId, ct) is null) return Results.NotFound();
        if (take is < 0 or > MaxBacklogTake) return Results.ValidationProblem(new Dictionary<string, string[]> { ["take"] = [$"Use a number from 0 to {MaxBacklogTake}."] });
        var expanded = ParseBacklogExpand(expand); if (expanded is null) return Results.ValidationProblem(new Dictionary<string, string[]> { ["expand"] = [$"Use a comma-separated list of section:count (current, next or backlog), each count from 0 to {MaxBacklogTake}."] });
        var active = await db.Sprints.Where(x => x.TeamId == teamId && x.State == SprintState.Active).Select(x => (Guid?)x.Id).SingleOrDefaultAsync(ct);
        var next = await db.Sprints.Where(x => x.TeamId == teamId && x.State == SprintState.Planned).OrderBy(x => x.StartsOn).Select(x => (Guid?)x.Id).FirstOrDefaultAsync(ct);
        // A filtered backlog is still a tree in rank order: an item whose parent was filtered
        // out is an orphan, and DisplayOrder already puts orphans at the root.
        var parsed = ItemFilter.Parse(filter); if (parsed.Error is not null) return Results.ValidationProblem(new Dictionary<string, string[]> { ["filter"] = [parsed.Error] });
        var (query, filterError) = await ItemQueries.ApplyFilterAsync(db.Items.AsNoTracking().Where(x => x.TeamId == teamId), parsed, team.ProjectId, db, directory, user.UserId, ct);
        if (filterError is not null) return Results.ValidationProblem(new Dictionary<string, string[]> { ["filter"] = [filterError] });
        query = await ItemQueries.ApplySearchAsync(query, q, team.ProjectId, db, ct);
        var placements = await query.OrderBy(x => x.Rank).ThenBy(x => x.Number).Select(x => new BacklogPlacement(x.Id, x.ParentId, x.SprintId)).ToListAsync(ct);
        // A missing active or planned sprint must yield an empty section: comparing SprintId to a
        // null id would match every unplanned item and list it in all three sections at once.
        List<BacklogPlacement> Section(Guid? sprintId, bool exists) => exists ? placements.Where(x => x.SprintId == sprintId).ToList() : [];
        var sections = new Dictionary<string, List<BacklogPlacement>> { ["current"] = Section(active, active is not null), ["next"] = Section(next, next is not null), ["backlog"] = Section(null, true) };
        var pages = sections.ToDictionary(x => x.Key, x => DisplayOrder(x.Value).Take(expanded.GetValueOrDefault(x.Key, take ?? DefaultBacklogTake)).ToHashSet());
        var pageIds = pages.Values.SelectMany(x => x).ToArray();
        var rows = await db.Items.AsNoTracking().Where(x => pageIds.Contains(x.Id)).OrderBy(x => x.Rank).ThenBy(x => x.Number).ToListAsync(ct);
        var views = await ViewsAsync(db, user.UserId!, rows, await StatesAsync(db, rows, ct), ct);
        List<WorkItemView> Page(string section) => views.Where(x => pages[section].Contains(x.Id)).ToList();
        return Results.Ok(new TeamBacklogView(Page("current"), Page("next"), Page("backlog"), sections["current"].Count, sections["next"].Count, sections["backlog"].Count));
    }
    private sealed record BacklogPlacement(Guid Id, Guid? ParentId, Guid? SprintId);
    // The same flattening the SPA draws (lib/backlog.ts flattenBacklog), over a rank-ordered section.
    private static List<Guid> DisplayOrder(List<BacklogPlacement> section)
    {
        var ids = section.Select(x => x.Id).ToHashSet();
        var children = section.ToLookup(x => x.ParentId is { } parent && ids.Contains(parent) ? parent : (Guid?)null);
        var order = new List<Guid>(section.Count);
        var stack = new Stack<BacklogPlacement>(children[null].Reverse());
        while (stack.TryPop(out var placement))
        {
            order.Add(placement.Id);
            foreach (var child in children[placement.Id].Reverse()) stack.Push(child);
        }
        return order;
    }
    private static Dictionary<string, int>? ParseBacklogExpand(string? raw)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in (raw ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = entry.Split(':');
            if (parts.Length != 2 || !BacklogSections.Contains(parts[0], StringComparer.OrdinalIgnoreCase) || !int.TryParse(parts[1], out var count) || count is < 0 or > MaxBacklogTake) return null;
            result[parts[0]] = count;
        }
        return result;
    }

    private static async Task<IResult> Create(CreateWorkItemRequest request, HttpContext http, WorkItemsDbContext db, TenancyDbContext tenancy, IProjectAccess access, ICurrentUser user, Aictiq.SharedKernel.Tenancy.ICurrentTenant tenant, TimeProvider clock, CancellationToken ct)
    {
        var project = http.ResolvedProject()!; var errors = await ValidateAsync(db, tenancy, access, request.Type, request.Title, request.StateId, request.AssigneeId, request.TeamId, request.ParentId, request.Points, request.EstimateHours, request.RemainingHours, request.CompletedHours, project.Id, ct);
        if (request.LabelIds is { Count: > 0 } && await ValidateLabelIdsAsync(db, project.Id, request.LabelIds, ct) is { } labelIdsError) errors["labelIds"] = [labelIdsError];
        if (errors.Count > 0) return Results.ValidationProblem(errors);
        await WorkflowEndpoints.EnsureDefaultAsync(db, tenant.OrganizationId!.Value, project.Id, ct);
        var defaultWorkflowId = await db.Workflows.Where(x => x.ProjectId == project.Id && x.IsDefault).Select(x => x.Id).SingleOrDefaultAsync(ct);
        if (defaultWorkflowId == Guid.Empty) return Results.Problem("The project workflow is still being initialized.", statusCode: StatusCodes.Status409Conflict);
        var state = request.StateId is { } id ? id : await db.WorkflowStates.Where(x => x.WorkflowId == defaultWorkflowId && x.IsInitial).Select(x => x.Id).SingleAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var number = await NextNumberAsync(db, tenant.OrganizationId!.Value, project.Id, ct);
        var now = clock.GetUtcNow(); var markdown = request.DescriptionMarkdown?.Trim() ?? "";
        var item = new WorkItem { OrganizationId = tenant.OrganizationId!.Value, ProjectId = project.Id, ProjectKey = project.Key, Number = number, Type = request.Type, Title = request.Title!.Trim(), DescriptionMarkdown = markdown, DescriptionHtml = Render(markdown), StateId = state, Priority = request.Priority ?? WorkItemPriority.None, AssigneeId = request.AssigneeId, TeamId = request.TeamId, ParentId = request.ParentId, Rank = await ItemRanking.AfterLastAsync(db, project.Id, ct), Points = request.Points, EstimateHours = request.EstimateHours, RemainingHours = request.RemainingHours ?? (request.Type is WorkItemType.Task or WorkItemType.Bug ? request.EstimateHours : null), CompletedHours = request.CompletedHours, DueDate = request.DueDate, CreatedBy = user.UserId!, CreatedAt = now, UpdatedAt = now };
        db.Items.Add(item);
        if (request.LabelIds is { Count: > 0 })
            foreach (var labelId in request.LabelIds.Distinct())
                db.ItemLabels.Add(new ItemLabel { OrganizationId = tenant.OrganizationId!.Value, ItemId = item.Id, LabelId = labelId, AddedAt = now, AddedBy = user.UserId! });
        await ItemWatcherRules.AddAsync(db, item, user.UserId, ItemWatchReason.Author, now, ct);
        await ItemWatcherRules.AddAsync(db, item, item.AssigneeId, ItemWatchReason.Assignee, now, ct);
        item.Changed(user.UserId!, "created");
        await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
        return Results.Created($"/api/v1/orgs/{http.Request.RouteValues["orgSlug"]}/items/{item.Key}", (await ViewsAsync(db, user.UserId!, [item], await StatesAsync(db, [item], ct), ct))[0]);
    }

    /// <summary>
    /// Bulk edits intentionally do not share a database transaction. A stale row or a
    /// transition denied by the workflow must leave the other selected rows useful. The
    /// response therefore carries an HTTP-like result for every requested key.
    /// </summary>
    private static async Task<IResult> BulkUpdate(BulkUpdateItemsRequest request, HttpContext http,
        WorkItemsDbContext db, TenancyDbContext tenancy, IProjectAccess access, ICurrentUser user, Aictiq.SharedKernel.Tenancy.ICurrentTenant tenant, TimeProvider clock, CancellationToken ct)
    {
        var project = http.ResolvedProject()!;
        if (request.Keys is not { Count: > 0 } || request.Keys.Count > 100)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["keys"] = ["Select between 1 and 100 items."] });
        if (request.Set is null || !HasChanges(request.Set))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["set"] = ["Choose at least one field to change."] });
        if (request.Versions is null)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["versions"] = ["Supply the version for every selected item."] });

        var set = request.Set;
        if (!await AssignableAsync(access, project.Id, set.AssigneeId, ct))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["set.assigneeId"] = ["The assignee must be able to see this project."] });
        if (!await TeamInProjectAsync(tenancy, set.TeamId, project.Id, ct))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["set.teamId"] = ["The team must belong to this project."] });
        var validLabels = await ValidateBulkLabelsAsync(db, project.Id, set, ct);
        var requestedState = set.StateId is { } stateId
            ? await db.WorkflowStates.FirstOrDefaultAsync(x => x.Id == stateId && db.Workflows.Any(w => w.Id == x.WorkflowId && w.ProjectId == project.Id), ct)
            : null;
        var stateInvalid = set.StateId is not null && requestedState is null;
        var transitions = requestedState is null ? [] : await db.WorkflowTransitions
            .Where(x => x.WorkflowId == requestedState.WorkflowId).ToListAsync(ct);
        var results = new List<BulkItemResult>(request.Keys.Count);
        var changedKeys = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var suppliedKey in request.Keys)
        {
            var key = suppliedKey.Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(key) || !seen.Add(key))
            {
                results.Add(new BulkItemResult(suppliedKey, StatusCodes.Status422UnprocessableEntity, "Each item key must be present exactly once.", null));
                continue;
            }
            if (stateInvalid)
            {
                results.Add(new BulkItemResult(key, StatusCodes.Status422UnprocessableEntity, "The requested state is not in this project's workflow.", null));
                continue;
            }
            if (validLabels is not null)
            {
                results.Add(new BulkItemResult(key, StatusCodes.Status422UnprocessableEntity, validLabels, null));
                continue;
            }
            if (!request.Versions.TryGetValue(key, out var version) && !request.Versions.TryGetValue(suppliedKey, out version))
            {
                results.Add(new BulkItemResult(key, StatusCodes.Status422UnprocessableEntity, "A version is required for this item.", null));
                continue;
            }

            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            try
            {
                var item = await db.Items.FirstOrDefaultAsync(x => x.ProjectId == project.Id && x.ProjectKey == ParseKey(key).ProjectKey && x.Number == ParseKey(key).Number, ct);
                if (item is null)
                {
                    results.Add(new BulkItemResult(key, StatusCodes.Status422UnprocessableEntity, "The item is not in this project.", null));
                    continue;
                }
                if (item.Version != version)
                {
                    results.Add(new BulkItemResult(key, StatusCodes.Status409Conflict, "The item was modified by someone else.", null));
                    continue;
                }
                if (set.SprintId is { } requestedSprintId && !await db.Sprints.AnyAsync(x => x.Id == requestedSprintId && x.TeamId == (set.TeamId ?? item.TeamId) && x.State != SprintState.Completed, ct))
                {
                    results.Add(new BulkItemResult(key, StatusCodes.Status422UnprocessableEntity, "The sprint must be open and belong to the item's team.", null));
                    continue;
                }
                if (requestedState is not null && item.StateId != requestedState.Id && transitions.Count > 0 &&
                    !transitions.Any(x => x.ToStateId == requestedState.Id && (x.FromStateId is null || x.FromStateId == item.StateId)))
                {
                    results.Add(new BulkItemResult(key, StatusCodes.Status422UnprocessableEntity, "That workflow transition is not allowed for this item.", null));
                    continue;
                }

                db.Entry(item).Property(x => x.Version).OriginalValue = version;
                var changed = await ApplyBulkSetAsync(db, item, set, requestedState, user.UserId!, clock.GetUtcNow(), ct);
                if (changed)
                {
                    await db.SaveChangesAsync(ct);
                    await transaction.CommitAsync(ct);
                    changedKeys.Add(item.Key);
                }
                var view = (await ViewsAsync(db, user.UserId!, [item], await StatesAsync(db, [item], ct), ct))[0];
                results.Add(new BulkItemResult(item.Key, StatusCodes.Status200OK, null, view));
            }
            catch (DbUpdateConcurrencyException)
            {
                results.Add(new BulkItemResult(key, StatusCodes.Status409Conflict, "The item was modified by someone else.", null));
            }
            finally
            {
                // A failed transaction can leave rejected Added history/label rows in the
                // tracker. Never let one selected item contaminate the next one's work.
                db.ChangeTracker.Clear();
            }
        }

        if (changedKeys.Count > 0)
        {
            db.Set<OutboxMessage>().Add(OutboxMessage.From(new ItemsBulkUpdated(
                tenant.OrganizationId!.Value, project.Id, changedKeys, user.UserId!)));
            await db.SaveChangesAsync(ct);
        }
        return Results.Ok(new BulkUpdateItemsResponse(results));
    }

    private static bool HasChanges(BulkItemSet set) => set.StateId is not null || set.AssigneeId is not null ||
        set.Priority is not null || set.TeamId is not null || set.SprintId is not null || set.AddLabels is not null || set.RemoveLabels is not null;

    private static async Task<string?> ValidateBulkLabelsAsync(WorkItemsDbContext db, Guid projectId, BulkItemSet set, CancellationToken ct)
    {
        var ids = (set.AddLabels ?? []).Concat(set.RemoveLabels ?? []).Distinct().ToArray();
        if (ids.Length == 0) return null;
        if ((set.AddLabels ?? []).Intersect(set.RemoveLabels ?? []).Any()) return "A label cannot be both added and removed.";
        return await db.Labels.CountAsync(x => x.ProjectId == projectId && ids.Contains(x.Id), ct) == ids.Length
            ? null : "One or more labels do not belong to this project.";
    }

    private static async Task<bool> ApplyBulkSetAsync(WorkItemsDbContext db, WorkItem item, BulkItemSet set,
        WorkflowState? targetState, string actorId, DateTimeOffset now, CancellationToken ct)
    {
        var changed = false;
        if (targetState is not null && item.StateId != targetState.Id)
        {
            item.Transition(targetState.Id, targetState.Category, actorId, now);
            changed = true;
        }
        if (set.AssigneeId is not null && item.AssigneeId != set.AssigneeId) { item.AssigneeId = set.AssigneeId; await ItemWatcherRules.AddAsync(db, item, item.AssigneeId, ItemWatchReason.Assignee, now, ct); changed = true; }
        if (set.Priority is { } priority && item.Priority != priority) { item.Priority = priority; changed = true; }
        if (set.TeamId is { } teamId && item.TeamId != teamId) { var oldSprintId = item.SprintId; item.TeamId = teamId; item.SprintId = null; await SprintEndpoints.LogScopeAsync(db, item, oldSprintId, null, now, ct); changed = true; }
        if (set.SprintId is { } sprintId && item.SprintId != sprintId) { var oldSprintId = item.SprintId; item.SprintId = sprintId; await SprintEndpoints.LogScopeAsync(db, item, oldSprintId, sprintId, now, ct); changed = true; }

        if (set.AddLabels is not null || set.RemoveLabels is not null)
        {
            var current = await db.ItemLabels.Where(x => x.ItemId == item.Id).ToListAsync(ct);
            var currentIds = current.Select(x => x.LabelId).ToHashSet();
            foreach (var link in current.Where(x => (set.RemoveLabels ?? []).Contains(x.LabelId))) { db.ItemLabels.Remove(link); changed = true; }
            foreach (var labelId in (set.AddLabels ?? []).Distinct().Where(x => !currentIds.Contains(x)))
            {
                db.ItemLabels.Add(new ItemLabel { OrganizationId = item.OrganizationId, ItemId = item.Id, LabelId = labelId, AddedAt = now, AddedBy = actorId });
                changed = true;
            }
        }
        if (changed && targetState is null) item.UpdatedAt = now;
        return changed;
    }

    private static async Task<IResult> Get(string itemKey, HttpContext http, WorkItemsDbContext db, IProjectAccess access, ICurrentUser user, CancellationToken ct)
    { var item = await FindVisible(db, access, user, itemKey, ct); return item is null ? Results.NotFound() : Results.Ok((await ViewsAsync(db, user.UserId!, [item], await StatesAsync(db, [item], ct), ct))[0]); }

    private static async Task<IResult> History(string itemKey, WorkItemsDbContext db, IProjectAccess access, ICurrentUser user, IUserDirectory directory, int page = 1, int pageSize = 25, CancellationToken ct = default)
    {
        var item = await FindVisible(db, access, user, itemKey, ct);
        if (item is null) return Results.NotFound();
        var rows = db.ItemHistory.AsNoTracking().Where(x => x.ItemId == item.Id)
            .Select(x => new HistoryRow(x.Id, x.EventId, x.ItemId, item.Key, x.ActorId, x.At, x.Field, x.OldValue, x.NewValue));
        return Results.Ok(await HistoryPageAsync(rows, directory, page, pageSize, ct));
    }

    private static async Task<IResult> ProjectActivity(HttpContext http, WorkItemsDbContext db, IUserDirectory directory, string? actorId, Guid? teamId, int page = 1, int pageSize = 25, CancellationToken ct = default)
    {
        var projectId = http.ResolvedProjectId()!.Value;
        var rows = db.ItemHistory.AsNoTracking().Join(db.Items.AsNoTracking(), history => history.ItemId, item => item.Id,
            (history, item) => new HistoryRow(history.Id, history.EventId, item.Id, item.Key, history.ActorId, history.At, history.Field, history.OldValue, history.NewValue))
            .Where(x => db.Items.Any(item => item.Id == x.ItemId && item.ProjectId == projectId && (teamId == null || item.TeamId == teamId) && (actorId == null || x.ActorId == actorId)));
        return Results.Ok(await HistoryPageAsync(rows, directory, page, pageSize, ct));
    }

    private static async Task<IResult> Update(string itemKey, UpdateWorkItemRequest request, HttpContext http, WorkItemsDbContext db, TenancyDbContext tenancy, IProjectAccess access, ICurrentUser user, TimeProvider clock, CancellationToken ct)
    {
        var item = await FindWritable(db, access, user, itemKey, ct); if (item is null) return Results.NotFound(); if (item.Version != request.Version) return Conflict();
        var type = request.Type ?? item.Type; var errors = await ValidateAsync(db, tenancy, access, type, request.Title ?? item.Title, request.StateId ?? item.StateId, request.AssigneeId ?? item.AssigneeId, request.TeamId ?? item.TeamId, request.ParentId ?? item.ParentId, request.Points ?? item.Points, request.EstimateHours ?? item.EstimateHours, request.RemainingHours ?? item.RemainingHours, request.CompletedHours ?? item.CompletedHours, item.ProjectId, ct, item.Id);
        if (request.LabelIds is { Count: > 0 } && await ValidateLabelIdsAsync(db, item.ProjectId, request.LabelIds, ct) is { } labelIdsError) errors["labelIds"] = [labelIdsError];
        if (errors.Count > 0) return Results.ValidationProblem(errors);
        if (request.Type is not null && request.Type != item.Type && !await ChildrenCompatibleAsync(db, item.Id, request.Type.Value, ct)) return InvalidParent("Changing this type would invalidate one or more children.");
        var updatedAt = clock.GetUtcNow();
        item.Type = type; item.Title = request.Title?.Trim() ?? item.Title; if (request.DescriptionMarkdown is not null) { item.DescriptionMarkdown = request.DescriptionMarkdown; item.DescriptionHtml = Render(request.DescriptionMarkdown); }
        // A state change here must still go through Transition() — this endpoint sets the
        // whole record, but the resolved/completed/removed timestamps, the item history and
        // automation rules watching this state all depend on WorkItemTransitioned
        // actually being raised, not just the column being written.
        if (request.StateId is { } requestedStateId && requestedStateId != item.StateId)
        {
            var targetState = await db.WorkflowStates.FirstAsync(x => x.Id == requestedStateId, ct);
            item.Transition(targetState.Id, targetState.Category, user.UserId!, updatedAt);
        }
        item.Priority = request.Priority ?? item.Priority; item.AssigneeId = request.AssigneeId; var oldSprintId = item.SprintId; var oldTeamId = item.TeamId; item.TeamId = request.TeamId; if (oldSprintId is not null && oldTeamId != item.TeamId) { item.SprintId = null; await SprintEndpoints.LogScopeAsync(db, item, oldSprintId, null, updatedAt, ct); } item.ParentId = request.ParentId; item.Points = request.Points; item.EstimateHours = request.EstimateHours; item.RemainingHours = request.RemainingHours; item.CompletedHours = request.CompletedHours; item.DueDate = request.DueDate; item.UpdatedAt = updatedAt;
        await ItemWatcherRules.AddAsync(db, item, item.AssigneeId, ItemWatchReason.Assignee, updatedAt, ct);
        // null/absent leaves labels untouched; [] clears them. Diffed against the current
        // set rather than replaced wholesale so an unchanged label is never re-inserted.
        if (request.LabelIds is not null)
        {
            var desired = request.LabelIds.Distinct().ToHashSet();
            var current = await db.ItemLabels.Where(x => x.ItemId == item.Id).ToListAsync(ct);
            foreach (var stale in current.Where(x => !desired.Contains(x.LabelId))) db.ItemLabels.Remove(stale);
            foreach (var added in desired.Except(current.Select(x => x.LabelId)))
                db.ItemLabels.Add(new ItemLabel { OrganizationId = item.OrganizationId, ItemId = item.Id, LabelId = added, AddedAt = clock.GetUtcNow(), AddedBy = user.UserId! });
        }
        item.Changed(user.UserId!, "updated");
        await db.SaveChangesAsync(ct); return Results.Ok((await ViewsAsync(db, user.UserId!, [item], await StatesAsync(db, [item], ct), ct))[0]);
    }

    private static async Task<IResult> Transition(string itemKey, TransitionRequest request, HttpContext http, WorkItemsDbContext db, IProjectAccess access, ICurrentUser user, TimeProvider clock, CancellationToken ct)
    {
        var item = await FindWritable(db, access, user, itemKey, ct); if (item is null) return Results.NotFound(); if (item.Version != request.Version) return Conflict();
        var to = await db.WorkflowStates.FirstOrDefaultAsync(x => x.Id == request.ToStateId, ct); if (to is null || !await db.Workflows.AnyAsync(w => w.Id == to.WorkflowId && w.ProjectId == item.ProjectId, ct)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["toStateId"] = ["Unknown project workflow state."] });
        var from = await db.WorkflowStates.FindAsync([item.StateId], ct); var transitions = db.WorkflowTransitions.Where(x => x.WorkflowId == to.WorkflowId); if (await transitions.AnyAsync(ct) && !await transitions.AnyAsync(x => x.ToStateId == to.Id && (x.FromStateId == null || x.FromStateId == item.StateId), ct)) return Results.Problem("That transition is not allowed.", statusCode: StatusCodes.Status409Conflict, type: ProblemTypes.Conflict);
        item.Transition(to.Id, to.Category, user.UserId!, clock.GetUtcNow());
        if (to.Category == WorkflowStateCategory.Completed && item.Type is WorkItemType.Task or WorkItemType.Bug)
            item.RemainingHours = 0;
        await db.SaveChangesAsync(ct); return Results.Ok((await ViewsAsync(db, user.UserId!, [item], new Dictionary<Guid, WorkflowState> { [to.Id] = to }, ct))[0]);
    }

    // The predicate and write intentionally live in one SQL UPDATE: observing an unclaimed
    // row and then assigning it through EF would make a concurrent claim race possible.
    //
    // `xmin` is an `xid`, and Postgres defines no `xid = bigint` operator, so the version
    // token has to be compared as a number on both sides. EF's own concurrency check does
    // this for us; raw SQL has to say it, or every claim fails with 42883 at runtime.
    private static async Task<IResult> Claim(string itemKey, ClaimRequest request, WorkItemsDbContext db,
        IProjectAccess access, ICurrentUser user, TimeProvider clock, IConfiguration configuration, CancellationToken ct)
    {
        var item = await FindWritable(db, access, user, itemKey, ct);
        if (item is null) return Results.NotFound();
        var activeState = await db.WorkflowStates.AsNoTracking()
            .Where(s => s.Category == WorkflowStateCategory.Active &&
                db.Workflows.Any(w => w.Id == s.WorkflowId && w.ProjectId == item.ProjectId) &&
                (!db.WorkflowTransitions.Any(t => t.WorkflowId == s.WorkflowId) ||
                 db.WorkflowTransitions.Any(t => t.WorkflowId == s.WorkflowId && t.ToStateId == s.Id && (t.FromStateId == null || t.FromStateId == item.StateId))))
            .OrderBy(s => s.Position).Select(s => (Guid?)s.Id).FirstOrDefaultAsync(ct);
        if (activeState is null) return Results.Problem("The project workflow has no reachable Active state.", statusCode: StatusCodes.Status409Conflict, type: ProblemTypes.Conflict);
        var staleAfter = Math.Max(1, configuration.GetValue("Claims:StaleAfterMinutes", 30));
        var now = clock.GetUtcNow();
        var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE work.items
            SET claimed_by = {user.UserId!}, claimed_at = {now}, claim_heartbeat_at = {now},
                assignee_id = {user.UserId!}, state_id = {activeState.Value}, updated_at = {now}
            WHERE id = {item.Id} AND xmin::text::bigint = {(long)request.Version}
              AND (claimed_by IS NULL OR claim_heartbeat_at < {now - TimeSpan.FromMinutes(staleAfter)})
            """, ct);
        if (affected == 0)
        {
            var current = await db.Items.AsNoTracking().IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == item.Id, ct);
            return Results.Problem("This item is already claimed or was modified.", statusCode: StatusCodes.Status409Conflict,
                type: "https://aictiq.com/problems/already-claimed", extensions: new Dictionary<string, object?> { ["claimedBy"] = current?.ClaimedBy, ["version"] = current?.Version });
        }
        db.ChangeTracker.Clear();
        var claimed = await db.Items.FirstAsync(x => x.Id == item.Id, ct);
        claimed.Changed(user.UserId!, "claim");
        await db.SaveChangesAsync(ct);
        return Results.Ok((await ViewsAsync(db, user.UserId!, [claimed], await StatesAsync(db, [claimed], ct), ct))[0]);
    }

    private static async Task<IResult> Release(string itemKey, WorkItemsDbContext db, IProjectAccess access, ICurrentUser user, TimeProvider clock, CancellationToken ct)
    {
        var item = await FindWritable(db, access, user, itemKey, ct); if (item is null) return Results.NotFound();
        var role = await access.GetProjectRoleAsync(user.UserId!, item.ProjectId, ct);
        if (item.ClaimedBy is not null && item.ClaimedBy != user.UserId && (role is null || !role.Value.Satisfies(ProjectRole.Admin))) return Results.Forbid();
        if (item.ClaimedBy is not null) { item.ClaimedBy = null; item.ClaimedAt = null; item.ClaimHeartbeatAt = null; item.UpdatedAt = clock.GetUtcNow(); item.Changed(user.UserId!, "claim"); await db.SaveChangesAsync(ct); }
        return Results.NoContent();
    }

    private static async Task<IResult> Heartbeat(string itemKey, WorkItemsDbContext db, IProjectAccess access, ICurrentUser user, TimeProvider clock, CancellationToken ct)
    {
        var item = await FindWritable(db, access, user, itemKey, ct); if (item is null) return Results.NotFound();
        if (item.ClaimedBy != user.UserId) return Results.Problem("Only the claimant can renew this claim.", statusCode: StatusCodes.Status409Conflict, type: "https://aictiq.com/problems/not-claimant");
        item.ClaimHeartbeatAt = clock.GetUtcNow(); item.UpdatedAt = item.ClaimHeartbeatAt.Value; await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> LogTime(string itemKey, LogTimeRequest request, HttpContext http, WorkItemsDbContext db,
        IProjectAccess access, ICurrentUser user, TimeProvider clock, CancellationToken ct)
    {
        if (request.Hours <= 0 || request.Hours > 9999.99m)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["hours"] = ["Hours must be greater than zero and no more than 9999.99."] });
        var item = await FindWritable(db, access, user, itemKey, ct);
        if (item is null) return Results.NotFound();
        if (item.Version != request.Version) return Conflict();
        if (item.Type is not (WorkItemType.Task or WorkItemType.Bug))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["hours"] = ["Time can only be logged on Tasks and Bugs."] });
        item.LogTime(request.Hours, clock.GetUtcNow());
        item.Changed(user.UserId!, "time");
        await db.SaveChangesAsync(ct);
        return Results.Ok((await ViewsAsync(db, user.UserId!, [item], await StatesAsync(db, [item], ct), ct))[0]);
    }

    /// <summary>
    /// Deletes the item and every item below it, for good. Comments, history, links, labels,
    /// watchers and attachment rows go with them in this transaction; the attachment objects,
    /// the wiki's references, analytics rows and notifications follow through the outbox.
    /// There is no trash: the Removed workflow state is how an item is set aside, this is how
    /// it stops existing.
    /// </summary>
    private static async Task<IResult> Delete(string itemKey, WorkItemsDbContext db, IProjectAccess access, ICurrentUser user, TimeProvider clock, CancellationToken ct)
    {
        var item = await FindWritable(db, access, user, itemKey, ct);
        if (item is null) return Results.NotFound();

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var subtree = await SubtreeAsync(db, item.Id, ct);
        var ids = subtree.Select(x => x.Id).ToArray();
        var commentIds = db.Comments.Where(c => ids.Contains(c.ItemId)).Select(c => (Guid?)c.Id);
        var objectKeys = await db.Attachments
            .Where(a => (a.ItemId != null && ids.Contains(a.ItemId.Value)) || commentIds.Contains(a.CommentId))
            .Select(a => a.ObjectKey).ToListAsync(ct);

        var deleted = await db.Database.SqlQuery<Guid>($"""SELECT work.delete_item_subtree({item.Id}) AS "Value" """).ToListAsync(ct);
        await AuditPurge.EntitiesAsync(db, item.OrganizationId, AuditEntityTypes.WorkItem, deleted, ct);
        db.Set<AuditLogEntry>().Add(AuditPurge.Tombstone(item.OrganizationId, AuditEntityTypes.WorkItem, item.Id,
            deleted.Count == 1 ? item.Key : $"{item.Key} (+{deleted.Count - 1} below)", user.UserId, clock.GetUtcNow()));
        db.Set<OutboxMessage>().Add(OutboxMessage.From(new WorkItemsDeleted(item.OrganizationId, item.ProjectId, deleted, objectKeys, user.UserId!)));
        db.Entry(item).State = EntityState.Detached;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.NoContent();
    }

    /// <summary>What deleting an item takes with it, for the confirmation to show before anyone commits to it.</summary>
    private static async Task<IResult> DeletePreview(string itemKey, WorkItemsDbContext db, IProjectAccess access, ICurrentUser user, CancellationToken ct)
    {
        var item = await FindWritable(db, access, user, itemKey, ct);
        if (item is null) return Results.NotFound();
        var subtree = await SubtreeAsync(db, item.Id, ct);
        var depths = subtree.ToDictionary(x => x.Id, x => x.Depth);
        var ids = depths.Keys.ToArray();
        var below = await db.Items.AsNoTracking().Where(x => ids.Contains(x.Id) && x.Id != item.Id)
            .Select(x => new { x.Id, x.ProjectKey, x.Number, x.Title, x.Type, x.ParentId }).ToListAsync(ct);
        // Tree order: each item directly under its parent, siblings by number.
        var byParent = below.ToLookup(x => x.ParentId);
        var ordered = new List<ItemDeletePreviewItem>();
        void Walk(Guid parent)
        {
            foreach (var child in byParent[parent].OrderBy(x => x.Number))
            {
                ordered.Add(new ItemDeletePreviewItem($"{child.ProjectKey}-{child.Number}", child.Title, child.Type, depths[child.Id]));
                Walk(child.Id);
            }
        }
        Walk(item.Id);
        var commentIds = db.Comments.Where(c => ids.Contains(c.ItemId)).Select(c => (Guid?)c.Id);
        return Results.Ok(new ItemDeletePreviewView(
            ordered,
            await db.Comments.CountAsync(c => ids.Contains(c.ItemId) && c.DeletedAt == null, ct),
            await db.Attachments.CountAsync(a => (a.ItemId != null && ids.Contains(a.ItemId.Value)) || commentIds.Contains(a.CommentId), ct),
            await db.ItemLinks.CountAsync(l => ids.Contains(l.ItemId), ct),
            await db.ItemRelations.CountAsync(r => ids.Contains(r.SourceId) || ids.Contains(r.TargetId), ct)));
    }

    private sealed record SubtreeRow(Guid Id, int Depth);

    /// <summary>The item and everything below it, each with its depth under the root (the root is 0).</summary>
    private static Task<List<SubtreeRow>> SubtreeAsync(WorkItemsDbContext db, Guid rootId, CancellationToken ct) =>
        db.Database.SqlQuery<SubtreeRow>($"""
            WITH RECURSIVE subtree AS (
                SELECT id, 0 AS depth FROM work.items WHERE id = {rootId}
                UNION SELECT i.id, s.depth + 1 FROM work.items i JOIN subtree s ON i.parent_id = s.id
            ) SELECT id AS "Id", depth AS "Depth" FROM subtree
            """).ToListAsync(ct);

    private static async Task<IResult> Reparent(string itemKey, ReparentRequest request, HttpContext http, WorkItemsDbContext db, IProjectAccess access, ICurrentUser user, TimeProvider clock, CancellationToken ct)
    {
        var item = await FindWritable(db, access, user, itemKey, ct); if (item is null) return Results.NotFound(); if (item.Version != request.Version) return Conflict(); Guid? parentId = null; if (!string.IsNullOrWhiteSpace(request.ParentKey)) { var parent = await FindVisible(db, access, user, request.ParentKey, ct); if (parent is null || parent.ProjectId != item.ProjectId) return InvalidParent("The parent must be in the same project."); parentId = parent.Id; }
        var errors = await ValidateParentAsync(db, item.Id, item.Type, parentId, item.ProjectId, ct); if (errors is not null) return InvalidParent(errors); item.ParentId = parentId; item.UpdatedAt = clock.GetUtcNow(); item.Changed(user.UserId!, "parentId"); await db.SaveChangesAsync(ct); return Results.Ok((await ViewsAsync(db, user.UserId!, [item], await StatesAsync(db, [item], ct), ct))[0]);
    }
    private static async Task<IResult> Move(string itemKey, MoveItemRequest request, WorkItemsDbContext db, TenancyDbContext tenancy, IProjectAccess access, ICurrentUser user, TimeProvider clock, CancellationToken ct)
    {
        var item = await FindWritable(db, access, user, itemKey, ct); if (item is null) return Results.NotFound();
        // A team or sprint id is a request, not a grant: a team from another project — or
        // another organization, if its id were known — must not become this item's home.
        if (!await TeamInProjectAsync(tenancy, request.TeamId, item.ProjectId, ct)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["teamId"] = ["The team must belong to this project."] });
        if (request.SprintId is { } requestedSprint && !await db.Sprints.AnyAsync(x => x.Id == requestedSprint && x.TeamId == (request.TeamId ?? item.TeamId) && x.State != SprintState.Completed, ct)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["sprintId"] = ["The sprint must be open and belong to the item's team."] });
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtext({item.ProjectId.ToString()}))", ct);
        db.ChangeTracker.Clear(); item = await FindWritable(db, access, user, itemKey, ct); if (item is null) return Results.NotFound(); if (item.Version != request.Version) return Conflict();
        if (request.AfterKey is not null && request.BeforeKey is not null && request.AfterKey.Equals(request.BeforeKey, StringComparison.OrdinalIgnoreCase)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["afterKey"] = ["The neighbours must be different."] });
        var after = request.AfterKey is null ? null : await FindVisible(db, access, user, request.AfterKey, ct);
        var before = request.BeforeKey is null ? null : await FindVisible(db, access, user, request.BeforeKey, ct);
        if (after is not null && after.ProjectId != item.ProjectId || before is not null && before.ProjectId != item.ProjectId) return Results.ValidationProblem(new Dictionary<string, string[]> { ["afterKey"] = ["Neighbours must be in the same project."] });
        if (after?.Rank is not null && before?.Rank is not null && string.CompareOrdinal(after.Rank, before.Rank) >= 0) return Results.ValidationProblem(new Dictionary<string, string[]> { ["afterKey"] = ["afterKey must precede beforeKey."] });
        var rank = ItemRanking.Between(after?.Rank, before?.Rank); if (rank.Length > 64) { await ItemRanking.RebalanceAsync(db, item.ProjectId, ct); await db.SaveChangesAsync(ct); db.ChangeTracker.Clear(); item = await FindWritable(db, access, user, itemKey, ct); if (item is null) return Results.NotFound(); after = request.AfterKey is null ? null : await FindVisible(db, access, user, request.AfterKey, ct); before = request.BeforeKey is null ? null : await FindVisible(db, access, user, request.BeforeKey, ct); rank = ItemRanking.Between(after?.Rank, before?.Rank); }
        while (await db.Items.AnyAsync(x => x.ProjectId == item.ProjectId && x.Id != item.Id && x.Rank == rank, ct)) rank = ItemRanking.Between(rank, before?.Rank);
        db.Entry(item).Property(x => x.Version).OriginalValue = request.Version; item.Rank = rank; item.UpdatedAt = clock.GetUtcNow();
        if (request.TeamId is not null && item.TeamId != request.TeamId) { var oldSprint = item.SprintId; item.TeamId = request.TeamId; item.SprintId = request.SprintId; await SprintEndpoints.LogScopeAsync(db, item, oldSprint, item.SprintId, item.UpdatedAt, ct); }
        else if ((request.SprintId is not null || request.RemoveSprint) && item.SprintId != request.SprintId) { var oldSprint = item.SprintId; item.SprintId = request.SprintId; await SprintEndpoints.LogScopeAsync(db, item, oldSprint, item.SprintId, item.UpdatedAt, ct); }
        if (request.ParentKey is not null) { var parent = await FindVisible(db, access, user, request.ParentKey, ct); if (parent is null) return Results.NotFound(); var error = await ValidateParentAsync(db, item.Id, item.Type, parent.Id, item.ProjectId, ct); if (error is not null) return InvalidParent(error); item.ParentId = parent.Id; }
        item.Moved(user.UserId!);
        try { await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); } catch (DbUpdateConcurrencyException) { return Conflict(); }
        return Results.Ok((await ViewsAsync(db, user.UserId!, [item], await StatesAsync(db, [item], ct), ct))[0]);
    }
    private static async Task<IResult> BoardMove(string itemKey, BoardMoveRequest request, WorkItemsDbContext db, TenancyDbContext tenancy, IProjectAccess access, ICurrentUser user, TimeProvider clock, CancellationToken ct)
    {
        var item = await FindWritable(db, access, user, itemKey, ct); if (item is null || item.TeamId is null) return Results.NotFound();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtext({item.ProjectId.ToString()}))", ct);
        db.ChangeTracker.Clear(); item = await FindWritable(db, access, user, itemKey, ct); if (item is null || item.TeamId is null) return Results.NotFound(); if (item.Version != request.Version) return Conflict();
        var board = await BoardEndpoints.FindAsync(db, item.TeamId.Value, ct); if (board is null || !BoardEndpoints.ContainsState(board, request.ToStateId)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["toStateId"] = ["The target state is not on this team's board."] });
        var target = await db.WorkflowStates.FirstOrDefaultAsync(x => x.Id == request.ToStateId && db.Workflows.Any(w => w.Id == x.WorkflowId && w.ProjectId == item.ProjectId), ct); if (target is null) return Results.ValidationProblem(new Dictionary<string, string[]> { ["toStateId"] = ["Unknown project workflow state."] });
        var transitions = db.WorkflowTransitions.Where(x => x.WorkflowId == target.WorkflowId); if (item.StateId != target.Id && await transitions.AnyAsync(ct) && !await transitions.AnyAsync(x => x.ToStateId == target.Id && (x.FromStateId == null || x.FromStateId == item.StateId), ct)) return Results.Problem("That transition is not allowed.", statusCode: 409, type: ProblemTypes.Conflict);
        var column = BoardEndpoints.ColumnFor(board, request.ToColumnId) ?? BoardEndpoints.ColumnFor(board, target.Id)!;
        if (!column.StateIds.Contains(target.Id)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["toColumnId"] = ["The target column does not allow this workflow state."] });
        var boardItems = await db.Items.Where(x => x.TeamId == item.TeamId && x.Id != item.Id && x.Rank != null).OrderBy(x => x.Rank).ToListAsync(ct);
        var count = boardItems.Count(x => BoardEndpoints.ColumnFor(x, board)?.Id == column.Id && x.Type is WorkItemType.Story or WorkItemType.Bug);
        if (column.WipLimit is { } limit && count >= limit)
        {
            var team = await tenancy.Teams.AsNoTracking().SingleOrDefaultAsync(x => x.Id == item.TeamId, ct);
            if (!request.Force || team is null || !await BoardEndpoints.CanManageAsync(team, tenancy, access, user, ct)) return Results.Problem("The destination column has reached its WIP limit.", type: ProblemTypes.WipLimit, statusCode: 409);
        }
        var after = request.AfterKey is null ? null : await FindVisible(db, access, user, request.AfterKey, ct); if (after is not null && (after.ProjectId != item.ProjectId || BoardEndpoints.ColumnFor(after, board)?.Id != column.Id)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["afterKey"] = ["The preceding card must be in the destination column."] });
        var before = boardItems.FirstOrDefault(x => BoardEndpoints.ColumnFor(x, board)?.Id == column.Id && (after == null || string.CompareOrdinal(x.Rank, after.Rank) > 0));
        var rank = ItemRanking.Between(after?.Rank, before?.Rank); while (await db.Items.AnyAsync(x => x.ProjectId == item.ProjectId && x.Id != item.Id && x.Rank == rank, ct)) rank = ItemRanking.Between(rank, before?.Rank);
        db.Entry(item).Property(x => x.Version).OriginalValue = request.Version;
        var now = clock.GetUtcNow(); if (item.StateId != target.Id) item.Transition(target.Id, target.Category, user.UserId!, now); else item.UpdatedAt = now;
        item.BoardColumnId = column.Id; item.Rank = rank; item.Moved(user.UserId!); if (target.Category == WorkflowStateCategory.Completed && item.Type is WorkItemType.Task or WorkItemType.Bug) item.RemainingHours = 0;
        try { await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); } catch (DbUpdateConcurrencyException) { return Conflict(); }
        return Results.Ok((await ViewsAsync(db, user.UserId!, [item], new Dictionary<Guid, WorkflowState> { [target.Id] = target }, ct))[0]);
    }
    private static async Task<IResult> Children(string itemKey, HttpContext http, WorkItemsDbContext db, IProjectAccess access, ICurrentUser user, CancellationToken ct) { var item = await FindVisible(db, access, user, itemKey, ct); if (item is null) return Results.NotFound(); var children = await db.Items.AsNoTracking().Where(x => x.ParentId == item.Id).OrderBy(x => x.Number).ToListAsync(ct); return Results.Ok(await ViewsAsync(db, user.UserId!, children, await StatesAsync(db, children, ct), ct)); }
    private static async Task<IResult> Ancestors(string itemKey, HttpContext http, WorkItemsDbContext db, IProjectAccess access, ICurrentUser user, CancellationToken ct) { var item = await FindVisible(db, access, user, itemKey, ct); if (item is null) return Results.NotFound(); var all = new List<WorkItem>(); for (var id = item.ParentId; id is { } current && all.Count < 6;) { var ancestor = await db.Items.AsNoTracking().FirstOrDefaultAsync(x => x.Id == current, ct); if (ancestor is null) break; all.Add(ancestor); id = ancestor.ParentId; } return Results.Ok(await ViewsAsync(db, user.UserId!, all, await StatesAsync(db, all, ct), ct)); }

    internal static async Task<WorkItem?> FindVisible(WorkItemsDbContext db, IProjectAccess access, ICurrentUser user, string key, CancellationToken ct) { var (projectKey, number) = ParseKey(key); if (number is null) return null; var item = await db.Items.FirstOrDefaultAsync(x => x.ProjectKey == projectKey && x.Number == number, ct); return item is not null && await access.GetProjectRoleAsync(user.UserId!, item.ProjectId, ct) is not null ? item : null; }
    /// <summary>
    /// The project's next item number, from <c>work.project_sequences</c>. Every door that
    /// creates an item must take its number here: a door that computed max + 1 instead left
    /// the sequence behind, and every later create collided on the unique number and 409'd.
    /// Call it inside the transaction that inserts the item.
    /// </summary>
    internal static async Task<int> NextNumberAsync(WorkItemsDbContext db, Guid organizationId, Guid projectId, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO work.project_sequences (project_id, organization_id, next_number) VALUES ({projectId}, {organizationId}, 1) ON CONFLICT (project_id) DO NOTHING", ct);
        var command = db.Database.GetDbConnection().CreateCommand();
        await using (command)
        {
            command.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
            command.CommandText = "UPDATE work.project_sequences SET next_number = next_number + 1 WHERE project_id = @projectId RETURNING next_number - 1";
            var parameter = command.CreateParameter(); parameter.ParameterName = "projectId"; parameter.Value = projectId; command.Parameters.Add(parameter);
            return Convert.ToInt32(await command.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
        }
    }
    internal static async Task<WorkItem?> FindWritable(WorkItemsDbContext db, IProjectAccess access, ICurrentUser user, string key, CancellationToken ct) { var item = await FindVisible(db, access, user, key, ct); if (item is null || await access.GetProjectRoleAsync(user.UserId!, item.ProjectId, ct) is not { } role || !role.Satisfies(ProjectRole.Member)) return null; return item; }
    internal static (string ProjectKey, int? Number) ParseKey(string key) { var idx = key.LastIndexOf('-'); return idx < 1 || !int.TryParse(key[(idx + 1)..], out var number) || number < 1 ? ("", null) : (key[..idx].ToUpperInvariant(), number); }
    private static async Task<Dictionary<string, string[]>> ValidateAsync(WorkItemsDbContext db, TenancyDbContext tenancy, IProjectAccess access, WorkItemType type, string? title, Guid? stateId, string? assigneeId, Guid? teamId, Guid? parentId, decimal? points, decimal? estimate, decimal? remaining, decimal? completed, Guid projectId, CancellationToken ct, Guid? itemId = null) { var errors = new Dictionary<string, string[]>(); if (string.IsNullOrWhiteSpace(title) || title.Trim().Length > 500) errors["title"] = ["A title of 1–500 characters is required."];
        // Who may be assigned is who can see the project. Anything else lets a member name
        // any account on the instance — confirming that it exists and, through the watcher
        // it becomes, sending it notifications about work in an organization it is not in.
        if (!await AssignableAsync(access, projectId, assigneeId, ct)) errors["assigneeId"] = ["The assignee must be able to see this project."];
        if (!await TeamInProjectAsync(tenancy, teamId, projectId, ct)) errors["teamId"] = ["The team must belong to this project."]; if (stateId is not null && !await db.WorkflowStates.AnyAsync(x => x.Id == stateId && db.Workflows.Any(w => w.Id == x.WorkflowId && w.ProjectId == projectId), ct)) errors["stateId"] = ["State must belong to the project workflow."]; if (points is < 0 || estimate is < 0 || remaining is < 0 || completed is < 0) errors["estimate"] = ["Estimates cannot be negative."]; if (type is WorkItemType.Epic or WorkItemType.Feature && (points is not null || estimate is not null || remaining is not null || completed is not null)) errors["estimate"] = ["Epics and Features are estimated by rollup."]; if (type == WorkItemType.Story && (estimate is not null || remaining is not null || completed is not null)) errors["estimate"] = ["Stories use points, not hours."]; if (type == WorkItemType.Task && points is not null) errors["points"] = ["Tasks use hours, not points."]; var parentError = await ValidateParentAsync(db, itemId, type, parentId, projectId, ct); if (parentError is not null) errors["parentId"] = [parentError]; return errors; }
    /// <summary>Null is unassigned; anyone else has to be in the project's audience.</summary>
    internal static async Task<bool> AssignableAsync(IProjectAccess access, Guid projectId, string? assigneeId, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(assigneeId) || (await access.ListProjectMemberIdsAsync(projectId, ct)).Contains(assigneeId, StringComparer.Ordinal);
    /// <summary>Null is "no team"; a team id must name a team of this project (the tenant filter already keeps it to this organization).</summary>
    internal static async Task<bool> TeamInProjectAsync(TenancyDbContext tenancy, Guid? teamId, Guid projectId, CancellationToken ct) =>
        teamId is null || await tenancy.Teams.AsNoTracking().AnyAsync(t => t.Id == teamId && t.ProjectId == projectId, ct);
    private static async Task<string?> ValidateParentAsync(WorkItemsDbContext db, Guid? itemId, WorkItemType type, Guid? parentId, Guid projectId, CancellationToken ct) { if (type == WorkItemType.Epic && parentId is not null) return "Epics cannot have a parent."; if (parentId is null) return ItemHierarchy.RequiresParent(type) ? "Features need an Epic parent and Tasks a Story or Bug parent." : null; if (itemId == parentId) return "An item cannot be its own parent."; var parent = await db.Items.AsNoTracking().FirstOrDefaultAsync(x => x.Id == parentId, ct); if (parent is null || parent.ProjectId != projectId) return "The parent must be in the same project."; var valid = ItemHierarchy.Allows(parent.Type, type); if (!valid) return "That parent type is not allowed for this item."; var cursor = parent; while (itemId is not null && cursor.ParentId is { } next) { if (next == itemId) return "An item cannot be parented beneath itself."; var ancestor = await db.Items.AsNoTracking().FirstOrDefaultAsync(x => x.Id == next, ct); if (ancestor is null) break; cursor = ancestor; } return null; }
    private static async Task<bool> ChildrenCompatibleAsync(WorkItemsDbContext db, Guid itemId, WorkItemType newType, CancellationToken ct) { var children = await db.Items.AsNoTracking().Where(x => x.ParentId == itemId).ToListAsync(ct); return children.All(x => ItemHierarchy.Allows(newType, x.Type)); }
    internal static async Task<Dictionary<Guid, WorkflowState>> StatesAsync(WorkItemsDbContext db, IEnumerable<WorkItem> items, CancellationToken ct) { var ids = items.Select(x => x.StateId).Distinct().ToArray(); return await db.WorkflowStates.AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct); }
    internal static async Task<List<WorkItemView>> ViewsAsync(WorkItemsDbContext db, string currentUserId, IEnumerable<WorkItem> source, IReadOnlyDictionary<Guid, WorkflowState> states, CancellationToken ct) { var items = source.ToList(); var ids = items.Select(x => x.Id).ToArray(); var children = await db.Items.AsNoTracking().Where(x => x.ParentId != null && ids.Contains(x.ParentId.Value)).Select(x => new { x.ParentId, x.Points, x.RemainingHours, x.StateId }).ToListAsync(ct); var completed = (await db.WorkflowStates.AsNoTracking().Where(x => x.Category == WorkflowStateCategory.Completed).Select(x => x.Id).ToListAsync(ct)).ToHashSet(); var childrenByParent = children.ToLookup(c => c.ParentId!.Value); var labels = await ItemLabelsAsync(db, ids, ct); var blocked = await db.ItemRelations.AsNoTracking().Where(x => ids.Contains(x.TargetId) && x.Kind == ItemRelationKind.Blocks).Select(x => x.TargetId).ToHashSetAsync(ct); var watching = await db.ItemWatchers.AsNoTracking().Where(x => ids.Contains(x.ItemId) && x.MutedAt == null).GroupBy(x => x.ItemId).Select(x => new { ItemId = x.Key, Count = x.Count(), IsWatching = x.Any(w => w.UserId == currentUserId) }).ToDictionaryAsync(x => x.ItemId); return items.Select(x => { var kids = childrenByParent[x.Id].ToList(); var rollup = new ItemRollup(kids.Count, kids.Count(c => completed.Contains(c.StateId)), kids.Sum(c => c.Points ?? 0), kids.Where(c => completed.Contains(c.StateId)).Sum(c => c.Points ?? 0), kids.Sum(c => c.RemainingHours ?? 0)); var state = states[x.StateId]; var itemWatching = watching.GetValueOrDefault(x.Id); return new WorkItemView(x.Id, x.Key, x.Type, x.Title, x.DescriptionMarkdown, x.DescriptionHtml, x.StateId, x.BoardColumnId, state.Category, x.Priority, x.AssigneeId, x.TeamId, x.SprintId, x.ParentId, x.Points, x.EstimateHours, x.RemainingHours, x.CompletedHours, x.DueDate, x.CreatedAt, x.UpdatedAt, x.Version, rollup, labels.GetValueOrDefault(x.Id) ?? [], blocked.Contains(x.Id), itemWatching?.IsWatching ?? false, itemWatching?.Count ?? 0, x.ClaimedBy, x.ClaimedAt, x.ClaimHeartbeatAt); }).ToList(); }
    // One batched query for the whole page/set rather than one per item — the itemCount-
    // style N+1 the ticket calls out by name.
    private static async Task<Dictionary<Guid, List<ItemLabelView>>> ItemLabelsAsync(WorkItemsDbContext db, IReadOnlyCollection<Guid> itemIds, CancellationToken ct)
    {
        if (itemIds.Count == 0) return [];
        var rows = await db.ItemLabels.AsNoTracking().Where(x => itemIds.Contains(x.ItemId))
            .Join(db.Labels.AsNoTracking(), l => l.LabelId, label => label.Id, (l, label) => new { l.ItemId, label.Id, label.Name, label.Color, label.Group })
            .ToListAsync(ct);
        return rows.GroupBy(x => x.ItemId).ToDictionary(g => g.Key, g => g
            .OrderBy(x => x.Group is null ? 1 : 0).ThenBy(x => x.Group, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => new ItemLabelView(x.Id, x.Name, x.Color, x.Group)).ToList());
    }
    private static async Task<string?> ValidateLabelIdsAsync(WorkItemsDbContext db, Guid projectId, IReadOnlyList<Guid> labelIds, CancellationToken ct)
    {
        var distinct = labelIds.Distinct().ToList();
        var validCount = await db.Labels.CountAsync(x => x.ProjectId == projectId && distinct.Contains(x.Id), ct);
        return validCount == distinct.Count ? null : "One or more labels do not belong to this project.";
    }
    // Markdown is rendered on the server and stripped of active content before caching.
    // The whitelist deliberately allows only harmless document markup that Markdig emits.
    internal static string Render(string markdown)
    {
        var html = Markdig.Markdown.ToHtml(markdown, Markdown);
        html = Regex.Replace(html, "<(script|style|iframe|object|embed)[^>]*>[\\s\\S]*?</\\1>", "", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, "\\s+on[a-z]+\\s*=\\s*(['\\\"]).*?\\1", "", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, "\\s+(href|src)\\s*=\\s*(['\\\"])\\s*(javascript|data):.*?\\2", "", RegexOptions.IgnoreCase);
        // Images are attachment bytes, not an arbitrary tracking/external-content channel.
        // A relative API URL is same-origin regardless of deployment host or path base.
        return Regex.Replace(html, "<img(?<before>[^>]*?)\\s+src\\s*=\\s*(?<q>['\\\"])(?<src>.*?)\\k<q>(?<after>[^>]*)>", match =>
            Regex.IsMatch(match.Groups["src"].Value, @"^/api/v1/(?:orgs/[^/]+/)?attachments/[0-9a-fA-F-]{36}/download$")
                ? match.Value : "", RegexOptions.IgnoreCase);
    }
    private static async Task<PagedResult<ItemHistoryEventView>> HistoryPageAsync(IQueryable<HistoryRow> source, IUserDirectory directory, int page, int pageSize, CancellationToken ct)
    {
        var take = Math.Clamp(pageSize == 0 ? 25 : pageSize, 1, 100); var currentPage = Math.Max(1, page);
        // `HistoryRow` is a projection record, so order after materializing it: EF can
        // translate the projection but not a member access on its constructor expression.
        var rows = await source.ToListAsync(ct);
        var groups = rows.OrderByDescending(x => x.At).ThenByDescending(x => x.Id)
            .GroupBy(x => x.EventId).OrderByDescending(x => x.Max(row => row.At)).ToList();
        var pageGroups = groups.Skip((currentPage - 1) * take).Take(take).ToList();
        var actors = await directory.GetAsync(pageGroups.Select(x => x.First().ActorId).Distinct().ToArray(), ct);
        var events = pageGroups.Select(group =>
        {
            var first = group.First();
            return new ItemHistoryEventView(first.EventId, first.ItemKey, actors.GetValueOrDefault(first.ActorId), first.At,
                group.OrderBy(x => x.Id).Select(row => new ItemHistoryChangeView(row.Id, row.Field, Json(row.OldValue), Json(row.NewValue))).ToList());
        }).ToList();
        return new PagedResult<ItemHistoryEventView>(events, currentPage, take, groups.Count);
    }
    private static JsonElement Json(string? value) => JsonDocument.Parse(value ?? "null").RootElement.Clone();
    private static IResult Conflict() => Results.Problem("The item was modified by someone else.", statusCode: 409, type: ProblemTypes.Conflict);
    private static IResult InvalidParent(string message) => Results.Problem(title: "Invalid parent.", detail: message, type: "https://aictiq.com/problems/invalid-parent", statusCode: 409);
}

public enum LabelRequirementKind { Any, All, None }

/// <summary>One `label:`/`-label:` term. Several terms in one filter AND together.</summary>
public sealed record LabelRequirement(LabelRequirementKind Kind, IReadOnlyList<string> Names);

public enum AssigneeRequirementKind { CurrentUser, None, User }

public enum ClaimRequirementKind { Any, None, CurrentUser, Agent, User }

/// <summary>
/// One <c>claimed:</c> term. Separate from the assignee requirement because a claim is not
/// an assignment: it is a lease an agent holds while it works, and "who is an agent
/// working on right now" is the question the presence surfaces ask. <c>@agent</c> is
/// resolved through <see cref="IUserDirectory.FilterAgentsAsync"/>, because whether an
/// account is an agent lives in Identity's schema.
/// </summary>
public sealed record ClaimRequirement(ClaimRequirementKind Kind, string? UserId = null);

/// <summary>The assignee term keeps <c>@me</c> server-owned instead of relying on a
/// client to know its own user id.</summary>
public sealed record AssigneeRequirement(AssigneeRequirementKind Kind, string? UserId = null);

public enum HourFilterComparison { None, GreaterThan, GreaterThanOrEqual, LessThan, LessThanOrEqual, Equal }
public sealed record HourFilter(HourFilterComparison Comparison, decimal? Value);

public sealed record ItemFilter(IReadOnlySet<WorkflowStateCategory>? Categories, WorkItemType? Type, WorkItemPriority? MinimumPriority, IReadOnlyList<LabelRequirement>? LabelRequirements, string? Error, bool? Blocked = null, HourFilter? EstimateHours = null, HourFilter? RemainingHours = null, AssigneeRequirement? Assignee = null, string? Sprint = null, ClaimRequirement? Claimed = null)
{
    public static ItemFilter Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new(null, null, null, null, null);
        var categories = new HashSet<WorkflowStateCategory>(); WorkItemType? type = null; WorkItemPriority? priority = null; bool? blocked = null; HourFilter? estimate = null; HourFilter? remaining = null; AssigneeRequirement? assignee = null; ClaimRequirement? claimed = null; string? sprint = null; var labels = new List<LabelRequirement>();
        foreach (var token in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = token.Split(':', 2);
            if (pair.Length != 2) return new(null, null, null, null, $"Invalid filter term '{token}'.");
            if (pair[0] == "type") { if (!Enum.TryParse<WorkItemType>(pair[1], true, out var value)) return new(null, null, null, null, "Unknown item type."); type = value; }
            else if (pair[0] == "priority") { if (!Enum.TryParse<WorkItemPriority>(pair[1].TrimStart('>', '='), true, out var value)) return new(null, null, null, null, "Unknown priority."); priority = value; }
            else if (pair[0] == "state") { foreach (var name in pair[1].Split(',', StringSplitOptions.RemoveEmptyEntries)) { if (!Enum.TryParse<WorkflowStateCategory>(name, true, out var value)) return new(null, null, null, null, "Unknown state category."); categories.Add(value); } }
            else if (pair[0] == "assignee")
            {
                var value = pair[1].Trim();
                if (string.IsNullOrEmpty(value)) return new(null, null, null, null, "An assignee filter requires a value.");
                assignee = value.Equals("@me", StringComparison.OrdinalIgnoreCase)
                    ? new(AssigneeRequirementKind.CurrentUser)
                    : value.Equals("none", StringComparison.OrdinalIgnoreCase)
                        ? new(AssigneeRequirementKind.None)
                        : new(AssigneeRequirementKind.User, value);
            }
            else if (pair[0] == "label" || pair[0] == "-label")
            {
                if (string.IsNullOrEmpty(pair[1])) return new(null, null, null, null, "A label filter requires at least one name.");
                var hasPlus = pair[1].Contains('+'); var hasComma = pair[1].Contains(',');
                if (hasPlus && hasComma) return new(null, null, null, null, "Combine label names with either ',' or '+', not both.");
                var names = pair[1].Split(hasPlus ? '+' : ',').Select(n => n.Trim()).ToArray();
                if (names.Any(string.IsNullOrEmpty)) return new(null, null, null, null, "A label filter cannot contain an empty name.");
                labels.Add(new LabelRequirement(pair[0] == "-label" ? LabelRequirementKind.None : hasPlus ? LabelRequirementKind.All : LabelRequirementKind.Any, names));
            }
            else if (pair[0] == "claimed")
            {
                var value = pair[1].Trim();
                claimed = value.ToLowerInvariant() switch
                {
                    "any" => new(ClaimRequirementKind.Any),
                    "none" => new(ClaimRequirementKind.None),
                    "@me" => new(ClaimRequirementKind.CurrentUser),
                    "@agent" or "@agents" => new(ClaimRequirementKind.Agent),
                    // Only an id shaped like one is taken as a user. Anything else is a
                    // typo, and treating a typo as "claimed by nobody in particular" would
                    // answer an empty list — which reads as "nothing is claimed".
                    _ => Guid.TryParse(value, out _) ? new ClaimRequirement(ClaimRequirementKind.User, value) : null,
                };
                if (claimed is null) return new(null, null, null, null, "claimed must be any, none, @me, @agent, or a user id.");
            }
            else if (pair[0] == "blocked") { if (!bool.TryParse(pair[1], out var value)) return new(null, null, null, null, "blocked must be true or false."); blocked = value; }
            else if (pair[0] == "sprint") { var value = pair[1].Trim(); if (!value.Equals("current", StringComparison.OrdinalIgnoreCase) && !value.Equals("next", StringComparison.OrdinalIgnoreCase) && !value.Equals("backlog", StringComparison.OrdinalIgnoreCase) && !Guid.TryParse(value, out _)) return new(null, null, null, null, "sprint must be current, next, backlog, or a sprint id."); sprint = value; }
            else if (pair[0] is "estimate" or "remaining")
            {
                if (!TryHourFilter(pair[1], out var hours)) return new(null, null, null, null, $"Invalid {pair[0]} hours filter.");
                if (pair[0] == "estimate") estimate = hours; else remaining = hours;
            }
            else return new(null, null, null, null, $"Unknown filter field '{pair[0]}'.");
        }
        return new(categories.Count == 0 ? null : categories, type, priority, labels.Count == 0 ? null : labels, null, blocked, estimate, remaining, assignee, sprint, claimed);
    }
    public IQueryable<WorkItem> Apply(IQueryable<WorkItem> query)
    {
        if (Type is { } type) query = query.Where(x => x.Type == type);
        if (MinimumPriority is { } priority) query = query.Where(x => x.Priority >= priority);
        query = ApplyEstimateHours(query, EstimateHours);
        return ApplyRemainingHours(query, RemainingHours);
    }
    private static bool TryHourFilter(string text, out HourFilter filter)
    {
        filter = new(HourFilterComparison.Equal, null);
        if (text.Equals("none", StringComparison.OrdinalIgnoreCase)) { filter = new(HourFilterComparison.None, null); return true; }
        var (comparison, value) = text.StartsWith(">=") ? (HourFilterComparison.GreaterThanOrEqual, text[2..]) : text.StartsWith("<=") ? (HourFilterComparison.LessThanOrEqual, text[2..]) : text.StartsWith('>') ? (HourFilterComparison.GreaterThan, text[1..]) : text.StartsWith('<') ? (HourFilterComparison.LessThan, text[1..]) : text.StartsWith('=') ? (HourFilterComparison.Equal, text[1..]) : (HourFilterComparison.Equal, text);
        if (!decimal.TryParse(value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var hours) || hours < 0) return false;
        filter = new(comparison, hours); return true;
    }
    private static IQueryable<WorkItem> ApplyEstimateHours(IQueryable<WorkItem> query, HourFilter? filter)
    {
        if (filter is null) return query;
        return filter.Comparison switch
        {
        HourFilterComparison.None => query.Where(x => x.EstimateHours == null),
        HourFilterComparison.GreaterThan => query.Where(x => x.EstimateHours > filter.Value),
        HourFilterComparison.GreaterThanOrEqual => query.Where(x => x.EstimateHours >= filter.Value),
        HourFilterComparison.LessThan => query.Where(x => x.EstimateHours < filter.Value),
        HourFilterComparison.LessThanOrEqual => query.Where(x => x.EstimateHours <= filter.Value),
        _ => query.Where(x => x.EstimateHours == filter!.Value)
        };
    }
    private static IQueryable<WorkItem> ApplyRemainingHours(IQueryable<WorkItem> query, HourFilter? filter)
    {
        if (filter is null) return query;
        return filter.Comparison switch
        {
        HourFilterComparison.None => query.Where(x => x.RemainingHours == null),
        HourFilterComparison.GreaterThan => query.Where(x => x.RemainingHours > filter.Value),
        HourFilterComparison.GreaterThanOrEqual => query.Where(x => x.RemainingHours >= filter.Value),
        HourFilterComparison.LessThan => query.Where(x => x.RemainingHours < filter.Value),
        HourFilterComparison.LessThanOrEqual => query.Where(x => x.RemainingHours <= filter.Value),
        _ => query.Where(x => x.RemainingHours == filter!.Value)
        };
    }
}

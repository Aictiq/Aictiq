using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Aictiq.Modules.WorkItems.Domain;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Tenancy;

namespace Aictiq.Modules.WorkItems.Endpoints;

/// <param name="Kind">`changed` for a history event, `commented` for a comment.</param>
/// <param name="Summary">
/// Already rendered for a feed: the fields that changed, or the first line of the comment.
/// The caller is a sidebar, not a diff viewer — <c>GET /items/{key}/history</c> is where
/// the full change record lives.
/// </param>
public sealed record AgentActivityEntry(
    string Kind, string ItemKey, string ItemTitle, UserSummary? Actor, DateTimeOffset At, string Summary);

/// <param name="CompletedByAgents">Items an agent finished, of <paramref name="CompletedTotal"/>.</param>
public sealed record AgentContribution(
    int CompletedByAgents, int CompletedTotal, IReadOnlyList<AgentContributionRow> ByAgent);

public sealed record AgentContributionRow(UserSummary Agent, int Completed, int InProgress);

/// <summary>
/// What the agents in this organization are doing.
///
/// It lives in WorkItems rather than beside the Agents screen's other endpoints because
/// the answer is entirely work-item data; Tenancy owns *who* the agents are and asks
/// Identity, and this asks Identity the same question through the same contract. Both
/// routes are bounded by <see cref="IProjectAccess.ListVisibleProjectIdsAsync"/> before
/// anything is paged, so a Guest sees an organization's agents only through the projects
/// they can already read.
/// </summary>
public static class AgentActivityEndpoints
{
    public static IEndpointRouteBuilder MapAgentActivityEndpoints(this IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/orgs/{orgSlug}/agent-activity").WithTags("Agents").RequireAuthorization();
        group.MapGet("/", Feed).RequireOrgRole(OrgRole.Guest).RequireScope(Scopes.Read);
        group.MapGet("/contribution", Contribution).RequireOrgRole(OrgRole.Guest).RequireScope(Scopes.Read);
        return api;
    }

    /// <param name="actorId">One agent — or one person; the feed does not care which.</param>
    /// <param name="agentsOnly">
    /// The `actor:agents` filter. Distinct from <paramref name="actorId"/> because
    /// "everything the agents did" is the question a stakeholder asks, and it has no single
    /// id to name.
    /// </param>
    private static async Task<IResult> Feed(
        HttpContext http, WorkItemsDbContext db, ICurrentTenant tenant, ICurrentUser user,
        IProjectAccess access, IUserDirectory directory,
        string? actorId, bool agentsOnly = false, int limit = 50, CancellationToken ct = default)
    {
        var projectIds = await access.ListVisibleProjectIdsAsync(user.UserId!, tenant.OrganizationId!.Value, ct);
        if (projectIds.Count == 0) return Results.Ok(Array.Empty<AgentActivityEntry>());
        var take = Math.Clamp(limit, 1, 200);

        var history = db.ItemHistory.AsNoTracking()
            .Join(db.Items.AsNoTracking(), h => h.ItemId, i => i.Id, (h, i) => new { h.EventId, h.ActorId, h.At, h.Field, i.Key, i.Title, i.ProjectId })
            .Where(x => projectIds.Contains(x.ProjectId));
        var comments = db.Comments.AsNoTracking()
            .Join(db.Items.AsNoTracking(), c => c.ItemId, i => i.Id, (c, i) => new { c.AuthorId, c.CreatedAt, c.BodyMarkdown, c.DeletedAt, i.Key, i.Title, i.ProjectId })
            .Where(x => x.DeletedAt == null && projectIds.Contains(x.ProjectId));

        if (actorId is not null)
        {
            history = history.Where(x => x.ActorId == actorId);
            comments = comments.Where(x => x.AuthorId == actorId);
        }
        else if (agentsOnly)
        {
            // Resolved to a set of ids and pushed into the query, rather than filtering the
            // page afterwards: an organization whose last two hundred events were all
            // people's would otherwise report that its agents had done nothing.
            var actorIds = await history.Select(x => x.ActorId)
                .Union(comments.Select(x => x.AuthorId)).Distinct().ToListAsync(ct);
            var agents = (await directory.FilterAgentsAsync(actorIds, ct)).ToArray();
            history = history.Where(x => agents.Contains(x.ActorId));
            comments = comments.Where(x => agents.Contains(x.AuthorId));
        }

        // Each source is capped before the merge: a busy organization's history dwarfs its
        // comments, and taking `limit` from the union would read both tables in full.
        // History is taken wider because it is grouped by event afterwards.
        var historyRows = await history.OrderByDescending(x => x.At).Take(take * 4).ToListAsync(ct);
        var commentRows = await comments.OrderByDescending(x => x.CreatedAt).Take(take).ToListAsync(ct);

        var actors = await directory.GetAsync(
            [.. historyRows.Select(x => x.ActorId).Concat(commentRows.Select(x => x.AuthorId)).Distinct()], ct);

        var entries = historyRows
            // One entry per event, not per changed field: a single save writes a row per
            // field, and a feed that listed them separately would read as five actions.
            .GroupBy(x => x.EventId)
            .Select(g => new AgentActivityEntry("changed", g.First().Key, g.First().Title,
                actors.GetValueOrDefault(g.First().ActorId), g.Max(x => x.At),
                string.Join(", ", g.Select(x => x.Field).Distinct())))
            .Concat(commentRows
                .Select(x => new AgentActivityEntry("commented", x.Key, x.Title,
                    actors.GetValueOrDefault(x.AuthorId), x.CreatedAt, FirstLine(x.BodyMarkdown))))
            .OrderByDescending(x => x.At)
            .Take(take)
            .ToList();

        return Results.Ok(entries);
    }

    /// <summary>
    /// How much of the current sprints' finished work an agent did. Deliberately a count
    /// rather than a rate: phase 7 owns analytics, and a tile that invented its own
    /// definition of throughput would have to be unlearned when it arrives.
    /// </summary>
    private static async Task<IResult> Contribution(
        HttpContext http, WorkItemsDbContext db, ICurrentTenant tenant, ICurrentUser user,
        IProjectAccess access, IUserDirectory directory, CancellationToken ct = default)
    {
        var projectIds = await access.ListVisibleProjectIdsAsync(user.UserId!, tenant.OrganizationId!.Value, ct);
        if (projectIds.Count == 0) return Results.Ok(new AgentContribution(0, 0, []));

        var activeSprints = await db.Sprints.AsNoTracking().Where(x => x.State == SprintState.Active).Select(x => x.Id).ToListAsync(ct);
        var completedStates = await db.WorkflowStates.AsNoTracking()
            .Where(x => x.Category == WorkflowStateCategory.Completed).Select(x => x.Id).ToListAsync(ct);
        var activeStates = await db.WorkflowStates.AsNoTracking()
            .Where(x => x.Category == WorkflowStateCategory.Active).Select(x => x.Id).ToListAsync(ct);

        var rows = await db.Items.AsNoTracking()
            .Where(x => projectIds.Contains(x.ProjectId) && x.SprintId != null && activeSprints.Contains(x.SprintId.Value))
            .Select(x => new { x.Id, x.AssigneeId, x.StateId })
            .ToListAsync(ct);

        // A factory run completes an item without being its assignee: the person who
        // dispatched it usually still is. The latest successful run on a completed item
        // credits its agent, so the tile counts that work too.
        var completedIds = rows.Where(x => completedStates.Contains(x.StateId)).Select(x => x.Id).ToList();
        var runRows = completedIds.Count == 0
            ? []
            : await db.ItemHistory.AsNoTracking()
                .Where(x => x.Field == "run-finished" && completedIds.Contains(x.ItemId))
                .OrderByDescending(x => x.At)
                .Select(x => new { x.ItemId, x.ActorId, x.NewValue })
                .ToListAsync(ct);
        var completedByRun = runRows
            .Where(x => RunSucceeded(x.NewValue))
            .GroupBy(x => x.ItemId)
            .ToDictionary(g => g.Key, g => g.First().ActorId);

        var assignees = rows.Where(x => x.AssigneeId != null).Select(x => x.AssigneeId!)
            .Concat(completedByRun.Values).Distinct().ToArray();
        var agentIds = await directory.FilterAgentsAsync(assignees, ct);
        var agents = await directory.GetAsync([.. agentIds], ct);

        // Whoever completed it: an agent assignee first, else the agent whose run did.
        string? CompletedBy(Guid itemId, string? assigneeId) =>
            assigneeId is not null && agents.ContainsKey(assigneeId) ? assigneeId : completedByRun.GetValueOrDefault(itemId);

        var completedTotal = rows.Count(x => completedStates.Contains(x.StateId));
        var byAgent = agents.Values
            .Select(agent => new AgentContributionRow(agent,
                rows.Count(x => completedStates.Contains(x.StateId) && CompletedBy(x.Id, x.AssigneeId) == agent.Id),
                rows.Count(x => x.AssigneeId == agent.Id && activeStates.Contains(x.StateId))))
            .Where(x => x.Completed > 0 || x.InProgress > 0)
            .OrderByDescending(x => x.Completed).ThenByDescending(x => x.InProgress)
            .ToList();

        return Results.Ok(new AgentContribution(byAgent.Sum(x => x.Completed), completedTotal, byAgent));
    }

    /// <summary>The <c>run-finished</c> history row's outcome, as <see cref="Events.RunFinishedHandler"/> writes it.</summary>
    private static bool RunSucceeded(string? newValue)
    {
        if (string.IsNullOrEmpty(newValue)) return false;
        try
        {
            using var document = JsonDocument.Parse(newValue);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("Outcome", out var outcome)
                && outcome.ValueKind == JsonValueKind.String
                && outcome.GetString() == RunOutcomes.Succeeded;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string FirstLine(string markdown)
    {
        // An agent's progress comment leads with an HTML marker comment; showing it would
        // make every one of its rows read as empty.
        var line = markdown.Split('\n').FirstOrDefault(x => x.Trim().Length > 0 && !x.TrimStart().StartsWith("<!--", StringComparison.Ordinal))?.Trim() ?? "";
        return line.Length <= 140 ? line : $"{line[..139]}…";
    }
}

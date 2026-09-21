using Microsoft.EntityFrameworkCore;
using Aictiq.Modules.WorkItems.Domain;
using Aictiq.SharedKernel.Contracts;

namespace Aictiq.Modules.WorkItems.Endpoints;

/// <summary>
/// The filter grammar, the search box and the sort, applied in the database. Every surface
/// that takes a filter — the item list, the board, the backlog, CSV export and MCP — goes
/// through here, so a term means the same thing everywhere. A surface that applied only the
/// terms it knew would accept the rest and silently ignore them, and "assignee:@me" returning
/// everyone reads as a filter that works.
/// </summary>
public static class ItemQueries
{
    /// <summary>Applies every term of <paramref name="filter"/>; the error is a message for the <c>filter</c> field.</summary>
    public static async Task<(IQueryable<WorkItem> Query, string? Error)> ApplyFilterAsync(IQueryable<WorkItem> query, ItemFilter filter,
        Guid projectId, WorkItemsDbContext db, IUserDirectory directory, string? userId, CancellationToken ct)
    {
        if (filter.Categories is { Count: > 0 } categories)
        {
            var stateIds = await db.WorkflowStates.Where(x => categories.Contains(x.Category)).Select(x => x.Id).ToListAsync(ct);
            query = query.Where(x => stateIds.Contains(x.StateId));
        }
        if (filter.LabelRequirements is { Count: > 0 } requirements)
        {
            var byName = await db.Labels.Where(x => x.ProjectId == projectId).ToDictionaryAsync(x => x.Name, x => x.Id, StringComparer.OrdinalIgnoreCase, ct);
            foreach (var requirement in requirements)
            {
                var ids = new List<Guid>();
                foreach (var name in requirement.Names)
                {
                    if (!byName.TryGetValue(name, out var labelId)) return (query, $"Unknown label '{name}'.");
                    ids.Add(labelId);
                }
                query = requirement.Kind switch
                {
                    LabelRequirementKind.Any => query.Where(x => db.ItemLabels.Any(l => l.ItemId == x.Id && ids.Contains(l.LabelId))),
                    LabelRequirementKind.None => query.Where(x => !db.ItemLabels.Any(l => l.ItemId == x.Id && ids.Contains(l.LabelId))),
                    // "all" cannot be expressed as one Contains, so each required label gets
                    // its own EXISTS clause, ANDed together by successive Where calls.
                    _ => ids.Aggregate(query, (q, labelId) => q.Where(x => db.ItemLabels.Any(l => l.ItemId == x.Id && l.LabelId == labelId))),
                };
            }
        }
        query = filter.Apply(query);
        if (filter.Sprint is { } sprintFilter)
        {
            if (sprintFilter.Equals("backlog", StringComparison.OrdinalIgnoreCase)) query = query.Where(x => x.SprintId == null);
            else if (sprintFilter.Equals("current", StringComparison.OrdinalIgnoreCase))
            {
                var active = await db.Sprints.Where(x => x.State == SprintState.Active).Select(x => x.Id).ToListAsync(ct);
                query = query.Where(x => x.SprintId != null && active.Contains(x.SprintId.Value));
            }
            else if (sprintFilter.Equals("next", StringComparison.OrdinalIgnoreCase))
            {
                var next = await db.Sprints.Where(x => x.State == SprintState.Planned).GroupBy(x => x.TeamId).Select(x => x.OrderBy(y => y.StartsOn).Select(y => y.Id).First()).ToListAsync(ct);
                query = query.Where(x => x.SprintId != null && next.Contains(x.SprintId.Value));
            }
            else if (Guid.TryParse(sprintFilter, out var sprintId)) query = query.Where(x => x.SprintId == sprintId);
        }
        if (filter.Assignee is { } assignee)
        {
            query = assignee.Kind switch
            {
                AssigneeRequirementKind.CurrentUser => query.Where(x => x.AssigneeId == userId),
                AssigneeRequirementKind.None => query.Where(x => x.AssigneeId == null),
                _ => query.Where(x => x.AssigneeId == assignee.UserId),
            };
        }
        if (filter.Claimed is { } claimed)
        {
            if (claimed.Kind == ClaimRequirementKind.Agent)
            {
                // Whether an account is an agent lives in Identity's schema, so the
                // project's distinct claimants go there and the matching set comes back —
                // the same shape as IUserDirectory.SearchAsync, and applied before paging
                // because a page filtered afterwards would report the wrong total.
                var claimants = await query.Where(x => x.ClaimedBy != null).Select(x => x.ClaimedBy!).Distinct().ToListAsync(ct);
                var agents = await directory.FilterAgentsAsync(claimants, ct);
                query = query.Where(x => x.ClaimedBy != null && agents.Contains(x.ClaimedBy));
            }
            else
            {
                query = claimed.Kind switch
                {
                    ClaimRequirementKind.Any => query.Where(x => x.ClaimedBy != null),
                    ClaimRequirementKind.None => query.Where(x => x.ClaimedBy == null),
                    ClaimRequirementKind.CurrentUser => query.Where(x => x.ClaimedBy == userId),
                    _ => query.Where(x => x.ClaimedBy == claimed.UserId),
                };
            }
        }
        if (filter.Blocked is { } blocked)
            query = blocked ? query.Where(x => db.ItemRelations.Any(r => r.TargetId == x.Id && r.Kind == ItemRelationKind.Blocks))
                : query.Where(x => !db.ItemRelations.Any(r => r.TargetId == x.Id && r.Kind == ItemRelationKind.Blocks));
        return (query, null);
    }

    /// <summary>Narrows to the items the search box matches; a blank query changes nothing.</summary>
    public static async Task<IQueryable<WorkItem>> ApplySearchAsync(IQueryable<WorkItem> query, string? q, Guid projectId,
        WorkItemsDbContext db, CancellationToken ct)
    {
        if (SearchQuery.Normalize(q) is not { } search) return query;
        var matchingIds = await SearchQuery.ItemIdsAsync(db, [projectId], search, ct);
        return query.Where(item => matchingIds.Contains(item.Id));
    }

    /// <summary>The fields a list may be sorted by, and the direction each one means when no direction is given.</summary>
    public static readonly IReadOnlyDictionary<string, bool> SortFields = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
    {
        ["rank"] = false, ["number"] = false, ["key"] = false, ["title"] = false, ["state"] = false,
        ["priority"] = true, ["estimate"] = false, ["remaining"] = false, ["due"] = false,
        ["updated"] = true, ["created"] = true,
    };

    /// <summary>
    /// <c>field</c> or <c>field:asc|desc</c>. A bare field keeps the direction it has always
    /// had (priority, updated and created put the most urgent or recent first), so
    /// <c>--sort priority</c> in a script means what it meant before directions existed.
    /// Every order ends on the item number: without a unique tiebreaker Postgres may order
    /// equal rows differently per query, and page two would repeat or skip items of page one.
    /// Empty values sort last in either direction.
    /// </summary>
    /// <param name="pinnedNumber">
    /// Without an explicit sort, the item a search named by number or key comes first: someone
    /// who typed "1377" is looking for PROJ2-1377, not for its rank among the items whose
    /// descriptions happen to mention it.
    /// </param>
    public static bool TrySort(IQueryable<WorkItem> query, string? sort, WorkItemsDbContext db, out IQueryable<WorkItem> sorted, out string? error, int? pinnedNumber = null)
    {
        sorted = query; error = null;
        if (string.IsNullOrWhiteSpace(sort))
        {
            sorted = pinnedNumber is { } pinned
                ? query.OrderByDescending(x => x.Number == pinned).ThenBy(x => x.Rank).ThenBy(x => x.Number)
                : query.OrderBy(x => x.Rank).ThenBy(x => x.Number);
            return true;
        }
        var parts = sort.Trim().Split(':', 2);
        if (!SortFields.TryGetValue(parts[0], out var descending))
        {
            error = $"sort must be one of {string.Join(", ", SortFields.Keys)}, optionally followed by :asc or :desc.";
            return false;
        }
        if (parts.Length == 2)
        {
            if (parts[1].Equals("asc", StringComparison.OrdinalIgnoreCase)) descending = false;
            else if (parts[1].Equals("desc", StringComparison.OrdinalIgnoreCase)) descending = true;
            else { error = "A sort direction must be asc or desc."; return false; }
        }
        IOrderedQueryable<WorkItem> Order<TKey>(System.Linq.Expressions.Expression<Func<WorkItem, TKey>> key) =>
            descending ? query.OrderByDescending(key) : query.OrderBy(key);
        IOrderedQueryable<WorkItem> Nullable<TKey>(System.Linq.Expressions.Expression<Func<WorkItem, bool>> isNull, System.Linq.Expressions.Expression<Func<WorkItem, TKey>> key) =>
            descending ? query.OrderBy(isNull).ThenByDescending(key) : query.OrderBy(isNull).ThenBy(key);
        // A state sorts by where it sits in the workflow, not by its name: "Active" before
        // "Done" is the order a person reads a board in, and alphabetical is not.
        var ordered = parts[0].ToLowerInvariant() switch
        {
            "rank" => Order(x => x.Rank),
            "number" or "key" => Order(x => x.Number),
            "title" => Order(x => x.Title),
            "state" => descending
                ? query.OrderByDescending(x => db.WorkflowStates.Where(s => s.Id == x.StateId).Select(s => (int)s.Category).FirstOrDefault())
                    .ThenByDescending(x => db.WorkflowStates.Where(s => s.Id == x.StateId).Select(s => s.Position).FirstOrDefault())
                : query.OrderBy(x => db.WorkflowStates.Where(s => s.Id == x.StateId).Select(s => (int)s.Category).FirstOrDefault())
                    .ThenBy(x => db.WorkflowStates.Where(s => s.Id == x.StateId).Select(s => s.Position).FirstOrDefault()),
            "priority" => Order(x => x.Priority),
            "estimate" => Nullable(x => x.EstimateHours == null, x => x.EstimateHours),
            "remaining" => Nullable(x => x.RemainingHours == null, x => x.RemainingHours),
            "due" => Nullable(x => x.DueDate == null, x => x.DueDate),
            "updated" => Order(x => x.UpdatedAt),
            _ => Order(x => x.CreatedAt),
        };
        sorted = ordered.ThenBy(x => x.Number);
        return true;
    }
}

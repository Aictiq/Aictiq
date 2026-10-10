using Aictiq.Modules.Automation.Domain;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Aictiq.Modules.Automation.Endpoints;

/// <summary>
/// The filters the organization's runs list and its statistics share, bound from the query
/// string. One shape for both, so "the failed runs on PROJ this week" counts exactly the
/// runs it lists.
/// </summary>
/// <param name="Project">Project key.</param>
/// <param name="Agent">Agent user id.</param>
/// <param name="Status">A <see cref="RunStatus"/> name, case-insensitive.</param>
/// <param name="Item">Item key, e.g. <c>PROJ-12</c>.</param>
/// <param name="Kind">A <see cref="RunKind"/> name: <c>implement</c>, <c>refine</c> or <c>chat</c>.</param>
/// <param name="Playbook">Playbook id.</param>
/// <param name="Runner">The runner that took the run.</param>
/// <param name="From">Runs queued at or after this instant.</param>
/// <param name="To">Runs queued before this instant.</param>
/// <param name="Failure">
/// Failed and timed-out runs with exactly this failure reason. The reason is operator detail,
/// so for anyone else the filter matches nothing rather than letting them probe for it.
/// </param>
public sealed record RunFilter(
    string? Project = null, string? Agent = null, string? Status = null, string? Item = null,
    string? Kind = null, Guid? Playbook = null, Guid? Runner = null,
    DateTimeOffset? From = null, DateTimeOffset? To = null, string? Failure = null);

/// <summary>
/// What <see cref="RunFilters.ApplyAsync"/> found: a refusal for a filter that makes no sense,
/// or the visible runs in the date range (<see cref="Scope"/>) and those narrowed by every other
/// filter (<see cref="Runs"/>). Both are null when the caller can see nothing that matches.
/// </summary>
internal sealed record FilteredRuns(IResult? Refusal, IQueryable<Run>? Scope, IQueryable<Run>? Runs)
{
    public static readonly FilteredRuns Empty = new(null, null, null);
}

internal static class RunFilters
{
    /// <summary>
    /// Bounds the runs by what the caller can see before anything else - an empty list of
    /// visible projects is "nothing", never "everything" - then applies the filters.
    /// </summary>
    public static async Task<FilteredRuns> ApplyAsync(
        RunFilter filter, AutomationDbContext db, Guid organizationId, string userId,
        IProjectAccess access, CancellationToken ct)
    {
        RunStatus? status = null;
        if (!string.IsNullOrWhiteSpace(filter.Status))
        {
            if (!Enum.TryParse<RunStatus>(filter.Status.Trim(), ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
            {
                return Refuse("status", "Unknown run status.");
            }
            status = parsed;
        }
        RunKind? kind = null;
        if (!string.IsNullOrWhiteSpace(filter.Kind))
        {
            if (!Enum.TryParse<RunKind>(filter.Kind.Trim(), ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
            {
                return Refuse("kind", "Unknown run kind.");
            }
            kind = parsed;
        }
        if (filter.From is { } from && filter.To is { } to && to < from)
        {
            return Refuse("to", "The end of the range is before its start.");
        }

        var visible = (await access.ListVisibleProjectIdsAsync(userId, organizationId, ct)).ToList();
        if (!string.IsNullOrWhiteSpace(filter.Project))
        {
            var resolved = await access.FindProjectAsync(organizationId, filter.Project.Trim(), ct);
            // A list stays a list: a project the caller cannot see is an empty page, not a 404.
            if (resolved is null || await access.GetProjectRoleAsync(userId, resolved.Id, ct) is null)
            {
                return FilteredRuns.Empty;
            }
            visible = [resolved.Id];
        }
        if (visible.Count == 0)
        {
            return FilteredRuns.Empty;
        }

        var scope = db.Runs.AsNoTracking().Where(run => visible.Contains(run.ProjectId));
        if (filter.From is { } since)
        {
            scope = scope.Where(run => run.QueuedAt >= since);
        }
        if (filter.To is { } until)
        {
            scope = scope.Where(run => run.QueuedAt < until);
        }

        var query = scope;
        if (!string.IsNullOrWhiteSpace(filter.Agent))
        {
            var agent = filter.Agent.Trim();
            query = query.Where(run => run.AgentUserId == agent);
        }
        if (status is { } state)
        {
            query = query.Where(run => run.Status == state);
        }
        if (!string.IsNullOrWhiteSpace(filter.Item))
        {
            var item = filter.Item.Trim();
            query = query.Where(run => run.ItemKey == item);
        }
        if (kind is { } runKind)
        {
            query = query.Where(run => run.Kind == runKind);
        }
        if (filter.Playbook is { } playbook)
        {
            query = query.Where(run => run.PlaybookId == playbook);
        }
        if (filter.Runner is { } runner)
        {
            query = query.Where(run => run.RunnerId == runner);
        }
        if (!string.IsNullOrWhiteSpace(filter.Failure))
        {
            if (!await access.CanOperateFactoryAsync(userId, organizationId, ct))
            {
                return new FilteredRuns(null, scope, null);
            }
            var reason = filter.Failure;
            query = query.Where(run => (run.Status == RunStatus.Failed || run.Status == RunStatus.TimedOut)
                && run.FailureReason == reason);
        }
        return new FilteredRuns(null, scope, query);
    }

    private static FilteredRuns Refuse(string field, string error) =>
        new(Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [error] }, type: ProblemTypes.Validation),
            null, null);
}

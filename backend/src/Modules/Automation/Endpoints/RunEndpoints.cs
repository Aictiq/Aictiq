using Aictiq.Modules.Automation.Domain;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Paging;
using Aictiq.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aictiq.Modules.Automation.Endpoints;

/// <param name="AgentName">The agent's display name, for the board. Null when nobody could say it.</param>
/// <param name="PlaybookName">The playbook's display name, resolved for the run screens. Null when the playbook is gone.</param>
/// <param name="RunnerName">The machine's name, for the run header and the item's claim banner. Null until a runner takes the run.</param>
/// <param name="CancelRequested">A cancel has been asked for; the runner acknowledges it on finish.</param>
/// <param name="RequestedBy">Who dispatched the run, or null when <paramref name="RuleId"/> did instead.</param>
/// <param name="RuleId">The automation rule that dispatched the run, or null for a person.</param>
/// <param name="RuleName">The rule's name, or null once the rule that started this run has been deleted.</param>
public sealed record RunView(
    Guid Id, Guid ProjectId, Guid ItemId, string ItemKey, Guid PlaybookId, string? PlaybookName,
    string AgentId, string? AgentName, string? RequestedBy, Guid? RuleId, string? RuleName,
    Guid? RunnerId, string? RunnerName, RunStatus Status, string Harness,
    Guid? PlaybookRevisionId, int MaxMinutes, DateTimeOffset QueuedAt, DateTimeOffset? AssignedAt,
    DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, DateTimeOffset? LastHeartbeatAt, bool CancelRequested,
    string? OutcomeSummary, string? PullRequestUrl, int? ExitCode, decimal? CostUsd, long? InputTokens,
    long? OutputTokens, string? FailureReason, string? PromptSnapshot, uint Version);

public sealed record DispatchRunRequest(Guid? PlaybookId, string? AgentId);

/// <summary>
/// A person's window on the factory: dispatch, watch, cancel.
///
/// Two visibility tiers, deliberately. Anyone who can see the item can see the run — its
/// status, its agent, its timings, its pull request; the run <em>is</em> the item's
/// history. The raw detail — the prompt snapshot and the failure reason — belongs to the
/// people who operate the factory, the same audience the live log is gated to.
/// </summary>
public static class RunEndpoints
{
    public static IEndpointRouteBuilder MapRunEndpoints(this IEndpointRouteBuilder api)
    {
        var itemRuns = api.MapGroup("/orgs/{orgSlug}/items/{itemKey}/runs")
            .WithTags("Factory runs").RequireAuthorization();
        itemRuns.MapPost("/", DispatchAsync)
            .RequireOrgRole(OrgRole.Member).RequireRunItemProject(ProjectRole.Member, writable: true)
            .RequireFactoryOperator().RequireScope(Scopes.Write);
        itemRuns.MapGet("/", ItemRunsAsync)
            .RequireOrgRole(OrgRole.Guest).RequireRunItemProject(ProjectRole.Guest).RequireScope(Scopes.Read);

        // Under the organization like every other person-facing route: a browser session
        // carries no organization claim, so a bare /runs/{id} could never establish a
        // tenant and would answer 404 for every run.
        var runs = api.MapGroup("/orgs/{orgSlug}/runs")
            .WithTags("Factory runs").RequireAuthorization();
        runs.MapGet("/", ListAsync).RequireOrgRole(OrgRole.Guest).RequireScope(Scopes.Read);
        runs.MapGet("/{runId:guid}", GetAsync).RequireOrgRole(OrgRole.Guest).RequireScope(Scopes.Read);
        runs.MapGet("/{runId:guid}/log", LogAsync).RequireOrgRole(OrgRole.Guest).RequireScope(Scopes.Read);
        runs.MapPost("/{runId:guid}/cancel", CancelAsync).RequireOrgRole(OrgRole.Member).RequireScope(Scopes.Write);

        return api;
    }

    // The route filters are this door's checks (organization Member, project Member on a
    // writable project, factory operator, write scope); the dispatcher does the rest, and
    // the MCP tool shares it.
    private static async Task<IResult> DispatchAsync(
        string orgSlug, string itemKey, DispatchRunRequest request, HttpContext http,
        AutomationDbContext db, ICurrentUser user, RunDispatcher dispatcher,
        IUserDirectory directory, CancellationToken ct)
    {
        var project = http.ResolvedProject()!;
        var result = await dispatcher.DispatchAsync(
            project, itemKey, request.PlaybookId, request.AgentId, DispatchActor.User(user.UserId!), ct);
        switch (result.Outcome)
        {
            case DispatchOutcome.ItemNotFound:
            case DispatchOutcome.PlaybookNotFound:
                return NotFound();
            case DispatchOutcome.Validation:
                return Validation(result.Field!, result.Message!);
            case DispatchOutcome.ReadOnly:
                return Results.Problem(
                    title: "This organization is read-only.",
                    detail: "The evaluation has ended or a payment problem is unresolved, so new agent runs are paused. Reading and exporting still work; choosing a paid plan resumes dispatch.",
                    type: ProblemTypes.OrganizationReadOnly,
                    statusCode: StatusCodes.Status409Conflict);
            case DispatchOutcome.ItemClaimed:
                return Results.Problem(
                    title: "This item already has a live claim or run.",
                    type: ProblemTypes.ItemClaimed,
                    statusCode: StatusCodes.Status409Conflict,
                    extensions: new Dictionary<string, object?>
                    {
                        ["claimedBy"] = result.ClaimedBy,
                        ["version"] = result.Version,
                    });
            case DispatchOutcome.RunInProgress:
                return Results.Problem(
                    title: "This item already has a live run.",
                    type: ProblemTypes.ItemClaimed,
                    statusCode: StatusCodes.Status409Conflict);
        }

        var run = result.Run!;
        var people = await directory.GetAsync(RequesterIds(run), ct);
        var names = await DisplayNamesAsync(db, [run], ct);
        return Results.Created($"/api/v1/orgs/{orgSlug}/runs/{run.Id}",
            ToView(run, people, includeDetails: true, names));
    }

    private static async Task<IResult> ListAsync(
        string? project, string? agent, string? status, string? item,
        AutomationDbContext db, ICurrentTenant tenant, ICurrentUser user,
        IProjectAccess access, IUserDirectory directory,
        int page = 1, int pageSize = 25, CancellationToken ct = default)
    {
        var organizationId = tenant.OrganizationId!.Value;
        RunStatus? statusFilter = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<RunStatus>(status.Trim(), ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
            {
                return Validation("status", "Unknown run status.");
            }
            statusFilter = parsed;
        }

        var visible = (await access.ListVisibleProjectIdsAsync(user.UserId!, organizationId, ct)).ToList();
        if (!string.IsNullOrWhiteSpace(project))
        {
            var resolved = await access.FindProjectAsync(organizationId, project.Trim(), ct);
            // A list stays a list: a project the caller cannot see is an empty page, not a 404.
            if (resolved is null || await access.GetProjectRoleAsync(user.UserId!, resolved.Id, ct) is null)
            {
                return Results.Ok(new PagedResult<RunView>([], 1, 1, 0));
            }
            visible = [resolved.Id];
        }
        if (visible.Count == 0)
        {
            return Results.Ok(new PagedResult<RunView>([], 1, 1, 0));
        }

        var query = db.Runs.AsNoTracking().Where(run => visible.Contains(run.ProjectId));
        if (!string.IsNullOrWhiteSpace(agent))
        {
            query = query.Where(run => run.AgentUserId == agent.Trim());
        }
        if (statusFilter is { } state)
        {
            query = query.Where(run => run.Status == state);
        }
        if (!string.IsNullOrWhiteSpace(item))
        {
            query = query.Where(run => run.ItemKey == item.Trim());
        }

        var normalizedPage = Math.Max(1, page);
        var normalizedPageSize = Math.Clamp(pageSize, 1, 100);
        var total = await query.CountAsync(ct);
        var runs = await query.OrderByDescending(run => run.QueuedAt)
            .Skip((normalizedPage - 1) * normalizedPageSize).Take(normalizedPageSize)
            .ToListAsync(ct);
        var people = await directory.GetAsync([.. runs.Select(run => run.AgentUserId).Distinct()], ct);
        var names = await DisplayNamesAsync(db, runs, ct);
        return Results.Ok(new PagedResult<RunView>(
            runs.Select(run => ToView(run, people, includeDetails: false, names)).ToList(),
            normalizedPage, normalizedPageSize, total));
    }

    private static async Task<IResult> GetAsync(
        Guid runId, AutomationDbContext db, ICurrentTenant tenant, ICurrentUser user,
        IProjectAccess access, IUserDirectory directory, CancellationToken ct)
    {
        if (user.UserId is not { } userId)
        {
            return NotFound();
        }
        var run = await db.Runs.AsNoTracking().SingleOrDefaultAsync(run => run.Id == runId, ct);
        if (run is null || await access.GetProjectRoleAsync(userId, run.ProjectId, ct) is null)
        {
            return NotFound();
        }

        var isOperator = await access.CanOperateFactoryAsync(userId, tenant.OrganizationId!.Value, ct);
        var people = await directory.GetAsync([run.AgentUserId], ct);
        var names = await DisplayNamesAsync(db, [run], ct);
        return Results.Ok(ToView(run, people, includeDetails: isOperator, names));
    }

    private static async Task<IResult> LogAsync(
        Guid runId, HttpContext http, AutomationDbContext db, ICurrentUser user,
        IProjectAccess access, int after = -1, int pageSize = 200, CancellationToken ct = default)
    {
        if (user.UserId is not { } userId)
        {
            return NotFound();
        }
        var run = await db.Runs.AsNoTracking().SingleOrDefaultAsync(run => run.Id == runId, ct);
        if (run is null || await access.GetProjectRoleAsync(userId, run.ProjectId, ct) is null)
        {
            return NotFound();
        }
        // The item is theirs to see; operating — and with it the raw agent output — is not.
        if (await AuthorizationFilters.FactoryOperatorRefusalAsync(http) is { } refusal)
        {
            return refusal;
        }

        var chunks = await db.RunLogChunks.AsNoTracking()
            .Where(chunk => chunk.RunId == run.Id && chunk.Seq > after)
            .OrderBy(chunk => chunk.Seq)
            .Take(Math.Clamp(pageSize, 1, 1000))
            .ToListAsync(ct);
        var truncated = await db.RunLogChunks.AnyAsync(
            chunk => chunk.RunId == run.Id && chunk.Seq == RunLogChunk.TruncatedSeq, ct);
        return Results.Ok(new
        {
            items = chunks.Select(chunk => new
            {
                chunk.Seq,
                chunk.At,
                stream = chunk.Stream.ToString().ToLowerInvariant(),
                chunk.Text,
            }).ToList(),
            truncated,
        });
    }

    private static async Task<IResult> ItemRunsAsync(
        string itemKey, HttpContext http, AutomationDbContext db,
        IWorkItemLookup items, IUserDirectory directory,
        int page = 1, int pageSize = 25, CancellationToken ct = default)
    {
        var project = http.ResolvedProject()!;
        var item = (await items.FindByKeysAsync(project.Id, [itemKey], ct)).FirstOrDefault();
        if (item is null)
        {
            return NotFound();
        }

        var query = db.Runs.AsNoTracking().Where(run => run.ItemId == item.Id);
        var normalizedPage = Math.Max(1, page);
        var normalizedPageSize = Math.Clamp(pageSize, 1, 100);
        var total = await query.CountAsync(ct);
        var runs = await query.OrderByDescending(run => run.QueuedAt)
            .Skip((normalizedPage - 1) * normalizedPageSize).Take(normalizedPageSize)
            .ToListAsync(ct);
        var people = await directory.GetAsync([.. runs.Select(run => run.AgentUserId).Distinct()], ct);
        var names = await DisplayNamesAsync(db, runs, ct);
        return Results.Ok(new PagedResult<RunView>(
            runs.Select(run => ToView(run, people, includeDetails: false, names)).ToList(),
            normalizedPage, normalizedPageSize, total));
    }

    private static async Task<IResult> CancelAsync(
        Guid runId, HttpContext http, AutomationDbContext db, ICurrentUser user,
        IProjectAccess access, IRealtimePublisher realtime, TimeProvider clock, CancellationToken ct)
    {
        if (user.UserId is not { } userId)
        {
            return NotFound();
        }
        var run = await db.Runs.AsNoTracking().SingleOrDefaultAsync(run => run.Id == runId, ct);
        if (run is null || await access.GetProjectRoleAsync(userId, run.ProjectId, ct) is not { } role)
        {
            return NotFound();
        }
        if (await AuthorizationFilters.FactoryOperatorRefusalAsync(http) is { } refusal)
        {
            return refusal;
        }
        // A rule-dispatched run has no requester (RequestedBy is null) — only a project
        // Admin may cancel it, exactly as for a run dispatched by someone else.
        if (run.RequestedBy != userId && !role.Satisfies(ProjectRole.Admin))
        {
            return Results.Problem(
                title: "Insufficient permissions.",
                detail: "Only the person who dispatched a run, or a project admin, can cancel it.",
                type: ProblemTypes.InsufficientRole,
                statusCode: StatusCodes.Status403Forbidden);
        }

        var now = clock.GetUtcNow();

        // Both paths are compare-and-swap on the status the row has *now*, never on the
        // status read above: a runner may claim a queued run, or finish a running one,
        // between that read and this write.
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            var cancelledQueued = await db.Runs
                .Where(r => r.Id == run.Id && r.Status == RunStatus.Queued)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(r => r.Status, RunStatus.Cancelled)
                    .SetProperty(r => r.FinishedAt, now)
                    .SetProperty(r => r.CancelRequestedAt, now), ct);
            if (cancelledQueued == 1)
            {
                // No runner has it, so cancelling is terminal here. RunFinished releases the
                // claim and moves the item by the playbook's failure state, as any finish does.
                await RunCompletion.StageAsync(db, run, RunOutcomes.Cancelled,
                    summary: null, pullRequestUrl: null,
                    failureReason: "cancelled before a runner took it", ct);
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);

                await realtime.PublishAsync(run.ProjectId, "run.changed", new
                {
                    runId = run.Id, itemId = run.ItemId, itemKey = run.ItemKey, status = "cancelled",
                }, ct);
                AutomationMetrics.Finished.Add(1, AutomationMetrics.OutcomeTag(RunOutcomes.Cancelled));
                return Results.NoContent();
            }
        }

        // The runner acknowledges on finish (its heartbeat reads the flag); the sweeper
        // catches a runner that never will. Asking twice is the same request.
        var requested = await db.Runs
            .Where(r => r.Id == run.Id && (r.Status == RunStatus.Assigned || r.Status == RunStatus.Running))
            .ExecuteUpdateAsync(set => set
                .SetProperty(r => r.CancelRequestedAt, r => r.CancelRequestedAt ?? now), ct);
        if (requested == 1)
        {
            await realtime.PublishAsync(run.ProjectId, "run.changed", new
            {
                runId = run.Id, itemId = run.ItemId, itemKey = run.ItemKey, cancelRequested = true,
            }, ct);
            return Results.NoContent();
        }

        return Results.Problem(
            title: "This run is already finished.",
            type: ProblemTypes.Conflict,
            statusCode: StatusCodes.Status409Conflict);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────

    internal static Task<long> StoredLogBytesAsync(AutomationDbContext db, Guid runId, CancellationToken ct) =>
        db.Database.SqlQuery<long>($"""
            SELECT COALESCE(SUM(octet_length(text)), 0) AS "Value"
            FROM automation.run_log_chunks WHERE run_id = {runId}
            """).SingleAsync(ct);

    /// <summary>
    /// The playbook, runner and rule display names for a page of runs, in batched reads. A
    /// run screen names the machine, the recipe and — for a rule-dispatched run — the rule;
    /// a join across modules is not available here, so the lookup is local to this module's
    /// own tables. A run whose rule was since deleted simply gets no name back.
    /// </summary>
    private static async Task<RunDisplayNames> DisplayNamesAsync(
        AutomationDbContext db, IReadOnlyCollection<Run> runs, CancellationToken ct)
    {
        var playbookIds = runs.Where(run => run.PlaybookId != Guid.Empty).Select(run => run.PlaybookId).Distinct().ToList();
        var runnerIds = runs.Select(run => run.RunnerId).Where(runner => runner is not null).Select(runner => runner!.Value).Distinct().ToList();
        var ruleIds = runs.Select(run => run.RuleId).Where(rule => rule is not null).Select(rule => rule!.Value).Distinct().ToList();

        var playbooks = playbookIds.Count == 0
            ? []
            : await db.Playbooks.AsNoTracking().Where(playbook => playbookIds.Contains(playbook.Id))
                .Select(playbook => new { playbook.Id, playbook.Name }).ToDictionaryAsync(row => row.Id, row => row.Name, ct);
        var runners = runnerIds.Count == 0
            ? []
            : await db.Runners.AsNoTracking().Where(runner => runnerIds.Contains(runner.Id))
                .Select(runner => new { runner.Id, runner.Name }).ToDictionaryAsync(row => row.Id, row => row.Name, ct);
        var rules = ruleIds.Count == 0
            ? []
            : await db.Rules.AsNoTracking().Where(rule => ruleIds.Contains(rule.Id))
                .Select(rule => new { rule.Id, rule.Name }).ToDictionaryAsync(row => row.Id, row => row.Name, ct);
        return new RunDisplayNames(playbooks, runners, rules);
    }

    internal sealed record RunDisplayNames(
        Dictionary<Guid, string> Playbooks,
        Dictionary<Guid, string> Runners,
        Dictionary<Guid, string> Rules);

    /// <summary>The directory ids to prefetch for a run: the agent, and whoever dispatched it, if a person did.</summary>
    private static IReadOnlyCollection<string> RequesterIds(Run run) =>
        run.RequestedBy is { } requestedBy ? [run.AgentUserId, requestedBy] : [run.AgentUserId];

    internal static RunView ToView(
        Run run, IReadOnlyDictionary<string, UserSummary> people, bool includeDetails,
        RunDisplayNames? names = null) =>
        new(run.Id, run.ProjectId, run.ItemId, run.ItemKey, run.PlaybookId,
            names?.Playbooks.GetValueOrDefault(run.PlaybookId),
            run.AgentUserId,
            people.TryGetValue(run.AgentUserId, out var agent) ? agent.DisplayName : null,
            run.RequestedBy, run.RuleId,
            run.RuleId is { } rule ? names?.Rules.GetValueOrDefault(rule) : null,
            run.RunnerId,
            run.RunnerId is { } runner ? names?.Runners.GetValueOrDefault(runner) : null,
            run.Status, run.Harness,
            run.PlaybookRevisionId, run.MaxMinutes, run.QueuedAt, run.AssignedAt,
            run.StartedAt, run.FinishedAt, run.LastHeartbeatAt,
            run.CancelRequestedAt is not null,
            run.OutcomeSummary, run.PullRequestUrl, run.ExitCode, run.CostUsd,
            run.InputTokens, run.OutputTokens,
            includeDetails ? run.FailureReason : null,
            includeDetails ? run.PromptSnapshot : null,
            run.Version);

    /// <summary>The project key of an item key is everything before the last dash: <c>PROJ-12</c> names project <c>PROJ</c>.</summary>
    internal static string? ProjectKeyOf(string itemKey)
    {
        var cut = itemKey.LastIndexOf('-');
        return cut > 0 && cut < itemKey.Length - 1 ? itemKey[..cut] : null;
    }

    /// <summary>
    /// The project-role check for routes that name an <c>{itemKey}</c> rather than a
    /// <c>{projectKey}</c>: the project key is parsed out of the item key and resolved
    /// exactly as <see cref="AuthorizationFilters.RequireProjectRole"/> resolves one,
    /// leaving the project where <see cref="AuthorizationFilters.ResolvedProject"/> and
    /// <see cref="AuthorizationFilters.ProjectWriteRefusalAsync"/> find it.
    /// </summary>
    private static TBuilder RequireRunItemProject<TBuilder>(this TBuilder builder, ProjectRole required, bool writable = false)
        where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilterFactory((_, next) => async context =>
        {
            var http = context.HttpContext;
            var user = http.RequestServices.GetRequiredService<ICurrentUser>();
            var tenant = http.RequestServices.GetRequiredService<ICurrentTenant>();

            if (user.UserId is not { } userId || tenant.OrganizationId is not { } organizationId)
            {
                return NotFound();
            }

            if (http.GetRouteValue("itemKey") is not string itemKey || itemKey.Length == 0)
            {
                throw new InvalidOperationException(
                    "RequireRunItemProject is on an endpoint with no {itemKey} route parameter.");
            }
            if (ProjectKeyOf(itemKey) is not { } projectKey)
            {
                return NotFound();
            }

            var access = http.RequestServices.GetRequiredService<IProjectAccess>();
            var project = await access.FindProjectAsync(organizationId, projectKey, http.RequestAborted);
            if (project is null)
            {
                return NotFound();
            }

            var role = await access.GetProjectRoleAsync(userId, project.Id, http.RequestAborted);
            if (role is null)
            {
                return NotFound();
            }

            if (!role.Value.Satisfies(required))
            {
                return Results.Problem(
                    title: "Insufficient permissions.",
                    detail: $"This action requires the {required} project role or higher.",
                    type: ProblemTypes.InsufficientRole,
                    statusCode: StatusCodes.Status403Forbidden);
            }

            http.Items[AuthorizationFilters.ProjectItemKey] = project;
            if (writable
                && await AuthorizationFilters.ProjectWriteRefusalAsync(http, project.Key, project.IsArchived) is { } refusal)
            {
                return refusal;
            }

            return await next(context);
        });

    private static IResult Validation(string field, string error) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [error] }, type: ProblemTypes.Validation);

    internal static IResult NotFound() =>
        Results.Problem(
            title: "Not found.",
            detail: "The resource does not exist, or you do not have access to it.",
            type: ProblemTypes.NotAMember,
            statusCode: StatusCodes.Status404NotFound);
}

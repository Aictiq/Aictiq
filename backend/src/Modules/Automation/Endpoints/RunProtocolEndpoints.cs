using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aictiq.Modules.Automation.Auth;
using Aictiq.Modules.Automation.Domain;
using Aictiq.Modules.Automation.Prompts;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Email;
using Aictiq.SharedKernel.Tenancy;
using Aictiq.SharedKernel.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Aictiq.Modules.Automation.Endpoints;

public sealed record RunnerClaimRequest(IReadOnlyList<string>? Harnesses, int Slots = 1);

/// <param name="Reason">
/// Why the run goes back. <c>no-local-repository</c>: this runner found no clone of the project, so
/// it is not offered the run again until its mappings or roots change. Absent: it only cannot take
/// the run now (another organization holds the machine).
/// </param>
/// <param name="Repository">With <c>no-local-repository</c> on a multi-repository run: the alias of the checkout it could not find.</param>
public sealed record RunnerReleaseRequest(string? Reason, string? Repository = null);

/// <param name="AgentToken">The per-run agent token's secret. Exists once, like every token secret;
/// it is revoked when the run finishes and dies with <paramref name="MaxMinutes"/> even if the runner never reports.</param>
/// <param name="ItemId">The item the run works; null for a project-chat run, which has none.</param>
public sealed record RunnerRunClaimed(Guid RunId, Guid? ItemId, string? ItemKey, Guid ProjectId, string ProjectKey,
    string OrganizationSlug, string Harness, string Prompt, Guid? PlaybookRevisionId, RunRepoView Repo,
    string DefaultBranch, string BranchName, int MaxMinutes, string? AictiqUrl, string AgentToken,
    string? AgentTokenDisplay, int HeartbeatIntervalSeconds, bool WorkOnDefaultBranch = false,
    RunResumeView? Resume = null, RunFollowUpView? FollowUp = null, string? SystemPrompt = null,
    string? WorkingDirectory = null, IReadOnlyList<RunRepoEntry>? Repos = null);

/// <summary>
/// One checkout of a multi-repository run, into <c>repo/&lt;alias&gt;/</c>. The claim's own
/// <see cref="RunnerRunClaimed.Repo"/> still describes the primary repository for older runners.
/// </summary>
/// <param name="Source"><c>github</c> or <c>local</c>, as <see cref="RunRepoView.Source"/>.</param>
/// <param name="WorkingDirectory">Where the agent starts inside this checkout when it is the run's only one.</param>
public sealed record RunRepoEntry(string Alias, string Source, string? RepoFullName, string? CloneToken,
    string? LocalPathHint, string DefaultBranch, string? WorkingDirectory, bool Primary);

/// <summary>
/// Set on a continue run: the runner resumes <paramref name="SessionId"/> in the workspace it
/// kept for <paramref name="ContinuesRunId"/>, telling the agent why that run stopped.
/// </summary>
public sealed record RunResumeView(Guid ContinuesRunId, string SessionId, string? FailureReason);

/// <summary>
/// Set on a follow-up run: a comment asked the agent to carry on with its earlier run's work.
/// The runner resumes <paramref name="SessionId"/> in the workspace it kept for
/// <paramref name="PreviousRunId"/> when it still has it, and starts fresh on the same branch
/// when not. Before the harness starts it checks <paramref name="PullRequestUrl"/>: open, the
/// run pushes to <paramref name="PreviousBranchName"/>; merged or closed, it starts
/// <paramref name="NewBranchName"/> from the default branch; gone, it stops with
/// <c>follow-up-target-missing</c>.
/// </summary>
/// <param name="SessionId">The earlier run's harness session, or null when it reported none or used another harness.</param>
/// <param name="Instruction">What the person wrote, for the message a resumed session is sent.</param>
/// <param name="RequestedByName">The person's display name.</param>
/// <param name="CommentId">The comment that asked, or null for a chain's stage or rework run.</param>
/// <param name="Kind">
/// <c>comment</c> for a person's request; <c>stage</c> for a follow-up playbook, which checks the
/// pull request with a fresh session and has nothing to do once it was merged or closed;
/// <c>rework</c> for the builder rerun a stage's findings asked for, quoted as the instruction.
/// </param>
public sealed record RunFollowUpView(
    Guid PreviousRunId, string? SessionId, string PreviousBranchName, string? PullRequestUrl,
    string NewBranchName, string Instruction, string? RequestedByName, Guid? CommentId, string Kind = "comment");
/// <param name="SessionId">The harness session, sent once the runner knows it, so a run the sweeper ends can still be continued.</param>
/// <param name="WorkspacePath">The absolute path of the run's checkout on the runner, sent with the session.</param>
public sealed record RunnerRunHeartbeatRequest(string? SessionId, string? WorkspacePath = null);

/// <param name="Source"><c>github</c> when the project has a binding, <c>local</c> when the runner is expected to find the working copy itself.</param>
public sealed record RunRepoView(string Source, string? RepoFullName, string? CloneToken, string? LocalPathHint);

public sealed record RunnerLogRequest(IReadOnlyList<RunnerLogChunk>? Chunks);

/// <param name="At">When the line was produced. Null means it arrives at the server's clock.</param>
public sealed record RunnerLogChunk(int Seq, RunLogStream Stream, string? Text, DateTimeOffset? At);

/// <param name="PullRequestUrls">Every pull request a multi-repository run opened or updated; <paramref name="PullRequestUrl"/> is the first.</param>
public sealed record RunnerFinishRequest(string? Outcome, int? ExitCode, string? Summary, string? PullRequestUrl,
    decimal? CostUsd, long? InputTokens, long? OutputTokens, string? FailureReason, string? SessionId = null,
    string? WorkspacePath = null, string? BranchName = null, IReadOnlyList<string>? PullRequestUrls = null);

/// <param name="Alias">The checkout of a multi-repository run to refresh; null for the primary repository.</param>
public sealed record RunnerRepoTokenRequest(string? Alias);

/// <summary>
/// The half of the runner protocol that concerns runs: claim, release, started, log,
/// heartbeat, finish, repo-token. Same group prefix and policy as the hello/heartbeat pair - nothing
/// but a runner principal answers here, and a runner principal answers nowhere else.
/// </summary>
/// <remarks>
/// Claiming is the one place the factory hands out a credential. The run row is taken
/// with <c>FOR UPDATE SKIP LOCKED</c> (raw SQL on the context's own connection - EF would
/// bury the locking clause in a subquery, where Postgres ignores it), so two idle runners
/// polling at once are handed two different runs; only then is the agent token minted, so
/// a run that was never claimed never costs a token.
/// </remarks>
public static partial class RunProtocolEndpoints
{
    public static IEndpointRouteBuilder MapRunProtocolEndpoints(this IEndpointRouteBuilder api)
    {
        var protocol = api.MapGroup("/runner")
            .WithTags("Runner protocol")
            .RequireAuthorization(RunnerDefaults.Policy);

        protocol.MapPost("/runs/claim", ClaimAsync);
        protocol.MapPost("/runs/{runId:guid}/release", ReleaseAsync);
        protocol.MapPost("/runs/{runId:guid}/started", StartedAsync);
        protocol.MapPost("/runs/{runId:guid}/log", LogAsync);
        protocol.MapPost("/runs/{runId:guid}/heartbeat", HeartbeatAsync);
        protocol.MapPost("/runs/{runId:guid}/finish", FinishAsync);
        protocol.MapPost("/runs/{runId:guid}/repo-token", RepoTokenAsync);

        return api;
    }

    private static async Task<IResult> ClaimAsync(
        RunnerClaimRequest? request, HttpContext http, AutomationDbContext db, ICurrentTenant tenant,
        IAgentIdentities agents, IOrganizationLookup organizations, IRepositoryCredentials repoCredentials,
        IRealtimePublisher realtime, IOrganizationBillingState billing, IOptions<AutomationOptions> options,
        IOptions<EmailOptions> email, IUserDirectory directory, FleetMode fleetMode, IProjectAccess access,
        TimeProvider clock, ILoggerFactory loggers, CancellationToken ct)
    {
        var logger = loggers.CreateLogger(typeof(RunProtocolEndpoints));
        if (ClaimErrors(request) is { Count: > 0 } errors)
        {
            return Results.ValidationProblem(errors, type: ProblemTypes.Validation);
        }

        var runnerId = RunnerId(http);
        var organizationId = tenant.OrganizationId!.Value;

        // A read-only organization hands out no new work. The sweeper cancels
        // what is already queued, but it runs on a timer and this route is the door a
        // runner actually comes through: without the check here, a run dispatched a moment
        // before the evaluation expired would still be picked up and worked on. Runs
        // already assigned or running are untouched - they finish through the other routes,
        // deadlines, outcome writes and credential cleanup included.
        if (await billing.IsReadOnlyAsync(organizationId, ct))
        {
            return Results.NoContent();
        }

        var harnesses = request!.Harnesses!.Distinct(StringComparer.Ordinal).ToArray();
        // Asked once per poll, not per second: a plan change takes effect on the runner's next poll.
        var fleet = await fleetMode.GetAsync(organizationId, ct);
        var deadline = clock.GetUtcNow() + TimeSpan.FromSeconds(options.Value.PollTimeoutSeconds);
        // What this runner last reported it maps, read once per poll like fleet mode: a new
        // mapping counts from the heartbeat after `aictiq runner map`, and the poll after that.
        var reported = await db.Runners.AsNoTracking()
            .Where(r => r.Id == runnerId)
            .Select(r => r.Capabilities)
            .SingleOrDefaultAsync(ct);
        var servable = new Dictionary<Guid, bool>();
        var projectKeys = new Dictionary<Guid, string>();
        // An older CLI checks out one repository: it never gets a run with several, or a sub-package.
        var multiRepo = reported?.MultiRepo == true;

        while (true)
        {
            var claimed = await TryClaimAsync();
            if (claimed is not null)
            {
                return Results.Ok(claimed);
            }

            if (clock.GetUtcNow() >= deadline)
            {
                return Results.NoContent();
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), clock, http.RequestAborted);
            }
            catch (OperationCanceledException)
            {
                return Results.NoContent();
            }
        }

        async Task<RunnerRunClaimed?> TryClaimAsync()
        {
            // A run whose agent was disabled after dispatch is failed here rather than
            // handed out: minting it a token would only produce a run that cannot
            // authenticate, and leaving it queued would block the head of the queue.
            while (true)
            {
                var outcome = await TryClaimOnceAsync();
                if (outcome is { Skipped: false })
                {
                    return outcome.Claimed;
                }
            }
        }

        async Task<(RunnerRunClaimed? Claimed, bool Skipped)> TryClaimOnceAsync()
        {
            await db.Database.OpenConnectionAsync(ct);
            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            // A run sent to one runner is that runner's alone, unless the runner has since
            // been disabled or deleted: then any runner may take it rather than leave it
            // queued for a machine that cannot come back. A continue run never moves: no
            // other machine has its session, and the sweeper fails it if its runner is gone.
            //
            // The lock lives inside this transaction, so the row stays ours until the
            // assignment is saved; SKIP LOCKED hands a second poller the next run instead
            // of waiting on this one. A scheduled run is nobody's until its time comes, and is
            // then ordered by that time rather than by when it was asked for. Raw SQL because
            // EF would bury the locking clause in a subquery, where Postgres ignores it.
            var projects = await ServableProjectsAsync();
            Guid? claimedRunId;
            if (fleet.Active)
            {
                claimedRunId = await FleetCandidateAsync(transaction, projects);
            }
            else
            {
                // Fleet mode off - never turned on, or the plan no longer allows it: required
                // tags are ignored, so a run dispatched with them before a downgrade still goes.
                await using var command = ((NpgsqlConnection)db.Database.GetDbConnection()).CreateCommand();
                command.Transaction = (NpgsqlTransaction)transaction.GetDbTransaction();
                command.CommandText = $"""
                    SELECT id FROM automation.runs
                    WHERE {ClaimablePredicate}
                    ORDER BY COALESCE(scheduled_for, queued_at)
                    FOR UPDATE SKIP LOCKED
                    LIMIT 1
                    """;
                command.Parameters.AddWithValue("organizationId", organizationId);
                command.Parameters.AddWithValue("harnesses", harnesses);
                command.Parameters.AddWithValue("runnerId", runnerId);
                command.Parameters.AddWithValue("now", clock.GetUtcNow());
                AddProjectParameters(command, projects, multiRepo, await UnservedRunsAsync());
                claimedRunId = await command.ExecuteScalarAsync(ct) as Guid?;
            }

            if (claimedRunId is not { } runId)
            {
                await transaction.CommitAsync(ct);
                return (null, false);
            }

            var run = await db.Runs.SingleAsync(r => r.Id == runId, ct);
            var now = clock.GetUtcNow();
            if (await agents.FindAsync(run.AgentUserId, ct) is not { IsActive: true })
            {
                run.FailureReason = "agent-disabled";
                run.Finish(RunStatus.Failed, RunOutcomes.Failed, now);
                await RunCompletion.StageAsync(db, run, RunOutcomes.Failed,
                    summary: null, pullRequestUrl: null, failureReason: run.FailureReason, ct);
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                AutomationMetrics.Finished.Add(1, AutomationMetrics.OutcomeTag(RunOutcomes.Failed));
                await realtime.PublishAsync(run.ProjectId, "run.changed", new
                {
                    runId = run.Id, itemId = run.ItemId, itemKey = run.ItemKey, status = RunOutcomes.Failed,
                }, ct);
                db.ChangeTracker.Clear();
                return (null, true);
            }

            run.Status = RunStatus.Assigned;
            run.RunnerId = runnerId;
            run.AssignedAt = now;
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            // The row is ours; minting happens after the commit so a crash between the
            // two leaves an assigned-but-unmanned run the sweeper can recover, not a
            // token nobody knows to revoke.
            AgentTokenIssued? issued = null;
            try
            {
                // A project chat may read and create items, nothing else: no write scope, and
                // create_item checks the project-chat grant this token carries instead.
                issued = await agents.IssueTokenAsync(run.AgentUserId, organizationId, $"run:{run.Id}",
                    run.Kind == RunKind.ProjectChat ? [Scopes.Read, Scopes.Mcp, Scopes.ProjectChat] : [Scopes.Read, Scopes.Write, Scopes.Mcp],
                    now.AddMinutes(run.MaxMinutes + 10), ct);
                run.AgentTokenId = issued.Token.Id;
                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not mint the per-run token for run {RunId}; unassigning it", run.Id);
                if (issued is not null)
                {
                    await agents.RevokeTokenAsync(run.AgentUserId, issued.Token.Id, CancellationToken.None);
                }

                await db.Database.ExecuteSqlAsync($"""
                    UPDATE automation.runs
                    SET status = 0, runner_id = NULL, assigned_at = NULL, agent_token_id = NULL
                    WHERE id = {run.Id} AND status = {(short)RunStatus.Assigned} AND runner_id = {runnerId}
                    """, CancellationToken.None);
                throw;
            }

            var organization = await organizations.FindByIdAsync(organizationId, ct);
            var settings = await db.ProjectSettings.AsNoTracking()
                .SingleOrDefaultAsync(row => row.ProjectId == run.ProjectId, ct);
            RunRepoView repo = settings?.RepoSource == ProjectRepositorySource.GitHubBinding
                && settings.RepoFullName is { } repoFullName
                    ? new("github", repoFullName,
                        await repoCredentials.GetCloneTokenAsync(run.ProjectId, repoFullName, ct), null)
                    : new("local", null, null, settings?.LocalPathHint);
            List<RunRepoEntry>? repos = null;
            if (run.Repositories.Count > 0)
            {
                repos = [];
                foreach (var checkout in run.Repositories)
                {
                    var github = !RepositoryPlans.NeedsLocal(checkout);
                    repos.Add(new RunRepoEntry(checkout.Alias, github ? "github" : "local", checkout.RepoFullName,
                        github ? await repoCredentials.GetCloneTokenAsync(run.ProjectId, checkout.RepoFullName!, ct) : null,
                        checkout.LocalPathHint, checkout.DefaultBranch, checkout.WorkingDirectory, checkout.Primary));
                }
            }

            await realtime.PublishAsync(run.ProjectId, "run.changed", new
            {
                runId = run.Id, itemId = run.ItemId, itemKey = run.ItemKey,
                status = "assigned", runnerId,
            }, ct);

            // The public URL is Email:BaseUrl when an instance has one; otherwise the address
            // the runner reached us on is the best statement of where Aictiq lives.
            RunResumeView? resume = null;
            if (run.ContinuesRunId is { } continuesRunId)
            {
                var previous = await db.Runs.AsNoTracking()
                    .Where(r => r.Id == continuesRunId)
                    .Select(r => new { r.SessionId, r.FailureReason })
                    .SingleOrDefaultAsync(ct);
                if (previous?.SessionId is { } sessionId)
                {
                    resume = new RunResumeView(continuesRunId, sessionId, previous.FailureReason);
                }
            }

            // A continue of a follow-up resumes its own failed session above; only a follow-up's
            // first run needs the earlier run's.
            RunFollowUpView? followUp = null;
            if (resume is null && run.FollowsUpRunId is { } previousRunId)
            {
                var previous = await db.Runs.AsNoTracking()
                    .Where(r => r.Id == previousRunId)
                    .Select(r => new { r.SessionId, r.Harness, r.RunnerId, r.PullRequestUrl, r.FailureReason })
                    .SingleOrDefaultAsync(ct);
                var stage = run.Kind == RunKind.Stage;
                // A rework is asked for by the stage that found something, not by a person.
                var requester = run.ReworkOfRunId is { } stageRunId
                    ? await db.Runs.AsNoTracking().Where(r => r.Id == stageRunId)
                        .Join(db.Playbooks.AsNoTracking(), r => r.PlaybookId, p => p.Id, (r, p) => p.Name)
                        .FirstOrDefaultAsync(ct)
                    : run.RequestedBy is { } requestedBy
                        ? (await directory.GetAsync([requestedBy], ct)).GetValueOrDefault(requestedBy)?.DisplayName
                        : null;
                // Another harness cannot read the session, and another machine does not have it.
                // A stage never resumes the builder's: it checks the work with fresh eyes.
                var sessionId = !stage && previous is { SessionId: { } session } && previous.Harness == run.Harness
                    && previous.RunnerId == runnerId && previous.FailureReason != Run.SessionUnavailable
                        ? session
                        : null;
                // A follow-up of a follow-up that reported no pull request still updates the first one's.
                var pullRequestUrl = run.WorkOnDefaultBranch ? null : previous?.PullRequestUrl
                    ?? await db.Runs.AsNoTracking()
                        .Where(r => r.ItemId == run.ItemId && r.Kind == RunKind.Implement && r.BranchName == run.BranchName && r.PullRequestUrl != null)
                        .OrderByDescending(r => r.QueuedAt).Select(r => r.PullRequestUrl).FirstOrDefaultAsync(ct);
                followUp = new RunFollowUpView(
                    previousRunId, sessionId, run.BranchName, pullRequestUrl,
                    BranchNames.Next(run.BranchName), run.Instruction ?? "", requester, run.TriggerCommentId,
                    stage ? "stage" : run.ReworkOfRunId is not null ? "rework" : "comment");
            }

            var aictiqUrl = string.IsNullOrWhiteSpace(email.Value.BaseUrl)
                ? $"{http.Request.Scheme}://{http.Request.Host}"
                : email.Value.BaseUrl.TrimEnd('/');

            return (new RunnerRunClaimed(
                run.Id, run.ItemId, run.ItemKey, run.ProjectId,
                run.ItemKey is { } itemKey
                    ? RunEndpoints.ProjectKeyOf(itemKey) ?? itemKey
                    : (await access.GetProjectAsync(run.ProjectId, ct))?.Key ?? "",
                organization?.Slug ?? "",
                run.Harness, SecurityPreamble.Include(run.PromptSnapshot), run.PlaybookRevisionId, repo,
                run.WorkOnDefaultBranch ? run.BranchName : settings?.DefaultBranch ?? "main", run.BranchName, run.MaxMinutes,
                aictiqUrl, issued.Secret, issued.Token.Display,
                options.Value.HeartbeatIntervalSeconds, run.WorkOnDefaultBranch, resume, followUp, SecurityPreamble.Text,
                run.WorkingDirectory, repos), false);
        }

        // The projects this runner may claim runs of, among those with runs queued: every project
        // cloned from GitHub, and each runner-local one it maps or whose path hint is under one of
        // its roots. Null while it reports no mappings at all (an older CLI): every project, as before.
        async Task<Guid[]?> ServableProjectsAsync()
        {
            if (!RunnerProjects.ReportsProjects(reported))
            {
                return null;
            }

            var queued = await db.Runs.AsNoTracking()
                .Where(r => r.Status == RunStatus.Queued)
                .Select(r => r.ProjectId)
                .Distinct()
                .ToListAsync(ct);
            var unknown = queued.Where(id => !servable.ContainsKey(id)).ToArray();
            if (unknown.Length > 0)
            {
                foreach (var (projectId, serving) in await ProjectServingAsync(unknown))
                {
                    servable[projectId] = serving.Serves(reported);
                }
            }
            return queued.Where(id => servable[id]).ToArray();
        }

        Task<Dictionary<Guid, ProjectServing>> ProjectServingAsync(Guid[] projectIds) =>
            ProjectServing.LoadAsync(db, access, projectIds, ct);

        // Queued multi-repository runs with a checkout this runner plainly cannot find on its machine.
        async Task<Guid[]> UnservedRunsAsync()
        {
            if (!multiRepo)
            {
                return [];
            }
            var queued = await db.Runs.AsNoTracking()
                .Where(r => r.Status == RunStatus.Queued && r.RepositoryAliases.Length > 0)
                .Select(r => new { r.Id, r.ProjectId, r.Repositories })
                .Take(500)
                .ToListAsync(ct);
            var unserved = new List<Guid>();
            foreach (var row in queued)
            {
                if (!projectKeys.TryGetValue(row.ProjectId, out var key))
                {
                    key = projectKeys[row.ProjectId] = (await access.GetProjectAsync(row.ProjectId, ct))?.Key ?? "";
                }
                if (RepositoryRouting.MissingRepository(reported, key, row.Repositories) is not null)
                {
                    unserved.Add(row.Id);
                }
            }
            return [.. unserved];
        }

        // Fleet mode: the run must want only tags this runner has, the runner must have a free
        // slot of its maxParallel, and among the online runners that could take it the one with
        // the most free slots should. A busier runner leaves a run to a freer one for a few
        // seconds (FleetBalanceGraceSeconds), long enough for that runner's next poll; past
        // that it takes it anyway, so a runner that is online but not polling strands nothing.
        // A run sent to this runner is its own, whatever it requires.
        async Task<Guid?> FleetCandidateAsync(IDbContextTransaction transaction, Guid[]? projects)
        {
            var now = clock.GetUtcNow();
            var runners = await db.Runners.AsNoTracking()
                .Where(r => r.DeletedAt == null && r.DisabledAt == null)
                .ToListAsync(ct);
            if (runners.SingleOrDefault(r => r.Id == runnerId) is not { } me)
            {
                return null;
            }

            var held = await db.Runs.AsNoTracking()
                .Where(r => (r.Status == RunStatus.Assigned || r.Status == RunStatus.Running) && r.RunnerId != null)
                .GroupBy(r => r.RunnerId!.Value)
                .Select(group => new { RunnerId = group.Key, Count = group.Count() })
                .ToDictionaryAsync(row => row.RunnerId, row => row.Count, ct);
            int FreeSlots(Runner runner) => Math.Max(1, runner.Capabilities?.MaxParallel ?? 1) - held.GetValueOrDefault(runner.Id);

            var mine = FreeSlots(me);
            if (mine <= 0)
            {
                return null;
            }

            var myTags = FleetTags.Effective(me);
            var candidates = new List<(Guid Id, string[] Required, Guid? Requested, string Harness, DateTimeOffset Turn)>();
            await using (var command = ((NpgsqlConnection)db.Database.GetDbConnection()).CreateCommand())
            {
                command.Transaction = (NpgsqlTransaction)transaction.GetDbTransaction();
                command.CommandText = $"""
                    SELECT id, required_tags, requested_runner_id, harness, COALESCE(scheduled_for, queued_at)
                    FROM automation.runs
                    WHERE {ClaimablePredicate}
                      AND (requested_runner_id = @runnerId OR required_tags <@ @tags)
                    ORDER BY COALESCE(scheduled_for, queued_at)
                    LIMIT 25
                    """;
                command.Parameters.AddWithValue("organizationId", organizationId);
                command.Parameters.AddWithValue("harnesses", harnesses);
                command.Parameters.AddWithValue("runnerId", runnerId);
                command.Parameters.AddWithValue("now", now);
                command.Parameters.AddWithValue("tags", myTags);
                AddProjectParameters(command, projects, multiRepo, await UnservedRunsAsync());
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    candidates.Add((reader.GetGuid(0), reader.GetFieldValue<string[]>(1),
                        reader.IsDBNull(2) ? null : reader.GetGuid(2), reader.GetString(3),
                        reader.GetFieldValue<DateTimeOffset>(4)));
                }
            }

            var others = runners
                .Where(r => r.Id != runnerId && RunnerEndpoints.IsOnline(r, options.Value, now) && FreeSlots(r) > mine)
                .Select(r => (Runner: r, Tags: FleetTags.Effective(r)))
                .ToList();
            // A freer runner only waits its turn on a run it could serve.
            var candidateIds = candidates.Select(c => c.Id).ToArray();
            var projectOf = await db.Runs.AsNoTracking()
                .Where(r => candidateIds.Contains(r.Id))
                .Select(r => new { r.Id, r.ProjectId, r.DeclinedRunnerIds, r.Repositories, r.WorkingDirectory })
                .ToDictionaryAsync(r => r.Id, ct);
            var serving = await ProjectServingAsync(projectOf.Values.Select(r => r.ProjectId).Distinct().ToArray());
            foreach (var projectId in projectOf.Values.Select(r => r.ProjectId).Distinct())
            {
                if (!projectKeys.ContainsKey(projectId))
                {
                    projectKeys[projectId] = (await access.GetProjectAsync(projectId, ct))?.Key ?? "";
                }
            }
            bool OtherServes(Runner other, Guid runId)
            {
                var row = projectOf[runId];
                return row.Repositories.Count == 0 && row.WorkingDirectory is null
                    ? serving[row.ProjectId].Serves(other.Capabilities)
                    : other.Capabilities?.MultiRepo == true
                        && RepositoryRouting.MissingRepository(other.Capabilities, projectKeys[row.ProjectId], row.Repositories) is null;
            }
            var grace = TimeSpan.FromSeconds(options.Value.FleetBalanceGraceSeconds);
            var eligible = candidates
                .Where(run => run.Requested == runnerId
                    || now - run.Turn >= grace
                    || !others.Any(other => FleetTags.Satisfies(other.Tags, run.Required)
                        && (other.Runner.Capabilities?.Harnesses.Any(h => h.Name == run.Harness) ?? false)
                        && !projectOf[run.Id].DeclinedRunnerIds.Contains(other.Runner.Id)
                        && OtherServes(other.Runner, run.Id)))
                .Select(run => run.Id)
                .ToArray();
            if (eligible.Length == 0)
            {
                return null;
            }

            await using var lockCommand = ((NpgsqlConnection)db.Database.GetDbConnection()).CreateCommand();
            lockCommand.Transaction = (NpgsqlTransaction)transaction.GetDbTransaction();
            lockCommand.CommandText = """
                SELECT id FROM automation.runs
                WHERE id = ANY(@ids) AND status = 0
                ORDER BY COALESCE(scheduled_for, queued_at)
                FOR UPDATE SKIP LOCKED
                LIMIT 1
                """;
            lockCommand.Parameters.AddWithValue("ids", eligible);
            return await lockCommand.ExecuteScalarAsync(ct) as Guid?;
        }
    }

    /// <summary>
    /// The runs a runner may claim, before fleet mode has its say. A run sent to one runner is
    /// that runner's alone, unless the runner has since been disabled or deleted: then any runner
    /// may take it rather than leave it queued for a machine that cannot come back. A continue
    /// run never moves: no other machine has its session, and the sweeper fails it if its runner
    /// is gone. A scheduled run is nobody's until its time comes.
    /// </summary>
    private const string ClaimablePredicate = """
        organization_id = @organizationId AND status = 0 AND harness = ANY(@harnesses)
          AND (scheduled_for IS NULL OR scheduled_for <= @now)
          AND (requested_runner_id IS NULL OR requested_runner_id = @runnerId
               OR NOT EXISTS (SELECT 1 FROM automation.runners r
                              WHERE r.id = requested_runner_id AND r.disabled_at IS NULL))
          AND (continues_run_id IS NULL OR requested_runner_id = @runnerId)
          AND NOT (@runnerId = ANY(declined_runner_ids))
          AND (continues_run_id IS NOT NULL OR @anyProject OR project_id = ANY(@projects) OR cardinality(repository_aliases) > 0)
          AND ((cardinality(repository_aliases) = 0 AND working_directory IS NULL) OR @multiRepo)
          AND NOT (id = ANY(@unservedRuns))
        """;

    /// <summary>
    /// Binds <see cref="ClaimablePredicate"/>'s project filter. A continue run is exempt: it goes
    /// back to the machine that holds its workspace whatever that machine now maps.
    /// </summary>
    /// <remarks>
    /// A run with several checkouts is judged by its own repositories rather than its project's
    /// primary one, and only a runner that reports <see cref="RunnerCapabilities.MultiRepo"/> takes it.
    /// </remarks>
    private static void AddProjectParameters(NpgsqlCommand command, Guid[]? projects, bool multiRepo, Guid[] unservedRuns)
    {
        command.Parameters.AddWithValue("anyProject", projects is null);
        command.Parameters.AddWithValue("projects", projects ?? []);
        command.Parameters.AddWithValue("multiRepo", multiRepo);
        command.Parameters.AddWithValue("unservedRuns", unservedRuns);
    }

    /// <summary>
    /// Hands a claimed run back before it starts. A machine that serves several
    /// organizations never executes two of them at once, and its long polls for each run
    /// side by side while it is idle, so two claims can land together; the one it cannot
    /// take now goes back to the head of the queue for the next poll, its token revoked.
    /// </summary>
    private static async Task<IResult> ReleaseAsync(
        Guid runId, RunnerReleaseRequest? request, HttpContext http, AutomationDbContext db, IAgentIdentities agents,
        IRealtimePublisher realtime, ILoggerFactory loggers, CancellationToken ct)
    {
        var runnerId = RunnerId(http);
        var run = await db.Runs.AsNoTracking().SingleOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null || run.RunnerId != runnerId)
        {
            return NotFound();
        }

        var declined = request?.Reason switch
        {
            null => false,
            NoLocalRepository => true,
            _ => (bool?)null,
        };
        if (declined is null
            || (request?.Repository is { } missing && (declined != true || !run.RepositoryAliases.Contains(missing, StringComparer.Ordinal))))
        {
            return Results.ValidationProblem(
                declined is null
                    ? new Dictionary<string, string[]> { ["reason"] = [$"Leave it out, or {NoLocalRepository}."] }
                    : new Dictionary<string, string[]> { ["repository"] = ["Name one of the run's checkouts, with no-local-repository."] },
                type: ProblemTypes.Validation);
        }
        // No other runner may take a continue or a run a person sent here, so handing it back
        // would leave it queued for good: the runner runs it and reports what it is missing.
        if (declined == true && (run.ContinuesRunId is not null || run.RequestedRunnerId == runnerId))
        {
            return Conflict("This run was sent to this runner. Run it and report the failure.");
        }

        // The predicate is the guard: only this runner's run, only while nothing has started.
        // A run that started, finished or was swept in between is a 409, never a requeue.
        var released = await db.Database.ExecuteSqlAsync($"""
            UPDATE automation.runs
            SET status = 0, runner_id = NULL, assigned_at = NULL, agent_token_id = NULL,
                declined_runner_ids = CASE WHEN {declined == true} AND NOT ({runnerId} = ANY(declined_runner_ids))
                    THEN array_append(declined_runner_ids, {runnerId}) ELSE declined_runner_ids END
            WHERE id = {runId} AND status = {(short)RunStatus.Assigned} AND runner_id = {runnerId}
            """, ct);
        if (released == 0)
        {
            return Conflict("This run is not waiting to be started.");
        }

        if (run.AgentTokenId is { } tokenId)
        {
            try
            {
                await agents.RevokeTokenAsync(run.AgentUserId, tokenId, ct);
            }
            catch (Exception ex)
            {
                // The run is back in the queue either way; the token dies at its own expiry.
                loggers.CreateLogger(typeof(RunProtocolEndpoints))
                    .LogWarning(ex, "Could not revoke the per-run token of released run {RunId}", run.Id);
            }
        }

        // Which repository was missing, for the run view: the run waits for a runner that has it.
        var handedBack = declined != true ? null : request?.Repository is { } alias
            ? $"A runner handed the run back: it has no local clone of the repository {alias}."
            : "A runner handed the run back: it has no local clone of the project's repository.";
        if (handedBack is not null)
        {
            loggers.CreateLogger(typeof(RunProtocolEndpoints)).LogInformation("Run {RunId}: {Reason}", run.Id, handedBack);
        }

        await realtime.PublishAsync(run.ProjectId, "run.changed", new
        {
            runId = run.Id, itemId = run.ItemId, itemKey = run.ItemKey, status = "queued", reason = handedBack,
        }, ct);
        return Results.NoContent();
    }

    /// <summary>The failure reason, and release reason, of a runner that has no clone of the run's project.</summary>
    internal const string NoLocalRepository = "no-local-repository";

    private static async Task<IResult> StartedAsync(
        Guid runId, HttpContext http, AutomationDbContext db, IRealtimePublisher realtime,
        TimeProvider clock, CancellationToken ct)
    {
        var runnerId = RunnerId(http);
        var run = await db.Runs.SingleOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null || run.RunnerId != runnerId)
        {
            return NotFound();
        }

        if (run.Status != RunStatus.Assigned)
        {
            return Conflict("This run is not waiting to be started.");
        }

        var now = clock.GetUtcNow();
        run.Status = RunStatus.Running;
        run.StartedAt = now;
        run.LastHeartbeatAt = now;
        await db.SaveChangesAsync(ct);

        await realtime.PublishAsync(run.ProjectId, "run.changed", new
        {
            runId = run.Id, itemId = run.ItemId, itemKey = run.ItemKey, status = "running",
        }, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> LogAsync(
        Guid runId, RunnerLogRequest? request, HttpContext http, AutomationDbContext db,
        IRunRealtimePublisher realtime, IOptions<AutomationOptions> options,
        TimeProvider clock, ILoggerFactory loggers, CancellationToken ct)
    {
        var logger = loggers.CreateLogger(typeof(RunProtocolEndpoints));
        var runnerId = RunnerId(http);
        var run = await db.Runs.SingleOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null || run.RunnerId != runnerId)
        {
            return NotFound();
        }

        if (run.Status is not (RunStatus.Assigned or RunStatus.Running))
        {
            return Conflict("This run is already finished; its log is closed.");
        }

        var chunks = request?.Chunks ?? [];
        var errors = new Dictionary<string, string[]>();
        if (chunks.Count is 0 or > 512)
        {
            errors["chunks"] = ["Between 1 and 512 log chunks per batch."];
        }

        long batchBytes = 0;
        foreach (var chunk in chunks)
        {
            if (chunk.Seq is < 0 or >= RunLogChunk.TruncatedSeq || !Enum.IsDefined(chunk.Stream)
                || chunk.Text is null || chunk.Text.Length > RunLogChunk.MaxTextLength)
            {
                errors["chunks"] = ["Each chunk needs a sequence of at least 0, a stream (stdout, stderr or event) and text of at most 65,536 characters."];
                break;
            }

            batchBytes += Encoding.UTF8.GetByteCount(chunk.Text);
        }

        if (batchBytes > options.Value.MaxLogBatchBytes)
        {
            errors["chunks"] = [$"A batch carries at most {options.Value.MaxLogBatchBytes} bytes of text."];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors, type: ProblemTypes.Validation);
        }

        if (chunks.Select(chunk => chunk.Seq).Distinct().Count() != chunks.Count)
        {
            return Results.ValidationProblem(
                new Dictionary<string, string[]> { ["chunks"] = ["Each chunk in a batch needs its own sequence number."] },
                type: ProblemTypes.Validation);
        }

        var now = clock.GetUtcNow();
        var storedBytes = await RunEndpoints.StoredLogBytesAsync(db, run.Id, ct);
        if (storedBytes + batchBytes > options.Value.MaxLogBytes)
        {
            // The marker is what lets a reader say "truncated": the cap refuses the batch,
            // so the stored bytes alone never reach it. Its sequence sorts after anything a
            // runner can send, and inserting it twice is the same nothing as any replay.
            var marker = new RunnerLogChunk(RunLogChunk.TruncatedSeq, RunLogStream.Event,
                $"[log truncated: this run reached its {options.Value.MaxLogBytes:N0}-byte cap]", now);
            var markerStored = await InsertChunksAsync(db, run, [marker], now, ct);
            if (markerStored.Count > 0)
            {
                await realtime.PublishToRunAsync(run.Id, "run.log", new
                {
                    runId = run.Id, seq = marker.Seq, lines = new[] { marker.Text },
                }, ct);
            }

            return Results.Problem(
                title: "The run log exceeded its cap.",
                type: ProblemTypes.LogLimitExceeded,
                statusCode: StatusCodes.Status413RequestEntityTooLarge);
        }

        // ON CONFLICT DO NOTHING per row, not a unique violation for the batch: a retried
        // batch that overlaps what was stored must still store the lines that are new.
        var stored = await InsertChunksAsync(db, run, chunks, now, ct);
        if (stored.Count < chunks.Count)
        {
            logger.LogDebug("Run {RunId} re-sent {Count} log chunk(s) that were already stored",
                run.Id, chunks.Count - stored.Count);
        }

        if (stored.Count > 0)
        {
            var fresh = chunks.Where(chunk => stored.Contains(chunk.Seq)).OrderBy(chunk => chunk.Seq).ToList();
            await realtime.PublishToRunAsync(run.Id, "run.log", new
            {
                runId = run.Id,
                seq = fresh[0].Seq,
                lines = fresh.Select(chunk => chunk.Text).ToList(),
            }, ct);
        }

        return Results.NoContent();
    }

    private static async Task<HashSet<int>> InsertChunksAsync(
        AutomationDbContext db, Run run, IReadOnlyList<RunnerLogChunk> chunks, DateTimeOffset now, CancellationToken ct)
    {
        var seqs = chunks.Select(chunk => chunk.Seq).ToArray();
        var ats = chunks.Select(chunk => (chunk.At ?? now).ToUniversalTime()).ToArray();
        var streams = chunks.Select(chunk => (short)chunk.Stream).ToArray();
        var texts = chunks.Select(chunk => chunk.Text!).ToArray();
        var inserted = await db.Database.SqlQuery<int>($"""
            INSERT INTO automation.run_log_chunks (run_id, seq, at, stream, text, organization_id)
            SELECT {run.Id}, c.seq, c.at, c.stream, c.text, {run.OrganizationId}
            FROM unnest({seqs}, {ats}, {streams}, {texts}) AS c(seq, at, stream, text)
            ON CONFLICT (run_id, seq) DO NOTHING
            RETURNING seq AS "Value"
            """).ToListAsync(ct);
        return [.. inserted];
    }

    private static async Task<IResult> HeartbeatAsync(
        Guid runId, HttpContext http, AutomationDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var request = await ReadHeartbeatAsync(http.Request, ct);
        var runnerId = RunnerId(http);
        var now = clock.GetUtcNow();
        var sessionId = Blank(request?.SessionId);
        if (sessionId is { Length: > Run.MaxSessionIdLength })
        {
            sessionId = null;
        }

        var workspacePath = WorkspacePath(request?.WorkspacePath);
        // One conditional UPDATE rather than a tracked save: a heartbeat must never lose a
        // version race to a person's cancel, and the answer (cancel requested?) is read
        // from the row as this very statement left it.
        var beat = await db.Database.SqlQuery<bool>($"""
            UPDATE automation.runs SET last_heartbeat_at = {now}, session_id = COALESCE({sessionId}, session_id),
                workspace_path = COALESCE({workspacePath}, workspace_path)
            WHERE id = {runId} AND runner_id = {runnerId} AND status < 3
            RETURNING cancel_requested_at IS NOT NULL AS "Value"
            """).ToListAsync(ct);
        if (beat.Count == 1)
        {
            return Results.Ok(new { cancelRequested = beat[0] });
        }

        var exists = await db.Runs.AsNoTracking().AnyAsync(r => r.Id == runId && r.RunnerId == runnerId, ct);
        return exists ? Conflict("This run is already finished.") : NotFound();
    }

    /// <summary>
    /// The body is read here rather than bound: a bound JSON body gives the endpoint
    /// Content-Type metadata, and a runner beats without a body (and so without a
    /// Content-Type) until its harness names a session. Routing then prefers the API's
    /// catch-all 404 fallback, which accepts any request, and the runner reads the 404 as
    /// its run having been closed.
    /// </summary>
    private static async Task<RunnerRunHeartbeatRequest?> ReadHeartbeatAsync(
        HttpRequest request, CancellationToken ct)
    {
        if (request.ContentLength is 0 || !request.HasJsonContentType())
        {
            return null;
        }

        try
        {
            return await request.ReadFromJsonAsync<RunnerRunHeartbeatRequest>(ct);
        }
        catch (JsonException)
        {
            // The session is optional on a heartbeat; an unreadable one must not stop the beat.
            return null;
        }
    }

    private static async Task<IResult> FinishAsync(
        Guid runId, RunnerFinishRequest request, HttpContext http, AutomationDbContext db,
        IAgentIdentities agents, IRealtimePublisher realtime, TimeProvider clock,
        ILoggerFactory loggers, CancellationToken ct)
    {
        var logger = loggers.CreateLogger(typeof(RunProtocolEndpoints));
        var outcome = request.Outcome?.Trim() ?? "";
        var summary = Blank(request.Summary);
        var pullRequestUrls = (request.PullRequestUrls ?? []).Select(Blank).OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var pullRequestUrl = Blank(request.PullRequestUrl) ?? pullRequestUrls.FirstOrDefault();
        if (pullRequestUrl is not null && !pullRequestUrls.Contains(pullRequestUrl, StringComparer.OrdinalIgnoreCase) && pullRequestUrls.Length > 0)
        {
            pullRequestUrls = [pullRequestUrl, .. pullRequestUrls];
        }
        var failureReason = Blank(request.FailureReason);
        if (FinishErrors(outcome, summary, pullRequestUrl, failureReason, request) is { Count: > 0 } errors)
        {
            return Results.ValidationProblem(errors, type: ProblemTypes.Validation);
        }

        if (failureReason is null && outcome == RunOutcomes.Failed)
        {
            failureReason = "The runner reported failure without a reason.";
        }

        var runnerId = RunnerId(http);
        await db.Database.OpenConnectionAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // Lock first, read second: the predicate re-checks against the row as it stands
        // after any concurrent finisher's lock is released, so a double finish is a 409
        // rather than two terminal writes, and the row we read is the one we hold.
        bool stillLive;
        await using (var command = ((NpgsqlConnection)db.Database.GetDbConnection()).CreateCommand())
        {
            command.Transaction = (NpgsqlTransaction)transaction.GetDbTransaction();
            command.CommandText = "SELECT id FROM automation.runs WHERE id = @runId AND status < 3 FOR UPDATE";
            command.Parameters.AddWithValue("runId", runId);
            stillLive = await command.ExecuteScalarAsync(ct) is not null;
        }

        var run = await db.Runs.SingleOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null || run.RunnerId != runnerId)
        {
            return NotFound();
        }

        if (!stillLive)
        {
            return Conflict("This run is already finished.");
        }

        // A cancel was asked for: whatever the runner reports, its finish is the
        // acknowledgement. An item somebody asked to stop must not move to the success
        // state because the agent happened to complete - the summary and PR still land.
        if (run.CancelRequestedAt is not null)
        {
            outcome = RunOutcomes.Cancelled;
        }

        run.OutcomeSummary = summary;
        run.PullRequestUrl = pullRequestUrl;
        run.PullRequestUrls = pullRequestUrls;
        run.ExitCode = request.ExitCode;
        run.CostUsd = request.CostUsd;
        run.InputTokens = request.InputTokens;
        run.OutputTokens = request.OutputTokens;
        run.FailureReason = failureReason;
        run.SessionId = Blank(request.SessionId) ?? run.SessionId;
        run.WorkspacePath = WorkspacePath(request.WorkspacePath) ?? run.WorkspacePath;
        if (Blank(request.BranchName) is { } branchName && branchName != run.BranchName)
        {
            // Only the one switch a follow-up may make: to the fresh branch it was offered,
            // after the earlier pull request closed.
            if (!run.IsFollowUp || run.WorkOnDefaultBranch || branchName != BranchNames.Next(run.BranchName))
            {
                return Results.ValidationProblem(
                    new Dictionary<string, string[]> { ["branchName"] = ["Only a follow-up run may report the new branch it was offered."] },
                    type: ProblemTypes.Validation);
            }
            run.BranchName = branchName;
        }
        run.Finish(
            outcome == RunOutcomes.Succeeded ? RunStatus.Succeeded
                : outcome == RunOutcomes.Failed ? RunStatus.Failed
                : RunStatus.Cancelled, // the only outcome left after validation
            outcome, clock.GetUtcNow());
        run.ScheduleAutoContinue(run.FinishedAt!.Value);
        await RunCompletion.StageAsync(db, run, outcome, summary, pullRequestUrl, failureReason, ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        if (run.AgentTokenId is { } tokenId)
        {
            try
            {
                await agents.RevokeTokenAsync(run.AgentUserId, tokenId, ct);
            }
            catch (Exception ex)
            {
                // The run is finished either way; a token that outlives it by minutes
                // (its own expiry) is not worth failing the response over.
                logger.LogWarning(ex, "Could not revoke the per-run token of run {RunId}", run.Id);
            }
        }

        AutomationMetrics.Finished.Add(1, AutomationMetrics.OutcomeTag(outcome));
        await realtime.PublishAsync(run.ProjectId, "run.changed", new
        {
            runId = run.Id, itemId = run.ItemId, itemKey = run.ItemKey, status = outcome,
        }, ct);

        // The runner owns the run it just ended: no visibility tier applies.
        return Results.Ok(RunEndpoints.ToView(run, new Dictionary<string, UserSummary>(), includeDetails: true));
    }

    private static async Task<IResult> RepoTokenAsync(
        Guid runId, RunnerRepoTokenRequest? request, HttpContext http, AutomationDbContext db, IRepositoryCredentials repoCredentials,
        CancellationToken ct)
    {
        var runnerId = RunnerId(http);
        var run = await db.Runs.AsNoTracking().SingleOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null || run.RunnerId != runnerId)
        {
            return NotFound();
        }

        if (!run.IsLive)
        {
            return Conflict("This run is already finished.");
        }

        if (request?.Alias is { } alias)
        {
            // A checkout of a multi-repository run: the repository it was dispatched with, nothing else.
            if (run.Repositories.FirstOrDefault(repo => repo.Alias == alias) is not { } checkout)
            {
                return NotFound();
            }
            return Results.Ok(new
            {
                token = RepositoryPlans.NeedsLocal(checkout)
                    ? null
                    : await repoCredentials.GetCloneTokenAsync(run.ProjectId, checkout.RepoFullName!, ct),
            });
        }

        var settings = await db.ProjectSettings.AsNoTracking()
            .SingleOrDefaultAsync(row => row.ProjectId == run.ProjectId, ct);
        string? token = null;
        if (settings?.RepoSource == ProjectRepositorySource.GitHubBinding && settings.RepoFullName is { } repoFullName)
        {
            token = await repoCredentials.GetCloneTokenAsync(run.ProjectId, repoFullName, ct);
        }

        return Results.Ok(new { token });
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────

    private static Guid RunnerId(HttpContext http) =>
        // The policy requires the claim, so a parse failure is a bug rather than a request to refuse.
        Guid.Parse(http.User.FindFirstValue(PrincipalClaims.Runner)!);

    private static Dictionary<string, string[]> ClaimErrors(RunnerClaimRequest? request)
    {
        var errors = new Dictionary<string, string[]>();
        var harnesses = request?.Harnesses;
        if (harnesses is null || harnesses.Count is 0 or > 16
            || harnesses.Any(harness => !RunnerEndpoints.HarnessName().IsMatch(harness)))
        {
            errors["harnesses"] = ["Between 1 and 16 harnesses, each named in lower case (claude, codex, opencode, cursor, copilot)."];
        }

        if (request is { Slots: < 1 or > 16 })
        {
            errors["slots"] = ["Between 1 and 16."];
        }

        return errors;
    }

    private static Dictionary<string, string[]> FinishErrors(
        string outcome, string? summary, string? pullRequestUrl, string? failureReason, RunnerFinishRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (outcome is not (RunOutcomes.Succeeded or RunOutcomes.Failed or RunOutcomes.Cancelled))
        {
            errors["outcome"] = ["The outcome is succeeded, failed, or cancelled - timed_out is the server's verdict, not a runner's."];
        }

        if (summary is { Length: > Run.MaxOutcomeSummaryLength })
        {
            errors["summary"] = ["Use 4,000 characters or fewer."];
        }

        if (pullRequestUrl is { Length: > Run.MaxPullRequestUrlLength }
            || (pullRequestUrl is not null
                && !(Uri.TryCreate(pullRequestUrl, UriKind.Absolute, out var uri)
                     && uri is { Scheme: "http" or "https" })))
        {
            errors["pullRequestUrl"] = ["An absolute http(s) URL of at most 2,048 characters."];
        }

        if (request.PullRequestUrls is { } urls && (urls.Count > ProjectRepository.MaxRepositories + 1
            || urls.Any(url => url is null || url.Length > Run.MaxPullRequestUrlLength
                || !(Uri.TryCreate(url.Trim(), UriKind.Absolute, out var each) && each is { Scheme: "http" or "https" }))))
        {
            errors["pullRequestUrls"] = ["At most 21 absolute http(s) URLs of at most 2,048 characters each."];
        }

        if (failureReason is { Length: > Run.MaxFailureReasonLength })
        {
            errors["failureReason"] = ["Use 500 characters or fewer."];
        }

        if (request.CostUsd is < 0)
        {
            errors["costUsd"] = ["Cost cannot be negative."];
        }

        if (request.SessionId is { Length: > Run.MaxSessionIdLength })
        {
            errors["sessionId"] = ["Use 200 characters or fewer."];
        }

        if (Blank(request.WorkspacePath) is { } path && WorkspacePath(path) is null)
        {
            errors["workspacePath"] = ["An absolute path of at most 1,024 characters."];
        }

        if (request.InputTokens is < 0 || request.OutputTokens is < 0)
        {
            errors["inputTokens"] = ["Token counts cannot be negative."];
        }

        return errors;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// A checkout path worth storing: absolute (POSIX or Windows), single-line and bounded. It
    /// ends up in a shell command a person copies, so anything else is dropped rather than kept.
    /// </summary>
    private static string? WorkspacePath(string? value) =>
        value is { Length: > 0 and <= Run.MaxWorkspacePathLength }
        && (value.StartsWith('/') || WindowsPath().IsMatch(value))
        && !value.Any(char.IsControl)
            ? value
            : null;

    [GeneratedRegex(@"^[A-Za-z]:[\\/]")]
    private static partial Regex WindowsPath();

    private static IResult Conflict(string detail) => Results.Problem(
        title: "Conflict.", detail: detail,
        type: ProblemTypes.Conflict, statusCode: StatusCodes.Status409Conflict);

    private static IResult NotFound() => Results.Problem(
        title: "Not found.", detail: "The record does not exist, or you do not have access to it.",
        type: ProblemTypes.NotAMember, statusCode: StatusCodes.Status404NotFound);
}

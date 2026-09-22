using System.Security.Claims;
using System.Text;
using Aictiq.Modules.Automation.Auth;
using Aictiq.Modules.Automation.Domain;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Email;
using Aictiq.SharedKernel.Tenancy;
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

/// <param name="AgentToken">The per-run agent token's secret. Exists once, like every token secret;
/// it is revoked when the run finishes and dies with <paramref name="MaxMinutes"/> even if the runner never reports.</param>
public sealed record RunnerRunClaimed(Guid RunId, Guid ItemId, string ItemKey, Guid ProjectId, string ProjectKey,
    string OrganizationSlug, string Harness, string Prompt, Guid? PlaybookRevisionId, RunRepoView Repo,
    string DefaultBranch, string BranchName, int MaxMinutes, string? AictiqUrl, string AgentToken,
    string? AgentTokenDisplay, int HeartbeatIntervalSeconds);

/// <param name="Source"><c>github</c> when the project has a binding, <c>local</c> when the runner is expected to find the working copy itself.</param>
public sealed record RunRepoView(string Source, string? RepoFullName, string? CloneToken, string? LocalPathHint);

public sealed record RunnerLogRequest(IReadOnlyList<RunnerLogChunk>? Chunks);

/// <param name="At">When the line was produced. Null means it arrives at the server's clock.</param>
public sealed record RunnerLogChunk(int Seq, RunLogStream Stream, string? Text, DateTimeOffset? At);

public sealed record RunnerFinishRequest(string? Outcome, int? ExitCode, string? Summary, string? PullRequestUrl,
    decimal? CostUsd, long? InputTokens, long? OutputTokens, string? FailureReason);

/// <summary>
/// The half of the runner protocol that concerns runs: claim, started, log, heartbeat,
/// finish, repo-token. Same group prefix and policy as the hello/heartbeat pair - nothing
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
        IOptions<EmailOptions> email, TimeProvider clock, ILoggerFactory loggers, CancellationToken ct)
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
        var deadline = clock.GetUtcNow() + TimeSpan.FromSeconds(options.Value.PollTimeoutSeconds);

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

            // The lock lives inside this transaction, so the row stays ours until the
            // assignment is saved; SKIP LOCKED hands a second poller the next run instead
            // of waiting on this one. Raw SQL because EF would bury the locking clause in
            // a subquery, where Postgres ignores it.
            Guid? claimedRunId;
            await using (var command = ((NpgsqlConnection)db.Database.GetDbConnection()).CreateCommand())
            {
                command.Transaction = (NpgsqlTransaction)transaction.GetDbTransaction();
                command.CommandText = """
                    SELECT id FROM automation.runs
                    WHERE organization_id = @organizationId AND status = 0 AND harness = ANY(@harnesses)
                    ORDER BY queued_at
                    FOR UPDATE SKIP LOCKED
                    LIMIT 1
                    """;
                command.Parameters.AddWithValue("organizationId", organizationId);
                command.Parameters.AddWithValue("harnesses", harnesses);
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
                issued = await agents.IssueTokenAsync(run.AgentUserId, organizationId, $"run:{run.Id}",
                    [Scopes.Read, Scopes.Write, Scopes.Mcp], now.AddMinutes(run.MaxMinutes + 10), ct);
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

            await realtime.PublishAsync(run.ProjectId, "run.changed", new
            {
                runId = run.Id, itemId = run.ItemId, itemKey = run.ItemKey,
                status = "assigned", runnerId,
            }, ct);

            // The public URL is Email:BaseUrl when an instance has one; otherwise the address
            // the runner reached us on is the best statement of where Aictiq lives.
            var aictiqUrl = string.IsNullOrWhiteSpace(email.Value.BaseUrl)
                ? $"{http.Request.Scheme}://{http.Request.Host}"
                : email.Value.BaseUrl.TrimEnd('/');

            return (new RunnerRunClaimed(
                run.Id, run.ItemId, run.ItemKey, run.ProjectId,
                RunEndpoints.ProjectKeyOf(run.ItemKey) ?? run.ItemKey,
                organization?.Slug ?? "",
                run.Harness, run.PromptSnapshot, run.PlaybookRevisionId, repo,
                settings?.DefaultBranch ?? "main", run.BranchName, run.MaxMinutes,
                aictiqUrl, issued.Secret, issued.Token.Display,
                options.Value.HeartbeatIntervalSeconds), false);
        }
    }

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
        var runnerId = RunnerId(http);
        var now = clock.GetUtcNow();
        // One conditional UPDATE rather than a tracked save: a heartbeat must never lose a
        // version race to a person's cancel, and the answer (cancel requested?) is read
        // from the row as this very statement left it.
        var beat = await db.Database.SqlQuery<bool>($"""
            UPDATE automation.runs SET last_heartbeat_at = {now}
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

    private static async Task<IResult> FinishAsync(
        Guid runId, RunnerFinishRequest request, HttpContext http, AutomationDbContext db,
        IAgentIdentities agents, IRealtimePublisher realtime, TimeProvider clock,
        ILoggerFactory loggers, CancellationToken ct)
    {
        var logger = loggers.CreateLogger(typeof(RunProtocolEndpoints));
        var outcome = request.Outcome?.Trim() ?? "";
        var summary = Blank(request.Summary);
        var pullRequestUrl = Blank(request.PullRequestUrl);
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
        run.ExitCode = request.ExitCode;
        run.CostUsd = request.CostUsd;
        run.InputTokens = request.InputTokens;
        run.OutputTokens = request.OutputTokens;
        run.FailureReason = failureReason;
        run.Finish(
            outcome == RunOutcomes.Succeeded ? RunStatus.Succeeded
                : outcome == RunOutcomes.Failed ? RunStatus.Failed
                : RunStatus.Cancelled, // the only outcome left after validation
            outcome, clock.GetUtcNow());
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
        Guid runId, HttpContext http, AutomationDbContext db, IRepositoryCredentials repoCredentials,
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
            errors["harnesses"] = ["Between 1 and 16 harnesses, each named in lower case (claude, codex, opencode)."];
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

        if (failureReason is { Length: > Run.MaxFailureReasonLength })
        {
            errors["failureReason"] = ["Use 500 characters or fewer."];
        }

        if (request.CostUsd is < 0)
        {
            errors["costUsd"] = ["Cost cannot be negative."];
        }

        if (request.InputTokens is < 0 || request.OutputTokens is < 0)
        {
            errors["inputTokens"] = ["Token counts cannot be negative."];
        }

        return errors;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static IResult Conflict(string detail) => Results.Problem(
        title: "Conflict.", detail: detail,
        type: ProblemTypes.Conflict, statusCode: StatusCodes.Status409Conflict);

    private static IResult NotFound() => Results.Problem(
        title: "Not found.", detail: "The record does not exist, or you do not have access to it.",
        type: ProblemTypes.NotAMember, statusCode: StatusCodes.Status404NotFound);
}

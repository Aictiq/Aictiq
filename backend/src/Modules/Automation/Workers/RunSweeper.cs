using Aictiq.Modules.Automation.Domain;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Aictiq.Modules.Automation.Workers;

/// <summary>
/// Settles the runs nobody else will end: a runner that stopped answering (its last
/// heartbeat, or its assignment if it never spoke, is older than
/// <see cref="AutomationOptions.RunnerLostAfterMinutes"/>) and a run that outlived its
/// playbook's time limit. Both verdicts go through the same path as a runner's own
/// finish — terminal status, token revoked, <see cref="RunFinished"/> staged — so the
/// item's history cannot tell them apart.
/// </summary>
/// <remarks>
/// Per organization, not one cross-tenant query: row-level security admits
/// <c>aictiq_app</c> only the rows of the organization in its session, so a sweep that
/// forgot the tenant would see nothing — a bug that would be invisible in development,
/// where the connection outranks RLS. Each candidate is locked, re-checked and written
/// in its own transaction, so a runner finishing its own run concurrently wins and the
/// sweep simply moves on.
/// </remarks>
public sealed class RunSweeper(
    IServiceScopeFactory scopeFactory,
    NpgsqlDataSource dataSource,
    IOptions<AutomationOptions> options,
    TimeProvider clock,
    ILogger<RunSweeper> logger) : BackgroundService
{
    private const int BatchSize = 50;

    private const string LockLiveRunSql = "SELECT id FROM automation.runs WHERE id = @runId AND status < 3 FOR UPDATE";

    private readonly AutomationOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the API finish migrating before the first sweep touches the tables.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(15), clock, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A fresh database before migrations, a transient outage — try again.
                logger.LogWarning(ex, "Run sweep failed; retrying on the next tick");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), clock, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>One sweep over every organization. Public so tests can drive it.</summary>
    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        // Organizations themselves are not tenant-protected — the tenant middleware must
        // be able to resolve one before any tenant exists — so their ids are readable
        // without a session. Raw SQL across another module's schema is the established
        // sweep pattern (Identity's retention sweep deliberately touches three schemas).
        var organizationIds = new List<Guid>();
        await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken))
        await using (var command = new NpgsqlCommand("SELECT id FROM tenancy.organizations", connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                organizationIds.Add(reader.GetGuid(0));
            }
        }

        foreach (var organizationId in organizationIds)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                using var tenantScope = scope.ServiceProvider.GetRequiredService<AmbientCurrentTenant>()
                    .Use(organizationId);
                await SweepLostRunsAsync(scope.ServiceProvider, cancellationToken);
                await SweepTimedOutRunsAsync(scope.ServiceProvider, cancellationToken);
                await SweepReadOnlyQueuedRunsAsync(scope.ServiceProvider, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Run sweep failed for organization {OrganizationId}", organizationId);
            }
        }
    }

    private async Task SweepLostRunsAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var db = services.GetRequiredService<AutomationDbContext>();
        var cutoff = clock.GetUtcNow() - TimeSpan.FromMinutes(_options.RunnerLostAfterMinutes);
        var candidates = await db.Runs.AsNoTracking()
            .Where(run => run.Status == RunStatus.Assigned || run.Status == RunStatus.Running)
            .Where(run => (run.LastHeartbeatAt ?? run.AssignedAt ?? run.QueuedAt) < cutoff)
            .OrderBy(run => run.LastHeartbeatAt ?? run.AssignedAt ?? run.QueuedAt)
            .Take(BatchSize)
            .Select(run => run.Id)
            .ToListAsync(cancellationToken);

        foreach (var runId in candidates)
        {
            if (await SettleAsync(db, services, runId, run =>
            {
                run.Finish(RunStatus.Failed, RunOutcomes.Failed, clock.GetUtcNow());
                run.FailureReason = "runner-lost";
                return RunCompletion.StageAsync(db, run, RunOutcomes.Failed,
                    summary: null, pullRequestUrl: run.PullRequestUrl, failureReason: "runner-lost", cancellationToken);
            }, cancellationToken))
            {
                AutomationMetrics.RunnerLost.Add(1);
                AutomationMetrics.Finished.Add(1, AutomationMetrics.OutcomeTag(RunOutcomes.Failed));
            }
        }
    }

    private async Task SweepTimedOutRunsAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var db = services.GetRequiredService<AutomationDbContext>();
        var organizationId = services.GetRequiredService<ICurrentTenant>().OrganizationId!.Value;
        var now = clock.GetUtcNow();
        // Raw SQL because the limit is per row (started_at + max_minutes), which LINQ over
        // DateTimeOffset cannot express. Raw SQL skips the tenant filter, so the
        // organization predicate is stated here rather than left to RLS.
        var candidates = await db.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value" FROM automation.runs
            WHERE organization_id = {organizationId} AND status = {(short)RunStatus.Running}
              AND started_at + make_interval(mins => max_minutes) < {now}
            ORDER BY started_at
            LIMIT {BatchSize}
            """).ToListAsync(cancellationToken);

        foreach (var runId in candidates)
        {
            if (await SettleAsync(db, services, runId, run =>
            {
                run.Finish(RunStatus.TimedOut, RunOutcomes.TimedOut, clock.GetUtcNow());
                run.FailureReason = "The run exceeded its time limit.";
                return RunCompletion.StageAsync(db, run, RunOutcomes.TimedOut,
                    summary: null, pullRequestUrl: run.PullRequestUrl, failureReason: run.FailureReason, cancellationToken);
            }, cancellationToken))
            {
                AutomationMetrics.Finished.Add(1, AutomationMetrics.OutcomeTag(RunOutcomes.TimedOut));
            }
        }
    }

    /// <summary>
    /// Cancels the still-queued runs of an organization that may not write any more:
    /// an expired evaluation or a lapsed payment stops new dispatches at every door, and a
    /// queued run is work nobody has taken — leaving it waiting would hand it to the next
    /// runner that polls, working on for an organization that is read-only. Runs already
    /// assigned or running are left to finish within their existing deadlines, exactly like
    /// a runner's own outcome writes; only the never-started are withdrawn.
    /// </summary>
    private async Task SweepReadOnlyQueuedRunsAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var billing = services.GetRequiredService<IOrganizationBillingState>();
        var organizationId = services.GetRequiredService<ICurrentTenant>().OrganizationId!.Value;
        if (!await billing.IsReadOnlyAsync(organizationId, cancellationToken)) return;

        var db = services.GetRequiredService<AutomationDbContext>();
        var candidates = await db.Runs.AsNoTracking()
            .Where(run => run.Status == RunStatus.Queued)
            .OrderBy(run => run.QueuedAt)
            .Take(BatchSize)
            .Select(run => run.Id)
            .ToListAsync(cancellationToken);

        foreach (var runId in candidates)
        {
            if (await SettleAsync(db, services, runId, run =>
            {
                run.Finish(RunStatus.Cancelled, RunOutcomes.Cancelled, clock.GetUtcNow());
                run.FailureReason = "The organization is read-only; the queued run was cancelled.";
                return RunCompletion.StageAsync(db, run, RunOutcomes.Cancelled,
                    summary: null, pullRequestUrl: null, failureReason: run.FailureReason, cancellationToken);
            }, cancellationToken))
            {
                AutomationMetrics.Finished.Add(1, AutomationMetrics.OutcomeTag(RunOutcomes.Cancelled));
            }
        }
    }

    /// <summary>
    /// Locks, re-checks and settles one run in its own transaction. Locking before
    /// reading means the live-check runs against the row as it stands after any
    /// concurrent finisher — a runner reporting its own finish in the same instant wins,
    /// and the sweep finds the row already terminal and skips it. The token is revoked
    /// only after the terminal state is durable: a sweep that dies between the two leaves
    /// a finished run with a token that expires on its own, never a live run nobody will
    /// end.
    /// </summary>
    /// <returns>False when the run was already settled by someone else; only a true return counts a metric.</returns>
    private async Task<bool> SettleAsync(
        AutomationDbContext db, IServiceProvider services, Guid runId,
        Func<Run, Task> settle, CancellationToken cancellationToken)
    {
        var agents = services.GetRequiredService<IAgentIdentities>();
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await using (var command = ((NpgsqlConnection)db.Database.GetDbConnection()).CreateCommand())
            {
                command.Transaction = (NpgsqlTransaction)transaction.GetDbTransaction();
                command.CommandText = LockLiveRunSql;
                command.Parameters.AddWithValue("runId", runId);
                if (await command.ExecuteScalarAsync(cancellationToken) is null)
                {
                    return false;
                }
            }

            var run = await db.Runs.SingleAsync(r => r.Id == runId, cancellationToken);
            await settle(run);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            if (run.AgentTokenId is { } tokenId)
            {
                try
                {
                    await agents.RevokeTokenAsync(run.AgentUserId, tokenId, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The run is settled and will not be swept again; the token still dies
                    // at its own expiry (max minutes + 10).
                    logger.LogWarning(ex, "Could not revoke the per-run token of run {RunId}", run.Id);
                }
            }

            await services.GetRequiredService<IRealtimePublisher>().PublishAsync(run.ProjectId, "run.changed", new
            {
                runId = run.Id, itemId = run.ItemId, itemKey = run.ItemKey,
                status = run.Status == RunStatus.TimedOut ? RunOutcomes.TimedOut : RunOutcomes.Failed,
            }, cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // One bad run must not stall the rest of the batch; the next tick retries it.
            // The tracker is cleared so a half-applied entity cannot poison the next row's save.
            db.ChangeTracker.Clear();
            logger.LogError(ex, "Run sweep could not settle run {RunId}", runId);
            return false;
        }
    }
}

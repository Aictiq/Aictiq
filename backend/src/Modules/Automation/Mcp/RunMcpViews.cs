using System.Text.Json;
using Aictiq.Modules.Automation.Domain;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Mcp;
using Aictiq.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Aictiq.Modules.Automation.Mcp;

/// <summary>
/// What the run tools and the <c>aictiq://run/{id}</c> resource have in common: who may see
/// a run, and the two tiers of what they see. Anyone with a role on the project sees the
/// run — it is the item's history; the log and the failure reason are the raw agent output
/// and belong to factory operators, exactly as on the REST surface.
/// </summary>
internal static class RunMcpViews
{
    /// <summary>The tail <c>get_run</c> and the resource carry; the CLI pages the rest.</summary>
    public const int LogTail = 50;

    public sealed record VisibleRun(Run Run, bool IsOperator);

    public sealed record LogLine(int Seq, DateTimeOffset At, string Stream, string Text);

    public static async Task<VisibleRun?> FindAsync(
        AutomationDbContext db, IProjectAccess access, ICurrentUser user, ICurrentTenant tenant,
        Guid runId, CancellationToken ct)
    {
        if (user.UserId is not { } userId || tenant.OrganizationId is not { } organizationId)
        {
            return null;
        }
        var run = await db.Runs.AsNoTracking().SingleOrDefaultAsync(run => run.Id == runId, ct);
        if (run is null || await access.GetProjectRoleAsync(userId, run.ProjectId, ct) is null)
        {
            return null;
        }
        return new VisibleRun(run, await access.CanOperateFactoryAsync(userId, organizationId, ct));
    }

    /// <summary>The wire spelling of a status — <c>timedOut</c>, never <c>6</c>.</summary>
    public static string StatusName(RunStatus status) => JsonNamingPolicy.CamelCase.ConvertName(status.ToString());

    public static async Task<IReadOnlyList<LogLine>> LogTailAsync(AutomationDbContext db, Guid runId, CancellationToken ct)
    {
        var chunks = await db.RunLogChunks.AsNoTracking()
            .Where(chunk => chunk.RunId == runId && chunk.Seq != RunLogChunk.TruncatedSeq)
            .OrderByDescending(chunk => chunk.Seq)
            .Take(LogTail)
            .ToListAsync(ct);
        chunks.Reverse();
        return chunks.Select(chunk => new LogLine(chunk.Seq, chunk.At, chunk.Stream.ToString().ToLowerInvariant(), chunk.Text)).ToList();
    }

    public static async Task<Names> NamesAsync(AutomationDbContext db, IUserDirectory directory, Run run, CancellationToken ct)
    {
        var ids = run.RequestedBy is { } requestedBy ? new[] { run.AgentUserId, requestedBy } : [run.AgentUserId];
        var people = await directory.GetAsync(ids, ct);
        var playbook = await db.Playbooks.AsNoTracking().Where(playbook => playbook.Id == run.PlaybookId)
            .Select(playbook => playbook.Name).FirstOrDefaultAsync(ct);
        var runner = run.RunnerId is { } runnerId
            ? await db.Runners.AsNoTracking().Where(runner => runner.Id == runnerId).Select(runner => runner.Name).FirstOrDefaultAsync(ct)
            : null;
        var rule = run.RuleId is { } ruleId
            ? await db.Rules.AsNoTracking().Where(rule => rule.Id == ruleId).Select(rule => rule.Name).FirstOrDefaultAsync(ct)
            : null;
        return new Names(
            people.TryGetValue(run.AgentUserId, out var agent) ? agent.DisplayName : null,
            run.RequestedBy is { } id && people.TryGetValue(id, out var requester) ? requester.DisplayName : null,
            playbook, runner, rule);
    }

    public sealed record Names(string? Agent, string? RequestedBy, string? Playbook, string? Runner, string? Rule = null);

    /// <summary>The <c>get_run</c> answer. The summary and the log are agent-written text, hence the boundary.</summary>
    public static async Task<object> DetailAsync(AutomationDbContext db, IUserDirectory directory, VisibleRun visible, CancellationToken ct)
    {
        var run = visible.Run;
        var names = await NamesAsync(db, directory, run, ct);
        var log = visible.IsOperator ? await LogTailAsync(db, run.Id, ct) : null;
        return new
        {
            contentStart = McpContentBoundary.Begin,
            id = run.Id, run.ItemKey, status = StatusName(run.Status),
            agentId = run.AgentUserId, agentName = names.Agent,
            playbook = names.Playbook, runner = names.Runner,
            run.RequestedBy, requestedByName = names.RequestedBy,
            run.RuleId, ruleName = names.Rule,
            run.Harness, run.MaxMinutes,
            run.QueuedAt, run.AssignedAt, run.StartedAt, run.FinishedAt, run.LastHeartbeatAt,
            cancelRequested = run.CancelRequestedAt is not null,
            run.OutcomeSummary, run.PullRequestUrl, run.ExitCode, run.CostUsd, run.InputTokens, run.OutputTokens,
            failureReason = visible.IsOperator ? run.FailureReason : null,
            log,
            contentEnd = McpContentBoundary.End,
        };
    }
}

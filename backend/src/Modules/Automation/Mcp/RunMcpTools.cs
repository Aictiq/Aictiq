using System.ComponentModel;
using Aictiq.Modules.Automation.Endpoints;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Mcp;
using Aictiq.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Aictiq.Modules.Automation.Mcp;

/// <summary>
/// The factory over MCP: an orchestrating agent hands a subtask to a worker the way a
/// person does from the item page. Same rules as REST, not weaker ones: the project is
/// resolved through <see cref="IProjectAccess"/> from the key's prefix, a dispatch needs the
/// project Member role, an un-archived project and the factory operator flag (an
/// agent whose flag an Admin cleared may not delegate), and the run itself comes from
/// <see cref="RunDispatcher"/>, the one dispatch path every door shares. The run records the
/// caller as the requester, whichever agent it runs as.
/// </summary>
[McpServerToolType]
public sealed class RunMcpTools(
    AutomationDbContext db, ICurrentTenant tenant, ICurrentUser user, IProjectAccess access,
    IUserDirectory directory, IOrganizationBillingState billing, IWorkItemLookup items,
    RunDispatcher dispatcher)
{
    [McpServerTool(Name = "start_run")]
    [Description("Hands a work item to an agent by queueing a factory run for it. Needs whoami.canOperateFactory. playbook and agent may be a name or an id; each defaults to the project's factory settings. Answers { runId } or, in words, why not.")]
    public async Task<object?> StartRun(string key, string? playbook = null, string? agent = null, CancellationToken cancellationToken = default)
    {
        var (project, role) = await ProjectForKeyAsync(key, cancellationToken) ?? throw NotFound(key);
        // A Guest sees the item and may not write it: the same "not found or no access" the
        // work-item tools answer, rather than a 403 that a REST route filter would produce.
        if (!role.Satisfies(ProjectRole.Member)) throw NotFound(key);
        if (project.IsArchived) throw new McpAnswerException($"project '{project.Key}' is archived and read-only");
        if (await billing.IsReadOnlyAsync(tenant.OrganizationId!.Value, cancellationToken)) throw new McpAnswerException("organization is read-only");
        if (!await access.CanOperateFactoryAsync(user.UserId!, tenant.OrganizationId!.Value, cancellationToken)) throw new McpAnswerException("not permitted to operate the factory");

        Guid? playbookId = playbook is null ? null : await ResolvePlaybookAsync(project.Id, playbook, cancellationToken);
        var agentId = agent is null ? null : await ResolveAgentAsync(project.Id, agent, cancellationToken);

        var result = await dispatcher.DispatchAsync(
            project, Normalize(key), playbookId, agentId, DispatchActor.User(user.UserId!), cancellationToken);
        return result.Outcome switch
        {
            DispatchOutcome.Created => new
            {
                runId = result.Run!.Id, result.Run.ItemKey, agentId = result.Run.AgentUserId,
                agentName = result.Agent!.DisplayName, status = RunMcpViews.StatusName(result.Run.Status),
            },
            DispatchOutcome.ItemNotFound => throw NotFound(key),
            DispatchOutcome.PlaybookNotFound => throw new McpAnswerException("playbook not found or no access"),
            DispatchOutcome.Validation => throw new McpException(result.Message!),
            DispatchOutcome.ItemClaimed => throw new McpAnswerException("item already claimed"),
            DispatchOutcome.RunInProgress => throw new McpAnswerException("conflict: run in progress"),
            DispatchOutcome.ReadOnly => throw new McpAnswerException("conflict: organization is read-only (billing)"),
            _ => throw new InvalidOperationException($"Unhandled dispatch outcome {result.Outcome}."),
        };
    }

    [McpServerTool(Name = "get_run", ReadOnly = true)]
    [Description("A factory run: status, timings, outcome, pull request and — for factory operators — the failure reason and the last 50 log lines.")]
    public async Task<object?> GetRun(Guid runId, CancellationToken cancellationToken = default)
    {
        var visible = await RunMcpViews.FindAsync(db, access, user, tenant, runId, cancellationToken)
            ?? throw new McpAnswerException("run not found or no access");
        return await RunMcpViews.DetailAsync(db, directory, visible, cancellationToken);
    }

    [McpServerTool(Name = "list_runs", ReadOnly = true)]
    [Description("The factory runs of a work item, newest first.")]
    public async Task<IReadOnlyList<object>> ListRuns(string key, int limit = 50, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 200) throw new McpException("limit must be between 1 and 200.");
        var (project, _) = await ProjectForKeyAsync(key, cancellationToken) ?? throw NotFound(key);
        var item = (await items.FindByKeysAsync(project.Id, [Normalize(key)], cancellationToken)).FirstOrDefault() ?? throw NotFound(key);

        var runs = await db.Runs.AsNoTracking().Where(run => run.ItemId == item.Id)
            .OrderByDescending(run => run.QueuedAt).Take(limit).ToListAsync(cancellationToken);
        var people = await directory.GetAsync([.. runs.Select(run => run.AgentUserId).Distinct()], cancellationToken);
        var playbookIds = runs.Select(run => run.PlaybookId).Distinct().ToList();
        var playbooks = await db.Playbooks.AsNoTracking().Where(playbook => playbookIds.Contains(playbook.Id))
            .ToDictionaryAsync(playbook => playbook.Id, playbook => playbook.Name, cancellationToken);
        return runs.Select(run => (object)new
        {
            id = run.Id, run.ItemKey, status = RunMcpViews.StatusName(run.Status),
            agentId = run.AgentUserId,
            agentName = people.TryGetValue(run.AgentUserId, out var agent) ? agent.DisplayName : null,
            playbook = playbooks.GetValueOrDefault(run.PlaybookId),
            run.RequestedBy, run.QueuedAt, run.FinishedAt, run.OutcomeSummary, run.PullRequestUrl,
        }).ToList();
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────

    private static string Normalize(string key) => key.Trim().ToUpperInvariant();

    private static McpAnswerException NotFound(string key) => new($"item '{key}' not found or no access");

    /// <summary>The item's project and the caller's role in it, or null when either is not theirs to see.</summary>
    private async Task<(ProjectRef Project, ProjectRole Role)?> ProjectForKeyAsync(string key, CancellationToken ct)
    {
        if (tenant.OrganizationId is not { } organizationId || user.UserId is not { } userId) return null;
        if (RunEndpoints.ProjectKeyOf(Normalize(key)) is not { } projectKey) return null;
        var project = await access.FindProjectAsync(organizationId, projectKey, ct);
        if (project is null) return null;
        var role = await access.GetProjectRoleAsync(userId, project.Id, ct);
        return role is null ? null : (project, role.Value);
    }

    /// <summary>A playbook of this project, by id or by name (case-insensitively).</summary>
    private async Task<Guid> ResolvePlaybookAsync(Guid projectId, string playbook, CancellationToken ct)
    {
        var text = playbook.Trim();
        var query = db.Playbooks.AsNoTracking().Where(playbook => playbook.ProjectId == projectId);
        var found = Guid.TryParse(text, out var id)
            ? await query.Where(playbook => playbook.Id == id).Select(playbook => (Guid?)playbook.Id).FirstOrDefaultAsync(ct)
            : await query.Where(playbook => playbook.Name.ToLower() == text.ToLower()).Select(playbook => (Guid?)playbook.Id).FirstOrDefaultAsync(ct);
        return found ?? throw new McpAnswerException("playbook not found or no access");
    }

    /// <summary>
    /// An agent of the project, by id or by display name. Names are matched only among the
    /// project's members, so a guessed name cannot reach an agent of another project, and an
    /// ambiguous one is refused with the ids rather than resolved by luck.
    /// </summary>
    private async Task<string> ResolveAgentAsync(Guid projectId, string agent, CancellationToken ct)
    {
        var text = agent.Trim();
        var members = await access.ListProjectMemberIdsAsync(projectId, ct);
        var people = await directory.GetAsync(members, ct);
        var candidates = people.Values
            .Where(person => person.IsAgent && (person.Id == text || string.Equals(person.DisplayName, text, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        return candidates.Count switch
        {
            1 => candidates[0].Id,
            0 => throw new McpException($"agent '{text}' is not an agent of this project; name one of its agents or pass the agent's id."),
            _ => throw new McpException($"agent '{text}' names several agents; pass an id: {string.Join(", ", candidates.Select(candidate => candidate.Id))}"),
        };
    }
}

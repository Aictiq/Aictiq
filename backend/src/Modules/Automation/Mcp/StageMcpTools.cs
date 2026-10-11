using System.ComponentModel;
using Aictiq.Modules.Automation.Domain;
using Aictiq.Modules.Automation.Endpoints;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Mcp;
using Aictiq.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Aictiq.Modules.Automation.Mcp;

/// <summary>
/// How a follow-up playbook's run answers. Only the agent of the item's live stage run may
/// call it, so a person cannot pass their own pull request and a builder run cannot approve
/// its own work. The verdict is read when the run finishes (<see cref="PlaybookChain"/>); a
/// second call before then replaces the first.
/// </summary>
[McpServerToolType]
public sealed class StageMcpTools(
    AutomationDbContext db, ICurrentTenant tenant, ICurrentUser user, IProjectAccess access,
    IWorkItemLookup items)
{
    [McpServerTool(Name = "submit_stage_verdict")]
    [Description("Follow-up playbook (stage) runs only: reports whether the pull request may go on. verdict \"pass\" means it is ready for the next stage or a person; \"changes_requested\" means the builder must change something first, and findings (required then) are its instructions: what to change and where. Call it once, last.")]
    public async Task<object> SubmitStageVerdict(
        string key, string verdict, string? findings = null, CancellationToken cancellationToken = default)
    {
        var decided = verdict?.Trim().ToLowerInvariant() switch
        {
            PlaybookChain.Pass => StageVerdict.Pass,
            PlaybookChain.ChangesRequested => StageVerdict.ChangesRequested,
            _ => throw new McpException($"verdict must be \"{PlaybookChain.Pass}\" or \"{PlaybookChain.ChangesRequested}\"."),
        };
        var note = findings?.Trim();
        if (decided == StageVerdict.ChangesRequested && string.IsNullOrEmpty(note))
            throw new McpException("changes_requested needs findings: what the builder must change.");
        if (note?.Length > Run.MaxStageFindingsLength)
            throw new McpException($"findings must be {Run.MaxStageFindingsLength} characters or fewer.");

        var normalized = key.Trim().ToUpperInvariant();
        var notFound = new McpAnswerException($"item '{key}' has no follow-up playbook run in progress for this agent");
        if (tenant.OrganizationId is not { } organizationId || user.UserId is not { } userId
            || RunEndpoints.ProjectKeyOf(normalized) is not { } projectKey)
            throw notFound;
        var project = await access.FindProjectAsync(organizationId, projectKey, cancellationToken) ?? throw notFound;
        var item = (await items.FindByKeysAsync(project.Id, [normalized], cancellationToken)).FirstOrDefault() ?? throw notFound;

        // A set-based update: the runner's heartbeats write the row all along, so a tracked
        // save would keep losing its concurrency check to them.
        var stored = string.IsNullOrEmpty(note) ? null : note;
        var live = db.Runs.Where(run => run.ItemId == item.Id
            && run.Status < RunStatus.Succeeded && run.Kind == RunKind.Stage && run.AgentUserId == userId);
        var runId = await live.Select(run => (Guid?)run.Id).SingleOrDefaultAsync(cancellationToken) ?? throw notFound;
        if (await live.ExecuteUpdateAsync(setters => setters
                .SetProperty(run => run.StageVerdict, (StageVerdict?)decided)
                .SetProperty(run => run.StageFindings, stored), cancellationToken) == 0)
            throw notFound;

        return new { key = item.Key, runId, verdict = PlaybookChain.VerdictName(decided) };
    }
}

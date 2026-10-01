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
/// How a refine run answers. Only the agent of the item's live refine run may call it, so a
/// person cannot mark their own draft "ready" and an implement run cannot stumble into it.
/// </summary>
[McpServerToolType]
public sealed class RefinementMcpTools(
    AutomationDbContext db, ICurrentTenant tenant, ICurrentUser user, IProjectAccess access,
    IWorkItemLookup items, IRealtimePublisher realtime, TimeProvider clock)
{
    public const string Ready = "ready";
    public const string NeedsInput = "needs_input";

    [McpServerTool(Name = "submit_refinement")]
    [Description("Refine runs only: reports the result of refining the item, after the ticket itself was rewritten with update_item. outcome \"ready\" means the ticket is complete (give a one-line summary); \"needs_input\" means a person must answer questions first (give 1-10 questions, only what blocks a correct ticket). Call it once, last.")]
    public async Task<object> SubmitRefinement(
        string key, string outcome, IReadOnlyList<string>? questions = null, string? summary = null,
        CancellationToken cancellationToken = default)
    {
        var status = outcome?.Trim().ToLowerInvariant() switch
        {
            Ready => RefinementStatus.Ready,
            NeedsInput => RefinementStatus.NeedsInput,
            _ => throw new McpException($"outcome must be \"{Ready}\" or \"{NeedsInput}\"."),
        };
        var asked = (questions ?? []).Select(question => question?.Trim() ?? "")
            .Where(question => question.Length > 0).ToList();
        if (status == RefinementStatus.NeedsInput && asked.Count == 0)
            throw new McpException("needs_input needs at least one question.");
        if (asked.Count > ItemRefinement.MaxQuestions)
            throw new McpException($"Ask at most {ItemRefinement.MaxQuestions} questions.");
        if (asked.Any(question => question.Length > ItemRefinement.MaxQuestionLength))
            throw new McpException($"Each question must be {ItemRefinement.MaxQuestionLength} characters or fewer.");
        var note = summary?.Trim();
        if (note?.Length > ItemRefinement.MaxSummaryLength)
            throw new McpException($"summary must be {ItemRefinement.MaxSummaryLength} characters or fewer.");

        var normalized = key.Trim().ToUpperInvariant();
        var notFound = new McpAnswerException($"item '{key}' has no refinement in progress for this agent");
        if (tenant.OrganizationId is not { } organizationId || user.UserId is not { } userId
            || RunEndpoints.ProjectKeyOf(normalized) is not { } projectKey)
            throw notFound;
        var project = await access.FindProjectAsync(organizationId, projectKey, cancellationToken) ?? throw notFound;
        var item = (await items.FindByKeysAsync(project.Id, [normalized], cancellationToken)).FirstOrDefault() ?? throw notFound;

        // The caller must be the agent its live refine run executes as; the run's own token
        // is the only credential that agent holds while the run lasts.
        var run = await db.Runs.AsNoTracking().SingleOrDefaultAsync(run => run.ItemId == item.Id
            && run.Status < RunStatus.Succeeded && run.Kind == RunKind.Refine && run.AgentUserId == userId,
            cancellationToken) ?? throw notFound;
        var refinement = await db.Refinements.SingleOrDefaultAsync(
            row => row.ItemId == item.Id && row.LastRunId == run.Id, cancellationToken) ?? throw notFound;

        refinement.Status = status;
        refinement.Questions = status == RefinementStatus.NeedsInput ? [.. asked] : [];
        refinement.Summary = string.IsNullOrEmpty(note) ? null : note;
        refinement.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        await RefinementEndpoints.PublishAsync(realtime, refinement, cancellationToken);

        return new
        {
            key = item.Key,
            status = status == RefinementStatus.Ready ? Ready : NeedsInput,
            questions = refinement.Questions,
        };
    }
}

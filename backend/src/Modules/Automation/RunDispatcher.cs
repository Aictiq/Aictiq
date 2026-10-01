using Aictiq.Modules.Automation.Domain;
using Aictiq.Modules.Automation.Prompts;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Tenancy;
using Aictiq.SharedKernel.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Aictiq.Modules.Automation;

public enum DispatchOutcome
{
    Created,

    /// <summary>The item is not in the project.</summary>
    ItemNotFound,

    /// <summary>The named playbook is not the project's, or its page cannot be read by the requester.</summary>
    PlaybookNotFound,

    /// <summary>A field is missing or wrong; <see cref="DispatchResult.Field"/> names it.</summary>
    Validation,

    /// <summary>The organization is read-only: its evaluation expired or a payment lapsed.</summary>
    ReadOnly,

    /// <summary>Someone - a person or another run - already holds the item.</summary>
    ItemClaimed,

    /// <summary>A run was inserted for the item between the claim and this insert.</summary>
    RunInProgress,

    /// <summary>The run cannot be continued; <see cref="DispatchResult.Message"/> says why.</summary>
    NotContinuable,
}

public enum DispatchActorKind { User, Rule }

/// <summary>
/// Who is asking for a run: a person, through REST or MCP, or an automation rule
///, through <c>Events/RuleFiringHandler</c>. Never a faked user - the run row
/// and every audit trail say exactly which.
/// </summary>
public sealed record DispatchActor(DispatchActorKind Kind, string? UserId, Guid? RuleId)
{
    public static DispatchActor User(string userId) => new(DispatchActorKind.User, userId, null);

    public static DispatchActor Rule(Guid ruleId) => new(DispatchActorKind.Rule, null, ruleId);
}

/// <param name="Run">The queued run, on <see cref="DispatchOutcome.Created"/>.</param>
/// <param name="Agent">The agent the run executes as, on <see cref="DispatchOutcome.Created"/>.</param>
/// <param name="Field">The offending request field, on <see cref="DispatchOutcome.Validation"/>.</param>
/// <param name="Message">What is wrong with it.</param>
/// <param name="ClaimedBy">Who holds the item, on <see cref="DispatchOutcome.ItemClaimed"/>.</param>
/// <param name="Version">The item's version at the refused claim, so a caller can show the conflict.</param>
public sealed record DispatchResult(
    DispatchOutcome Outcome, Run? Run = null, AgentIdentity? Agent = null,
    string? Field = null, string? Message = null, string? ClaimedBy = null, uint Version = 0)
{
    public static DispatchResult Created(Run run, AgentIdentity agent) => new(DispatchOutcome.Created, run, agent);

    public static DispatchResult ItemNotFound() => new(DispatchOutcome.ItemNotFound);

    public static DispatchResult PlaybookNotFound() => new(DispatchOutcome.PlaybookNotFound);

    public static DispatchResult Invalid(string field, string message) =>
        new(DispatchOutcome.Validation, Field: field, Message: message);

    public static DispatchResult ReadOnly() => new(DispatchOutcome.ReadOnly);

    public static DispatchResult Claimed(string? claimedBy, uint version) =>
        new(DispatchOutcome.ItemClaimed, ClaimedBy: claimedBy, Version: version);

    public static DispatchResult InProgress() => new(DispatchOutcome.RunInProgress);

    public static DispatchResult NotContinuable(string message) =>
        new(DispatchOutcome.NotContinuable, Message: message);
}

/// <summary>
/// Hands an item to an agent: the one dispatch path behind every door - the REST endpoint,
/// the MCP <c>start_run</c> tool and automation rules (<c>Events/RuleFiringHandler</c>).
/// The door decides
/// <em>who may ask</em> (organization and project role, a writable project, the factory
/// operator flag, the token's scope); this service decides everything about the run itself,
/// so a rule that one door forgot cannot exist. It claims the item through
/// <see cref="IWorkItemClaims"/> before inserting the run and relies on <c>ux_runs_item_live</c>
/// for the race the claim cannot see.
///
/// It also refuses on behalf of Billing: a read-only organization - an expired
/// evaluation or a lapsed payment - dispatches nothing, through any door. Rules check the
/// same state before firing; this is the choke point that does not depend on them.
/// </summary>
public sealed class RunDispatcher(
    AutomationDbContext db, ICurrentTenant tenant, IWorkItemLookup items, IProjectAccess access,
    IAgentIdentities agents, IWikiPageAccess pages, IWikiPageContent pageContent,
    IWorkItemClaims claims, IRealtimePublisher realtime, IOrganizationBillingState billing,
    TimeProvider clock)
{
    /// <param name="project">The item's project, already resolved and checked by the door.</param>
    /// <param name="itemKey">The item to hand over.</param>
    /// <param name="playbookId">A playbook of the project, or null for its default.</param>
    /// <param name="agentId">The agent to run as, or null for the project's default agent.</param>
    /// <param name="runnerId">
    /// The runner that must take the run, or null for whichever free runner claims it first.
    /// </param>
    /// <param name="actor">
    /// Who asked. For a user, the playbook page must be readable by them; a rule has no
    /// requester to check readability against - its author was checked when the rule was
    /// written, so only the page's existence and content matter here.
    /// </param>
    /// <param name="refine">
    /// Set for a refine run: the item is claimed where it stands rather than moved to Active
    /// and assigned, the prompt is <see cref="RefinePromptComposer"/>'s, and the run works in
    /// an isolated clone of the default branch, so it leaves no item branch behind for the
    /// implement run that may follow.
    /// </param>
    public async Task<DispatchResult> DispatchAsync(
        ProjectRef project, string itemKey, Guid? playbookId, string? agentId, Guid? runnerId,
        DispatchActor actor, CancellationToken ct, RefineContext? refine = null)
    {
        var organizationId = tenant.OrganizationId!.Value;

        if (await billing.IsReadOnlyAsync(organizationId, ct))
        {
            return DispatchResult.ReadOnly();
        }

        var item = (await items.FindByKeysAsync(project.Id, [itemKey], ct)).FirstOrDefault();
        if (item is null)
        {
            return DispatchResult.ItemNotFound();
        }

        var playbook = await FindPlaybookAsync(playbookId, project.Id, ct);
        if (playbook is null)
        {
            return playbookId is null
                ? DispatchResult.Invalid("playbookId", "This project has no default playbook. Set one under Factory settings.")
                : DispatchResult.PlaybookNotFound();
        }

        var settings = await db.ProjectSettings.AsNoTracking()
            .SingleOrDefaultAsync(row => row.ProjectId == project.Id, ct);
        var agentUserId = agentId ?? settings?.DefaultAgentId;
        if (agentUserId is null)
        {
            return DispatchResult.Invalid("agentId", "No agent was named and this project has no default agent.");
        }
        if (agentUserId.Length > Run.MaxActorLength)
        {
            return DispatchResult.Invalid("agentId", "Use 64 characters or fewer.");
        }

        var agent = await agents.FindAsync(agentUserId, ct);
        if (agent is not { IsActive: true }
            || !(await access.ListProjectMemberIdsAsync(project.Id, ct)).Contains(agentUserId, StringComparer.Ordinal))
        {
            return DispatchResult.Invalid("agentId", "The agent must be an active agent that can see this project.");
        }

        if (runnerId is { } requestedRunner
            && await RunnerErrorAsync(requestedRunner, playbook.Harness, ct) is { } runnerError)
        {
            return DispatchResult.Invalid("runnerId", runnerError);
        }

        if (playbook.WikiPageId is not { } pageId
            || (actor.Kind == DispatchActorKind.User && !await pages.CanReadAsync(pageId, project.Id, actor.UserId!, ct)))
        {
            return DispatchResult.PlaybookNotFound();
        }
        var content = await pageContent.GetMarkdownAsync(pageId, project.Id, ct);
        if (content is null)
        {
            return DispatchResult.PlaybookNotFound();
        }

        var claim = refine is null
            ? await claims.ClaimForAsync(item.Id, agentUserId, ct)
            : await claims.ClaimInPlaceAsync(item.Id, agentUserId, ct);
        switch (claim.Outcome)
        {
            case WorkItemClaimOutcome.NotFound:
                return DispatchResult.ItemNotFound();
            case WorkItemClaimOutcome.Conflict:
                return DispatchResult.Claimed(claim.ClaimedBy, claim.Version);
        }

        var onDefaultBranch = refine is not null || playbook.WorkOnDefaultBranch;
        var branch = onDefaultBranch
            ? settings?.DefaultBranch ?? "main"
            : BranchNames.For(item.Key, item.Title);
        var prompt = refine is null
            ? RunPromptComposer.Compose(
                agent.DisplayName ?? agentUserId, item.Key, project.Key, project.Name,
                branch, playbook.Name, content.Markdown, playbook.WorkOnDefaultBranch)
            : RefinePromptComposer.Compose(
                agent.DisplayName ?? agentUserId, item.Key, project.Key, project.Name,
                playbook.Name, content.Markdown, refine);

        var now = clock.GetUtcNow();
        var run = new Run
        {
            OrganizationId = organizationId,
            ProjectId = project.Id,
            ItemId = item.Id,
            ItemKey = item.Key,
            PlaybookId = playbook.Id,
            Kind = refine is null ? RunKind.Implement : RunKind.Refine,
            AgentUserId = agentUserId,
            RequestedRunnerId = runnerId,
            RequestedBy = actor.Kind == DispatchActorKind.User ? actor.UserId : null,
            RuleId = actor.Kind == DispatchActorKind.Rule ? actor.RuleId : null,
            Harness = playbook.Harness,
            PromptSnapshot = prompt,
            PlaybookRevisionId = content.RevisionId,
            BranchName = branch,
            WorkOnDefaultBranch = onDefaultBranch,
            MaxMinutes = Math.Clamp(playbook.MaxMinutes, 5, 720),
            QueuedAt = now,
        };
        db.Runs.Add(run);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Someone dispatched between our claim and this insert. The partial unique
            // index is the guarantee - the claim check above never could be.
            return DispatchResult.InProgress();
        }

        await realtime.PublishAsync(project.Id, "run.changed", new
        {
            runId = run.Id, itemId = item.Id, itemKey = item.Key, status = "queued", agentId = agentUserId,
            kind = refine is null ? "implement" : "refine",
        }, ct);
        AutomationMetrics.Started.Add(1);

        return DispatchResult.Created(run, agent);
    }

    /// <summary>
    /// Queues a run that resumes <paramref name="previous"/>'s harness session in the workspace
    /// its runner kept, pinned to that runner. Everything a dispatch checks still applies - a
    /// read-only organization, an inactive agent, a claimed item - and the continue run is a run
    /// like any other for limits and billing. It copies what the failed run was told (playbook,
    /// branch, prompt, time limit) rather than recomposing it: the session already holds the
    /// original instructions, and the runner sends only a short "continue" message.
    /// </summary>
    /// <param name="automatic">Queued by the sweeper after a transient failure, not by a person.</param>
    public async Task<DispatchResult> ContinueAsync(
        Run previous, DispatchActor actor, bool automatic, CancellationToken ct)
    {
        var organizationId = tenant.OrganizationId!.Value;
        if (await billing.IsReadOnlyAsync(organizationId, ct))
        {
            return DispatchResult.ReadOnly();
        }

        if (await ContinueRefusalAsync(previous, ct) is { } refusal)
        {
            return DispatchResult.NotContinuable(refusal);
        }

        var agent = await agents.FindAsync(previous.AgentUserId, ct);
        if (agent is not { IsActive: true }
            || !(await access.ListProjectMemberIdsAsync(previous.ProjectId, ct)).Contains(previous.AgentUserId, StringComparer.Ordinal))
        {
            return DispatchResult.Invalid("agentId", "The agent must be an active agent that can see this project.");
        }

        ItemRefinement? refinement = null;
        if (previous.Kind == RunKind.Refine)
        {
            refinement = await db.Refinements.SingleOrDefaultAsync(
                row => row.ItemId == previous.ItemId && row.LastRunId == previous.Id, ct);
            if (refinement is null)
            {
                return DispatchResult.NotContinuable("The item's refinement has moved on since this run.");
            }
        }

        var claim = previous.Kind == RunKind.Refine
            ? await claims.ClaimInPlaceAsync(previous.ItemId, previous.AgentUserId, ct)
            : await claims.ClaimForAsync(previous.ItemId, previous.AgentUserId, ct);
        switch (claim.Outcome)
        {
            case WorkItemClaimOutcome.NotFound:
                return DispatchResult.ItemNotFound();
            case WorkItemClaimOutcome.Conflict:
                return DispatchResult.Claimed(claim.ClaimedBy, claim.Version);
        }

        var now = clock.GetUtcNow();
        var run = new Run
        {
            OrganizationId = organizationId,
            ProjectId = previous.ProjectId,
            ItemId = previous.ItemId,
            ItemKey = previous.ItemKey,
            PlaybookId = previous.PlaybookId,
            Kind = previous.Kind,
            AgentUserId = previous.AgentUserId,
            // Only the runner that kept the workspace has the session.
            RequestedRunnerId = previous.RunnerId,
            RequestedBy = actor.Kind == DispatchActorKind.User ? actor.UserId : null,
            RuleId = actor.Kind == DispatchActorKind.Rule ? actor.RuleId : null,
            Harness = previous.Harness,
            PromptSnapshot = previous.PromptSnapshot,
            PlaybookRevisionId = previous.PlaybookRevisionId,
            BranchName = previous.BranchName,
            WorkOnDefaultBranch = previous.WorkOnDefaultBranch,
            MaxMinutes = previous.MaxMinutes,
            QueuedAt = now,
            ContinuesRunId = previous.Id,
            AutoContinued = automatic,
            AutoContinues = previous.AutoContinues + (automatic ? 1 : 0),
        };
        db.Runs.Add(run);
        if (refinement is not null)
        {
            refinement.Status = RefinementStatus.Refining;
            refinement.LastRunId = run.Id;
            refinement.UpdatedAt = now;
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } unique)
        {
            db.ChangeTracker.Clear();
            return unique.ConstraintName == "ux_runs_continues"
                ? DispatchResult.NotContinuable("This run has already been continued.")
                : DispatchResult.InProgress();
        }

        await realtime.PublishAsync(previous.ProjectId, "run.changed", new
        {
            runId = run.Id, itemId = run.ItemId, itemKey = run.ItemKey, status = "queued", agentId = run.AgentUserId,
            kind = run.Kind == RunKind.Refine ? "refine" : "implement", continuesRunId = previous.Id,
        }, ct);
        AutomationMetrics.Started.Add(1);

        return DispatchResult.Created(run, agent);
    }

    /// <summary>
    /// Why <paramref name="run"/> cannot be continued, or null when it can: it must have ended
    /// with a resumable session, and be the item's latest run - a newer run, a retry or an
    /// earlier continue, has moved the item and the kept workspace on.
    /// </summary>
    public async Task<string?> ContinueRefusalAsync(Run run, CancellationToken ct)
    {
        if (run.Status is not (RunStatus.Failed or RunStatus.TimedOut))
        {
            return "Only a failed or timed-out run can be continued.";
        }
        if (!run.HasResumableSession)
        {
            return "This run has no harness session to resume. Start a fresh run instead.";
        }
        if (await db.Runs.AnyAsync(other => other.ItemId == run.ItemId && other.Id != run.Id
                && (other.ContinuesRunId == run.Id || other.QueuedAt > run.QueuedAt), ct))
        {
            return "The item has a newer run; continue that one instead.";
        }
        return null;
    }

    /// <summary>
    /// A requested runner must be one this organization still has, enabled, and - once it has
    /// reported its capabilities - able to run the playbook's harness. Being offline is fine:
    /// the run waits for it, as a run waits today when no runner is online.
    /// </summary>
    private async Task<string?> RunnerErrorAsync(Guid runnerId, string harness, CancellationToken ct)
    {
        var runner = await db.Runners.AsNoTracking()
            .SingleOrDefaultAsync(r => r.Id == runnerId && r.DeletedAt == null, ct);
        if (runner is null)
        {
            return "Choose a runner of this organization.";
        }
        if (!runner.IsUsable)
        {
            return $"{runner.Name} is disabled. Choose another runner.";
        }
        if (runner.Capabilities is { } capabilities
            && !capabilities.Harnesses.Any(h => string.Equals(h.Name, harness, StringComparison.Ordinal)))
        {
            return $"{runner.Name} does not report {harness}, which this playbook uses.";
        }
        return null;
    }

    private async Task<Playbook?> FindPlaybookAsync(Guid? playbookId, Guid projectId, CancellationToken ct)
    {
        if (playbookId is { } id)
        {
            return await db.Playbooks.AsNoTracking()
                .SingleOrDefaultAsync(playbook => playbook.Id == id && playbook.ProjectId == projectId, ct);
        }

        return await db.Playbooks.AsNoTracking()
            .FirstOrDefaultAsync(playbook => playbook.ProjectId == projectId && playbook.IsDefault, ct);
    }
}

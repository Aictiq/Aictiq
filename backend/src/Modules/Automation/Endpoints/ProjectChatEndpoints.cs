using Aictiq.Modules.Automation.Domain;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Aictiq.Modules.Automation.Endpoints;

/// <param name="Messages">The caller's own chat in this project, oldest first, at most <see cref="ProjectChat.MaxPairs"/>.</param>
/// <param name="RunnerOnline">An enabled runner that offers the chat's harness is online now, so a queued question can start: the project's default runner, when questions go to it.</param>
/// <param name="RunnerName">The project's default runner, when questions go to it rather than to any free runner.</param>
public sealed record ProjectChatView(IReadOnlyList<ProjectChatMessageView> Messages, bool RunnerOnline, string? RunnerName = null);

/// <param name="Answer">The agent's answer as Markdown, once it has one.</param>
/// <param name="RunStatus">While the question is pending: where its run stands (queued, assigned or running).</param>
/// <param name="Error">Why there is no answer, for a failed, cancelled or timed-out question.</param>
/// <param name="Attachments">The files sent with the question, in the order they were attached.</param>
public sealed record ProjectChatMessageView(
    Guid Id, string Question, string? Answer, ProjectChatMessageStatus Status, RunStatus? RunStatus,
    string? Error, Guid? RunId, DateTimeOffset AskedAt, DateTimeOffset? AnsweredAt,
    IReadOnlyList<ProjectChatAttachmentView> Attachments);

public sealed record ProjectChatAttachmentView(Guid Id, string FileName, string ContentType, long SizeBytes);

/// <summary>A chat file as a project-chat run's runner lists it, shaped like an item attachment.</summary>
public sealed record ProjectChatRunAttachmentView(Guid Id, Guid ChatMessageId, string FileName, string ContentType, long SizeBytes);

/// <param name="AttachmentIds">The caller's own pending uploads (<c>?purpose=chat</c>) to send with the question.</param>
public sealed record AskProjectChatRequest(string? Question, IReadOnlyList<Guid>? AttachmentIds = null);

/// <summary>
/// A person's private chat with the project's default agent. Every route reads and writes the
/// caller's own messages only - keyed on (project, caller), never on an id someone could pass -
/// so no member, admin or token of anyone else can reach it. Stakeholders and guests may not
/// operate the factory, so they get no chat: each question is a runner run.
/// </summary>
public static class ProjectChatEndpoints
{
    public static IEndpointRouteBuilder MapProjectChatEndpoints(this IEndpointRouteBuilder api)
    {
        var chat = api.MapGroup("/orgs/{orgSlug}/projects/{projectKey}/chat")
            .WithTags("Project chat").RequireAuthorization();
        chat.MapGet("/", GetAsync).RequireProjectRole(ProjectRole.Member).RequireFactoryOperator().RequireScope(Scopes.Read);
        chat.MapPost("/", AskAsync).RequireProjectRole(ProjectRole.Member).RequireFactoryOperator()
            .RequireProjectWritable().RequireScope(Scopes.Write);
        chat.MapDelete("/", ClearAsync).RequireProjectRole(ProjectRole.Member).RequireFactoryOperator().RequireScope(Scopes.Write);
        // For the runner of a project-chat run, with that run's own token: the files it provisions.
        chat.MapGet("/attachments", AttachmentsAsync).RequireProjectRole(ProjectRole.Guest).RequireScope(Scopes.Read);
        return api;
    }

    private static async Task<IResult> GetAsync(
        HttpContext http, AutomationDbContext db, ICurrentUser user, IProjectChatAttachments attachments,
        IOptions<AutomationOptions> options, TimeProvider clock, CancellationToken ct)
    {
        var project = http.ResolvedProject()!;
        return Results.Ok(await ViewAsync(db, attachments, project.Id, user.UserId!, options.Value, clock.GetUtcNow(), ct));
    }

    /// <summary>
    /// The files of the chat a live project-chat run answers: that run's token reads the asker's
    /// files in the run's project and nothing else. Anyone else reads their own, which the
    /// chat view already shows them.
    /// </summary>
    private static async Task<IResult> AttachmentsAsync(
        HttpContext http, ICurrentUser user, IProjectChatGrants grants, IProjectChatAttachments attachments, CancellationToken ct)
    {
        var project = http.ResolvedProject()!;
        var owner = user.UserId!;
        if (user.Scopes.Contains(Scopes.ProjectChat, StringComparer.Ordinal))
        {
            var grant = user.TokenId is { } tokenId ? await grants.FindAsync(tokenId, ct) : null;
            if (grant is null || grant.ProjectId != project.Id) return Results.NotFound();
            owner = grant.RequestedBy;
        }
        var files = await attachments.ListAsync(project.Id, owner, ct);
        return Results.Ok(files.Select(file => new ProjectChatRunAttachmentView(
            file.Id, file.ChatMessageId, file.FileName, file.ContentType, file.SizeBytes)));
    }

    private static async Task<IResult> AskAsync(
        string orgSlug, AskProjectChatRequest request, HttpContext http, AutomationDbContext db, ICurrentUser user,
        RunDispatcher dispatcher, IUserDirectory directory, IProjectChatAttachments attachments,
        IOptions<AutomationOptions> options, TimeProvider clock, CancellationToken ct)
    {
        var question = request.Question?.Trim();
        if (string.IsNullOrEmpty(question) || question.Length > ProjectChat.MaxQuestionLength)
        {
            return Validation("question", "Ask a question of 1 to 4,000 characters.");
        }

        var project = http.ResolvedProject()!;
        var userId = user.UserId!;
        var requesterName = (await directory.GetAsync([userId], ct)).GetValueOrDefault(userId)?.DisplayName ?? user.UserName ?? "A project member";

        // The files are bound to the question's id before the question exists, so a file that
        // cannot go along refuses the question with nothing queued; a refused question hands
        // its files back as uploads the person may send again.
        var messageId = Guid.CreateVersion7();
        var (attached, failure) = await attachments.AttachAsync(project.Id, userId, messageId, request.AttachmentIds ?? [], ct);
        if (failure is { Plan: { } plan })
        {
            return Results.Problem(plan.Reason, title: "Plan limit reached.", type: ProblemTypes.PlanLimit,
                statusCode: StatusCodes.Status402PaymentRequired,
                extensions: new Dictionary<string, object?> { ["limit"] = plan.Limit, ["upgradeUrl"] = plan.UpgradeUrl });
        }
        if (failure is not null)
        {
            return Validation(failure.Field, failure.Message);
        }

        DispatchResult result;
        try
        {
            result = await dispatcher.DispatchProjectChatAsync(orgSlug, project, userId, requesterName, question, messageId, attached, ct);
        }
        catch when (attached.Count > 0)
        {
            await attachments.DetachAsync(messageId, CancellationToken.None);
            throw;
        }
        if (result.Outcome != DispatchOutcome.Created && attached.Count > 0)
        {
            await attachments.DetachAsync(messageId, CancellationToken.None);
        }
        switch (result.Outcome)
        {
            case DispatchOutcome.Validation:
                return Validation(result.Field!, result.Message!);
            case DispatchOutcome.ReadOnly:
                return Results.Problem(
                    title: "This organization is read-only.",
                    detail: "The evaluation has ended, a payment problem is unresolved or the free plan's people limit is exceeded, so new agent runs are paused. Reading and exporting still work; choosing a paid plan resumes dispatch.",
                    type: ProblemTypes.OrganizationReadOnly,
                    statusCode: StatusCodes.Status409Conflict);
            case DispatchOutcome.RunInProgress:
                return Results.Problem(
                    title: "A question is already in progress.",
                    detail: "Wait for the agent to answer it, or clear the chat to cancel it.",
                    type: ProblemTypes.Conflict,
                    statusCode: StatusCodes.Status409Conflict);
        }

        return Results.Created($"/api/v1/orgs/{orgSlug}/projects/{project.Key}/chat",
            await ViewAsync(db, attachments, project.Id, userId, options.Value, clock.GetUtcNow(), ct));
    }

    /// <summary>
    /// Empties the caller's chat and cancels the question in flight, if any: a queued run ends
    /// here, a running one is asked to stop and its answer lands nowhere.
    /// </summary>
    private static async Task<IResult> ClearAsync(
        HttpContext http, AutomationDbContext db, ICurrentUser user, IRealtimePublisher realtime,
        IProjectChatAttachments attachments, TimeProvider clock, CancellationToken ct)
    {
        var project = http.ResolvedProject()!;
        var userId = user.UserId!;
        var now = clock.GetUtcNow();

        // Messages first, so the cancelled run's completion finds nothing left to answer.
        await db.ProjectChatMessages.Where(row => row.ProjectId == project.Id && row.UserId == userId)
            .ExecuteDeleteAsync(ct);
        await attachments.DeleteAsync(project.Id, userId, chatMessageIds: null, ct);

        var live = await db.Runs.AsNoTracking()
            .Where(run => run.ProjectId == project.Id && run.RequestedBy == userId
                && run.Kind == RunKind.ProjectChat && run.Status < RunStatus.Succeeded)
            .ToListAsync(ct);
        foreach (var run in live)
        {
            // Compare-and-swap on the status the row has now, as the run cancel route does.
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var cancelledQueued = await db.Runs
                .Where(r => r.Id == run.Id && r.Status == RunStatus.Queued)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(r => r.Status, RunStatus.Cancelled)
                    .SetProperty(r => r.FinishedAt, now)
                    .SetProperty(r => r.CancelRequestedAt, now), ct);
            if (cancelledQueued == 1)
            {
                await RunCompletion.StageAsync(db, run, RunOutcomes.Cancelled,
                    summary: null, pullRequestUrl: null, failureReason: "cancelled before a runner took it", ct);
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                AutomationMetrics.Finished.Add(1, AutomationMetrics.OutcomeTag(RunOutcomes.Cancelled));
                await realtime.PublishAsync(project.Id, "run.changed", new
                {
                    runId = run.Id, itemId = (Guid?)null, itemKey = (string?)null, status = "cancelled",
                }, ct);
                continue;
            }

            var requested = await db.Runs
                .Where(r => r.Id == run.Id && (r.Status == RunStatus.Assigned || r.Status == RunStatus.Running))
                .ExecuteUpdateAsync(set => set.SetProperty(r => r.CancelRequestedAt, r => r.CancelRequestedAt ?? now), ct);
            await transaction.CommitAsync(ct);
            if (requested == 1)
            {
                await realtime.PublishAsync(project.Id, "run.changed", new
                {
                    runId = run.Id, itemId = (Guid?)null, itemKey = (string?)null, cancelRequested = true,
                }, ct);
            }
        }

        return Results.NoContent();
    }

    private static async Task<ProjectChatView> ViewAsync(
        AutomationDbContext db, IProjectChatAttachments attachments, Guid projectId, string userId, AutomationOptions options,
        DateTimeOffset now, CancellationToken ct)
    {
        var messages = await db.ProjectChatMessages.AsNoTracking()
            .Where(row => row.ProjectId == projectId && row.UserId == userId)
            .OrderByDescending(row => row.AskedAt)
            .Take(ProjectChat.MaxPairs)
            .ToListAsync(ct);
        messages.Reverse();
        var files = (await attachments.ListAsync(projectId, userId, ct)).ToLookup(file => file.ChatMessageId);

        var pendingRunIds = messages.Where(row => row.Status == ProjectChatMessageStatus.Pending && row.RunId is not null)
            .Select(row => row.RunId!.Value).ToList();
        var pendingRuns = pendingRunIds.Count == 0
            ? []
            : await db.Runs.AsNoTracking().Where(run => pendingRunIds.Contains(run.Id))
                .Select(run => new { run.Id, run.Status, run.Harness, run.RequestedRunnerId })
                .ToDictionaryAsync(run => run.Id, ct);

        // The harness a question runs on: the one in flight, else what the next one would borrow.
        var harness = pendingRuns.Values.Select(run => run.Harness).FirstOrDefault()
            ?? await db.Playbooks.AsNoTracking().Where(playbook => playbook.ProjectId == projectId && playbook.IsDefault)
                .Select(playbook => playbook.Harness).FirstOrDefaultAsync(ct)
            ?? ProjectChat.DefaultHarness;
        var runners = await db.Runners.AsNoTracking().Where(r => r.DeletedAt == null && r.DisabledAt == null).ToListAsync(ct);
        // The runner a question goes to: the one in flight was sent to, else the project's default.
        var pinnedId = pendingRuns.Count > 0
            ? pendingRuns.Values.Select(run => run.RequestedRunnerId).FirstOrDefault()
            : await db.ProjectSettings.AsNoTracking().Where(settings => settings.ProjectId == projectId)
                .Select(settings => settings.DefaultRunnerId).FirstOrDefaultAsync(ct);
        var pinned = runners.FirstOrDefault(r => r.Id == pinnedId);
        var runnerOnline = (pinned is null ? runners : [pinned]).Any(r => RunnerEndpoints.IsOnline(r, options, now)
            && (r.Capabilities is null || r.Capabilities.Harnesses.Any(h => h.Name == harness)));

        return new ProjectChatView(
            messages.Select(row => new ProjectChatMessageView(
                row.Id, row.Question, row.Answer, row.Status,
                row.Status == ProjectChatMessageStatus.Pending && row.RunId is { } runId && pendingRuns.TryGetValue(runId, out var run)
                    ? run.Status
                    : null,
                row.Error, row.RunId, row.AskedAt, row.AnsweredAt,
                files[row.Id].Select(file => new ProjectChatAttachmentView(file.Id, file.FileName, file.ContentType, file.SizeBytes)).ToList()))
                .ToList(),
            runnerOnline, pinned?.Name);
    }

    private static IResult Validation(string field, string error) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [error] }, type: ProblemTypes.Validation);
}

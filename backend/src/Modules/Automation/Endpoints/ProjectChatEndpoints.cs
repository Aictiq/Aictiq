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
/// <param name="RunnerOnline">An enabled runner that offers the chat's harness is online now, so a queued question can start.</param>
public sealed record ProjectChatView(IReadOnlyList<ProjectChatMessageView> Messages, bool RunnerOnline);

/// <param name="Answer">The agent's answer as Markdown, once it has one.</param>
/// <param name="RunStatus">While the question is pending: where its run stands (queued, assigned or running).</param>
/// <param name="Error">Why there is no answer, for a failed, cancelled or timed-out question.</param>
public sealed record ProjectChatMessageView(
    Guid Id, string Question, string? Answer, ProjectChatMessageStatus Status, RunStatus? RunStatus,
    string? Error, Guid? RunId, DateTimeOffset AskedAt, DateTimeOffset? AnsweredAt);

public sealed record AskProjectChatRequest(string? Question);

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
        return api;
    }

    private static async Task<IResult> GetAsync(
        HttpContext http, AutomationDbContext db, ICurrentUser user, IOptions<AutomationOptions> options,
        TimeProvider clock, CancellationToken ct)
    {
        var project = http.ResolvedProject()!;
        return Results.Ok(await ViewAsync(db, project.Id, user.UserId!, options.Value, clock.GetUtcNow(), ct));
    }

    private static async Task<IResult> AskAsync(
        string orgSlug, AskProjectChatRequest request, HttpContext http, AutomationDbContext db, ICurrentUser user,
        RunDispatcher dispatcher, IUserDirectory directory, IOptions<AutomationOptions> options, TimeProvider clock,
        CancellationToken ct)
    {
        var question = request.Question?.Trim();
        if (string.IsNullOrEmpty(question) || question.Length > ProjectChat.MaxQuestionLength)
        {
            return Validation("question", "Ask a question of 1 to 4,000 characters.");
        }

        var project = http.ResolvedProject()!;
        var userId = user.UserId!;
        var requesterName = (await directory.GetAsync([userId], ct)).GetValueOrDefault(userId)?.DisplayName ?? user.UserName ?? "A project member";
        var result = await dispatcher.DispatchProjectChatAsync(orgSlug, project, userId, requesterName, question, ct);
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
            await ViewAsync(db, project.Id, userId, options.Value, clock.GetUtcNow(), ct));
    }

    /// <summary>
    /// Empties the caller's chat and cancels the question in flight, if any: a queued run ends
    /// here, a running one is asked to stop and its answer lands nowhere.
    /// </summary>
    private static async Task<IResult> ClearAsync(
        HttpContext http, AutomationDbContext db, ICurrentUser user, IRealtimePublisher realtime,
        TimeProvider clock, CancellationToken ct)
    {
        var project = http.ResolvedProject()!;
        var userId = user.UserId!;
        var now = clock.GetUtcNow();

        // Messages first, so the cancelled run's completion finds nothing left to answer.
        await db.ProjectChatMessages.Where(row => row.ProjectId == project.Id && row.UserId == userId)
            .ExecuteDeleteAsync(ct);

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
        AutomationDbContext db, Guid projectId, string userId, AutomationOptions options, DateTimeOffset now,
        CancellationToken ct)
    {
        var messages = await db.ProjectChatMessages.AsNoTracking()
            .Where(row => row.ProjectId == projectId && row.UserId == userId)
            .OrderByDescending(row => row.AskedAt)
            .Take(ProjectChat.MaxPairs)
            .ToListAsync(ct);
        messages.Reverse();

        var pendingRunIds = messages.Where(row => row.Status == ProjectChatMessageStatus.Pending && row.RunId is not null)
            .Select(row => row.RunId!.Value).ToList();
        var pendingRuns = pendingRunIds.Count == 0
            ? []
            : await db.Runs.AsNoTracking().Where(run => pendingRunIds.Contains(run.Id))
                .Select(run => new { run.Id, run.Status, run.Harness })
                .ToDictionaryAsync(run => run.Id, ct);

        // The harness a question runs on: the one in flight, else what the next one would borrow.
        var harness = pendingRuns.Values.Select(run => run.Harness).FirstOrDefault()
            ?? await db.Playbooks.AsNoTracking().Where(playbook => playbook.ProjectId == projectId && playbook.IsDefault)
                .Select(playbook => playbook.Harness).FirstOrDefaultAsync(ct)
            ?? ProjectChat.DefaultHarness;
        var runners = await db.Runners.AsNoTracking().Where(r => r.DeletedAt == null && r.DisabledAt == null).ToListAsync(ct);
        var runnerOnline = runners.Any(r => RunnerEndpoints.IsOnline(r, options, now)
            && (r.Capabilities is null || r.Capabilities.Harnesses.Any(h => h.Name == harness)));

        return new ProjectChatView(
            messages.Select(row => new ProjectChatMessageView(
                row.Id, row.Question, row.Answer, row.Status,
                row.Status == ProjectChatMessageStatus.Pending && row.RunId is { } runId && pendingRuns.TryGetValue(runId, out var run)
                    ? run.Status
                    : null,
                row.Error, row.RunId, row.AskedAt, row.AnsweredAt)).ToList(),
            runnerOnline);
    }

    private static IResult Validation(string field, string error) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [error] }, type: ProblemTypes.Validation);
}

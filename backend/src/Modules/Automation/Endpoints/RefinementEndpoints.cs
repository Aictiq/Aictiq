using Aictiq.Modules.Automation.Domain;
using Aictiq.Modules.Automation.Prompts;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Aictiq.Modules.Automation.Endpoints;

/// <param name="Enabled">A playbook is chosen, so people may ask for a refinement.</param>
public sealed record RefinementSettingsView(
    Guid ProjectId, Guid? PlaybookId, string? AgentId, string ProductDescription,
    string WritingInstructions, string NamingConventions, string Platforms,
    Guid? RefinedStateId, bool Enabled, DateTimeOffset? UpdatedAt, uint Version);

public sealed record UpdateRefinementSettingsRequest(
    Guid? PlaybookId, string? AgentId, string? ProductDescription, string? WritingInstructions,
    string? NamingConventions, string? Platforms, Guid? RefinedStateId, uint Version);

public sealed record RefinementAnswer(string? Question, string? Answer);

/// <param name="Answers">Answers to the questions the last run asked.</param>
/// <param name="Feedback">What the person wants changed, when they ask again without being asked.</param>
/// <param name="RunnerId">The runner that must take the run; null for any free runner.</param>
/// <param name="Harness">The harness for this run in place of the refine playbook's; null for the playbook's.</param>
public sealed record RefineItemRequest(
    IReadOnlyList<RefinementAnswer>? Answers = null, string? Feedback = null, Guid? RunnerId = null,
    string? Harness = null);

public sealed record ConfirmRefinementRequest(uint Version);

public sealed record RefinementQuestionAnswer(string Question, string Answer);

/// <param name="RefinedStateId">Where the project sends a confirmed ticket; the client moves it there.</param>
public sealed record RefinementView(
    Guid Id, Guid ItemId, string ItemKey, RefinementStatus Status, IReadOnlyList<string> Questions,
    IReadOnlyList<RefinementQuestionAnswer> Answered, string? Summary, Guid? LastRunId,
    string RequestedBy, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    DateTimeOffset? ConfirmedAt, string? ConfirmedBy, Guid? RefinedStateId, uint Version);

/// <summary>
/// Refining a ticket: a person files a short description, a refine run rewrites it into a
/// complete ticket or asks what it cannot know, and the person confirms the result.
///
/// The run is an ordinary factory run, dispatched through <see cref="RunDispatcher"/>, so the
/// same doors apply: asking needs project Member on a writable project and the factory
/// operator flag. Refinement details, confirmation and project refinement settings also
/// belong to factory operators.
/// </summary>
public static class RefinementEndpoints
{
    public const string StarterName = "Refine";

    private const string StarterMarkdown = """
        # Refine

        Turn the short description on this item into a complete, implementation-ready ticket.

        ## Read before you write

        - Read the item, its comments and every attachment. Screenshots often answer what the text leaves out: which screen, which platform, what looks wrong.
        - Use the project's item template for this type when it has one.
        - Check the code only where the ticket's wording depends on it - what the thing is called today, whether the behaviour already exists, what the change could break. Search for that one answer and move on; a refined ticket is worth a few minutes, and the implement run does the real reading.

        ## What to write

        Keep the title short and follow the project's naming conventions. Structure the description by item type:

        - **Story / Feature**: User story ("As a ..., I want ..., so that ..."), Context, Requirements, Acceptance criteria (testable, as a checklist), and Technical or design notes when the input supports them.
        - **Bug**: Summary, Steps to reproduce, Expected result, Actual result, Platform / device / version, Acceptance criteria, and Notes on the likely cause when the code shows it.
        - **Epic**: Goal, Scope, Out of scope, the stories it likely breaks into, and Success criteria.
        - **Task**: What to do, Done when, and Notes.

        ## When to ask

        Ask only what blocks a correct ticket and that neither the description, the attachments nor the code answers - for example the platform when a bug depends on it. Do not invent requirements to avoid asking.
        """;

    public static IEndpointRouteBuilder MapRefinementEndpoints(this IEndpointRouteBuilder api)
    {
        var settings = api.MapGroup("/orgs/{orgSlug}/projects/{projectKey}/refinement-settings")
            .WithTags("Ticket refinement").RequireAuthorization();
        settings.MapGet("/", GetSettingsAsync).RequireProjectRole(ProjectRole.Member).RequireFactoryOperator().RequireScope(Scopes.Read);
        settings.MapPut("/", PutSettingsAsync).RequireProjectRole(ProjectRole.Admin).RequireFactoryOperator().RequireProjectWritable().RequireScope(Scopes.Write);
        settings.MapPost("/starter-playbook", StarterAsync).RequireProjectRole(ProjectRole.Admin).RequireFactoryOperator().RequireProjectWritable().RequireScope(Scopes.Write);

        var item = api.MapGroup("/orgs/{orgSlug}/items/{itemKey}/refinement")
            .WithTags("Ticket refinement").RequireAuthorization();
        item.MapGet("/", GetAsync)
            .RequireOrgRole(OrgRole.Guest).RequireRunItemProject(ProjectRole.Guest)
            .RequireFactoryOperator().RequireScope(Scopes.Read);
        item.MapPost("/", RefineAsync)
            .RequireOrgRole(OrgRole.Member).RequireRunItemProject(ProjectRole.Member, writable: true)
            .RequireFactoryOperator().RequireScope(Scopes.Write);
        item.MapPost("/confirm", ConfirmAsync)
            .RequireOrgRole(OrgRole.Member).RequireRunItemProject(ProjectRole.Member, writable: true)
            .RequireFactoryOperator().RequireScope(Scopes.Write);
        return api;
    }

    // ── settings ─────────────────────────────────────────────────────────────────────

    private static async Task<IResult> GetSettingsAsync(HttpContext http, AutomationDbContext db, CancellationToken ct)
    {
        var projectId = http.ResolvedProjectId()!.Value;
        var row = await db.RefinementSettings.AsNoTracking().SingleOrDefaultAsync(row => row.ProjectId == projectId, ct);
        return Results.Ok(row is null
            ? new RefinementSettingsView(projectId, null, null, "", "", "", "", null, false, null, 0)
            : ToView(row));
    }

    private static async Task<IResult> PutSettingsAsync(
        UpdateRefinementSettingsRequest request, HttpContext http, AutomationDbContext db,
        ICurrentTenant tenant, IProjectAccess access, IAgentIdentities agents,
        IProjectWorkflowAccess workflows, TimeProvider clock, CancellationToken ct)
    {
        var projectId = http.ResolvedProjectId()!.Value;
        var errors = new Dictionary<string, string[]>();
        if (request.PlaybookId is { } playbookId
            && !await db.Playbooks.AnyAsync(row => row.Id == playbookId && row.ProjectId == projectId, ct))
            errors["playbookId"] = ["Choose a playbook of this project."];
        if (request.AgentId is { } agentId
            && !await PlaybookEndpoints.IsAssignableAgentAsync(projectId, agentId, access, agents, ct))
            errors["agentId"] = ["The agent must be an active agent that can see this project."];
        if (request.RefinedStateId is { } stateId
            && !await workflows.StatesBelongToProjectAsync(projectId, [stateId], ct))
            errors["refinedStateId"] = ["State must belong to a workflow in this project."];
        foreach (var (field, value) in new[]
        {
            ("productDescription", request.ProductDescription), ("writingInstructions", request.WritingInstructions),
            ("namingConventions", request.NamingConventions), ("platforms", request.Platforms),
        })
        {
            if (value?.Length > ProjectRefinementSettings.MaxTextLength)
                errors[field] = [$"Use {ProjectRefinementSettings.MaxTextLength} characters or fewer."];
        }
        if (errors.Count > 0) return Validation(errors);

        var row = await db.RefinementSettings.SingleOrDefaultAsync(row => row.ProjectId == projectId, ct);
        if (row is null)
        {
            if (request.Version != 0) return Conflict();
            row = new ProjectRefinementSettings { OrganizationId = tenant.OrganizationId!.Value, ProjectId = projectId };
            db.RefinementSettings.Add(row);
        }
        else
        {
            if (request.Version != row.Version) return Conflict();
            db.Entry(row).Property(settings => settings.Version).OriginalValue = request.Version;
        }

        row.PlaybookId = request.PlaybookId;
        row.AgentId = request.AgentId;
        row.ProductDescription = request.ProductDescription?.Trim() ?? "";
        row.WritingInstructions = request.WritingInstructions?.Trim() ?? "";
        row.NamingConventions = request.NamingConventions?.Trim() ?? "";
        row.Platforms = request.Platforms?.Trim() ?? "";
        row.RefinedStateId = request.RefinedStateId;
        row.UpdatedAt = clock.GetUtcNow();
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return Conflict(); }
        return Results.Ok(ToView(row));
    }

    /// <summary>
    /// A "Refine" playbook in the wiki's Factory section, chosen for refinement in one step.
    /// It is never the project default: a refine playbook handed to an implement run would
    /// write tickets instead of code.
    /// </summary>
    private static async Task<IResult> StarterAsync(
        HttpContext http, AutomationDbContext db, ICurrentTenant tenant, ICurrentUser user,
        IWikiPageCreator pages, IWikiPageAccess access, TimeProvider clock, CancellationToken ct)
    {
        var projectId = http.ResolvedProjectId()!.Value;
        if (await db.Playbooks.AnyAsync(
                playbook => playbook.ProjectId == projectId && playbook.Name.ToLower() == StarterName.ToLower(), ct))
            return Conflict("A playbook named Refine already exists. Choose it instead.");

        var pageId = await pages.CreateFactoryPageAsync(
            tenant.OrganizationId!.Value, projectId, user.UserId!, StarterName, StarterMarkdown, ct);
        if (pageId is null) return Conflict("The wiki is not available to hold the instructions.");

        var now = clock.GetUtcNow();
        var playbook = new Playbook
        {
            OrganizationId = tenant.OrganizationId.Value,
            ProjectId = projectId,
            Name = StarterName,
            WikiPageId = pageId,
            Harness = Harnesses.Claude,
            MaxMinutes = 30,
            CreatedBy = user.UserId!,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Playbooks.Add(playbook);
        var row = await db.RefinementSettings.SingleOrDefaultAsync(row => row.ProjectId == projectId, ct);
        if (row is null)
        {
            row = new ProjectRefinementSettings { OrganizationId = tenant.OrganizationId.Value, ProjectId = projectId };
            db.RefinementSettings.Add(row);
        }
        row.PlaybookId = playbook.Id;
        row.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        await access.InvalidateAsync(projectId, ct);
        return Results.Ok(ToView(row));
    }

    // ── one item ─────────────────────────────────────────────────────────────────────

    private static async Task<IResult> GetAsync(
        string itemKey, HttpContext http, AutomationDbContext db, IWorkItemLookup items, CancellationToken ct)
    {
        var project = http.ResolvedProject()!;
        var item = (await items.FindByKeysAsync(project.Id, [itemKey.Trim().ToUpperInvariant()], ct)).FirstOrDefault();
        if (item is null) return RunEndpoints.NotFound();
        var row = await db.Refinements.AsNoTracking().SingleOrDefaultAsync(row => row.ItemId == item.Id, ct);
        // No refinement is an answer, not a missing resource: most items were never refined.
        return row is null ? Results.NoContent() : Results.Ok(await ToViewAsync(db, row, ct));
    }

    private static async Task<IResult> RefineAsync(
        string itemKey, RefineItemRequest request, HttpContext http, AutomationDbContext db,
        ICurrentUser user, IWorkItemLookup items, RunDispatcher dispatcher, IRealtimePublisher realtime,
        TimeProvider clock, CancellationToken ct)
    {
        var project = http.ResolvedProject()!;
        var key = itemKey.Trim().ToUpperInvariant();
        var item = (await items.FindByKeysAsync(project.Id, [key], ct)).FirstOrDefault();
        if (item is null) return RunEndpoints.NotFound();

        var settings = await db.RefinementSettings.AsNoTracking().SingleOrDefaultAsync(row => row.ProjectId == project.Id, ct);
        if (settings?.PlaybookId is not { } playbookId)
            return Validation(new() { ["playbookId"] = ["Ticket refinement is not set up for this project. Choose a refine playbook under Project settings → Factory."] });

        var answers = new List<(string Question, string Answer)>();
        var errors = new Dictionary<string, string[]>();
        foreach (var answer in request.Answers ?? [])
        {
            var question = answer.Question?.Trim();
            var text = answer.Answer?.Trim();
            if (string.IsNullOrEmpty(question) || question.Length > ItemRefinement.MaxQuestionLength)
                errors["answers"] = [$"Each answer needs its question, of at most {ItemRefinement.MaxQuestionLength} characters."];
            else if (string.IsNullOrEmpty(text))
                continue; // A question left blank is one the person chose not to answer.
            else if (text.Length > ItemRefinement.MaxAnswerLength)
                errors["answers"] = [$"Keep each answer to {ItemRefinement.MaxAnswerLength} characters or fewer."];
            else
                answers.Add((question, text));
        }
        if (answers.Count > ItemRefinement.MaxQuestions)
            errors["answers"] = [$"Answer at most {ItemRefinement.MaxQuestions} questions at a time."];
        var feedback = request.Feedback?.Trim();
        if (feedback?.Length > ItemRefinement.MaxAnswerLength)
            errors["feedback"] = [$"Keep the note to {ItemRefinement.MaxAnswerLength} characters or fewer."];
        if (!string.IsNullOrEmpty(feedback))
            answers.Add(("What should the refined ticket change?", feedback));
        var harness = string.IsNullOrWhiteSpace(request.Harness) ? null : request.Harness.Trim();
        if (harness is not null && !Harnesses.IsSupported(harness))
            errors["harness"] = ["Choose claude, codex, opencode, cursor or copilot."];
        if (errors.Count > 0) return Validation(errors);

        var row = await db.Refinements.SingleOrDefaultAsync(row => row.ItemId == item.Id, ct);
        if (row is { Status: RefinementStatus.Refining })
            return Results.Problem(
                title: "This item is already being refined.",
                detail: "Wait for the running refinement to answer, or cancel its run.",
                type: ProblemTypes.ItemClaimed, statusCode: StatusCodes.Status409Conflict);

        List<(string Question, string Answer)> answered =
            [.. (row is null ? [] : row.AnsweredQuestions.Zip(row.Answers, (q, a) => (q, a))), .. answers];
        var context = new RefineContext(settings.ProductDescription, settings.WritingInstructions,
            settings.NamingConventions, settings.Platforms, answered);

        // The run and the refinement that waits on it commit together: a refinement that
        // says "Refining" with no run behind it would never be settled.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var result = await dispatcher.DispatchAsync(
            project, key, playbookId, settings.AgentId, request.RunnerId, DispatchActor.User(user.UserId!), ct, context,
            harness: harness);
        switch (result.Outcome)
        {
            case DispatchOutcome.ItemNotFound:
            case DispatchOutcome.PlaybookNotFound:
                return RunEndpoints.NotFound();
            case DispatchOutcome.Validation:
                return Validation(new() { [result.Field!] = [result.Message!] });
            case DispatchOutcome.ReadOnly:
                return Results.Problem(
                    title: "This organization is read-only.",
                    detail: "The evaluation has ended, a payment problem is unresolved or the free plan's people limit is exceeded, so new agent runs are paused.",
                    type: ProblemTypes.OrganizationReadOnly, statusCode: StatusCodes.Status409Conflict);
            case DispatchOutcome.ItemClaimed:
            case DispatchOutcome.RunInProgress:
                return Results.Problem(
                    title: "This item already has a live claim or run.",
                    detail: "Refine it once the agent working on it lets go.",
                    type: ProblemTypes.ItemClaimed, statusCode: StatusCodes.Status409Conflict);
        }

        var now = clock.GetUtcNow();
        if (row is null)
        {
            row = new ItemRefinement
            {
                OrganizationId = result.Run!.OrganizationId,
                ProjectId = project.Id,
                ItemId = item.Id,
                ItemKey = item.Key,
                RequestedBy = user.UserId!,
                CreatedAt = now,
            };
            db.Refinements.Add(row);
        }
        row.Status = RefinementStatus.Refining;
        row.Questions = [];
        row.AnsweredQuestions = [.. answered.Select(pair => pair.Question)];
        row.Answers = [.. answered.Select(pair => pair.Answer)];
        row.Summary = null;
        row.LastRunId = result.Run!.Id;
        row.RequestedBy = user.UserId!;
        row.ConfirmedAt = null;
        row.ConfirmedBy = null;
        row.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        await PublishAsync(realtime, row, ct);
        return Results.Ok(await ToViewAsync(db, row, ct));
    }

    /// <summary>
    /// The person accepts the ticket. Moving it to the project's refined state is the client's
    /// next call, through the ordinary transition endpoint, so the workflow's rules and the
    /// item's history treat it as the person's own move.
    /// </summary>
    private static async Task<IResult> ConfirmAsync(
        string itemKey, ConfirmRefinementRequest request, HttpContext http, AutomationDbContext db,
        ICurrentUser user, IWorkItemLookup items, IRealtimePublisher realtime, TimeProvider clock, CancellationToken ct)
    {
        var project = http.ResolvedProject()!;
        var item = (await items.FindByKeysAsync(project.Id, [itemKey.Trim().ToUpperInvariant()], ct)).FirstOrDefault();
        if (item is null) return RunEndpoints.NotFound();
        var row = await db.Refinements.SingleOrDefaultAsync(row => row.ItemId == item.Id, ct);
        if (row is null) return RunEndpoints.NotFound();
        if (row.Version != request.Version) return Conflict();
        if (row.Status == RefinementStatus.Refining)
            return Results.Problem(
                title: "The ticket is still being refined.",
                detail: "Wait for the agent to finish, then review and confirm it.",
                type: ProblemTypes.Conflict, statusCode: StatusCodes.Status409Conflict);

        if (row.Status != RefinementStatus.Confirmed)
        {
            var now = clock.GetUtcNow();
            row.Status = RefinementStatus.Confirmed;
            row.Questions = [];
            row.ConfirmedAt = now;
            row.ConfirmedBy = user.UserId;
            row.UpdatedAt = now;
            db.Entry(row).Property(refinement => refinement.Version).OriginalValue = request.Version;
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateConcurrencyException) { return Conflict(); }
            await PublishAsync(realtime, row, ct);
        }
        return Results.Ok(await ToViewAsync(db, row, ct));
    }

    internal static Task PublishAsync(IRealtimePublisher realtime, ItemRefinement row, CancellationToken ct) =>
        realtime.PublishAsync(row.ProjectId, "refinement.changed", new
        {
            itemId = row.ItemId, itemKey = row.ItemKey, status = row.Status.ToString(),
        }, ct);

    private static async Task<RefinementView> ToViewAsync(AutomationDbContext db, ItemRefinement row, CancellationToken ct)
    {
        var refinedStateId = await db.RefinementSettings.AsNoTracking()
            .Where(settings => settings.ProjectId == row.ProjectId)
            .Select(settings => settings.RefinedStateId).SingleOrDefaultAsync(ct);
        return new RefinementView(
            row.Id, row.ItemId, row.ItemKey, row.Status, row.Questions,
            [.. row.AnsweredQuestions.Zip(row.Answers, (q, a) => new RefinementQuestionAnswer(q, a))],
            row.Summary, row.LastRunId, row.RequestedBy, row.CreatedAt, row.UpdatedAt,
            row.ConfirmedAt, row.ConfirmedBy, refinedStateId, row.Version);
    }

    private static RefinementSettingsView ToView(ProjectRefinementSettings row) => new(
        row.ProjectId, row.PlaybookId, row.AgentId, row.ProductDescription, row.WritingInstructions,
        row.NamingConventions, row.Platforms, row.RefinedStateId, row.PlaybookId is not null,
        row.UpdatedAt, row.Version);

    private static IResult Validation(Dictionary<string, string[]> errors) =>
        Results.ValidationProblem(errors, type: ProblemTypes.Validation);

    private static IResult Conflict(string? detail = null) => Results.Problem(
        title: "Conflict.", detail: detail ?? "The record changed - refresh and try again.",
        type: ProblemTypes.Conflict, statusCode: StatusCodes.Status409Conflict);
}

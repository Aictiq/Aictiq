using Aictiq.Modules.Automation.Domain;
using System.Text.Json.Serialization;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Aictiq.Modules.Automation.Endpoints;

public sealed record PlaybookView(
    Guid Id, Guid ProjectId, string Name, Guid? WikiPageId, string Harness,
    Guid? OnSuccessStateId, Guid? OnFailureStateId, int MaxMinutes, bool IsDefault,
    string CreatedBy, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, uint Version, bool WorkOnDefaultBranch = false,
    IReadOnlyList<Guid>? StagePlaybookIds = null, IReadOnlyList<string>? RequiredTags = null,
    IReadOnlyList<RepositoryStep>? RepositorySteps = null);

/// <summary>A playbook's follow-up playbooks, in the order they run; empty removes them all.</summary>
public sealed record PutPlaybookStagesRequest(IReadOnlyList<Guid>? StagePlaybookIds);

/// <param name="InstructionsMarkdown">
/// What agents should follow. Aictiq files it as a page in the wiki's Factory section, named
/// after the playbook. Give this or <paramref name="WikiPageId"/>, a page already in that section.
/// </param>
/// <param name="RequiredTags">Fleet mode: tags every run of the playbook requires of its runner. Kept, but ignored, while fleet mode is off.</param>
public sealed record CreatePlaybookRequest(
    string? Name, Guid? WikiPageId, string? Harness,
    Guid? OnSuccessStateId, Guid? OnFailureStateId, int MaxMinutes = 60, string? InstructionsMarkdown = null, bool WorkOnDefaultBranch = false,
    IReadOnlyList<string>? RequiredTags = null, IReadOnlyList<RepositoryStep>? RepositorySteps = null);

/// <param name="InFactorySection">
/// False for a playbook from before its page had to live in the Factory section: saving new
/// instructions files them there.
/// </param>
public sealed record PlaybookInstructionsView(Guid? WikiPageId, string? PageTitle, string Markdown, bool InFactorySection);

public sealed class UpdatePlaybookRequest
{
    private Guid? _onSuccessStateId;
    private Guid? _onFailureStateId;

    public string? Name { get; init; }
    public Guid? WikiPageId { get; init; }
    public string? Harness { get; init; }
    public Guid? OnSuccessStateId
    {
        get => _onSuccessStateId;
        init { _onSuccessStateId = value; HasOnSuccessStateId = true; }
    }
    public Guid? OnFailureStateId
    {
        get => _onFailureStateId;
        init { _onFailureStateId = value; HasOnFailureStateId = true; }
    }
    public int? MaxMinutes { get; init; }
    public bool? WorkOnDefaultBranch { get; init; }
    /// <summary>Fleet mode: the playbook's required tags, replacing the old ones. Null leaves them.</summary>
    public IReadOnlyList<string>? RequiredTags { get; init; }
    /// <summary>Multiple repositories: the playbook's repository steps, replacing the old ones. Null leaves them; empty clears them.</summary>
    public IReadOnlyList<RepositoryStep>? RepositorySteps { get; init; }
    /// <summary>New instructions: a new revision of the playbook's Factory page, or a new page there when it has none.</summary>
    public string? InstructionsMarkdown { get; init; }
    public uint Version { get; init; }

    [JsonIgnore] public bool HasOnSuccessStateId { get; private set; }
    [JsonIgnore] public bool HasOnFailureStateId { get; private set; }
}

/// <param name="MultiRepoAvailability"><c>hidden</c> (self-hosted, no billing), <c>upgrade</c> (not a paid hosted plan) or <c>available</c>.</param>
/// <param name="PrimaryAlias">The primary repository's alias: the one stored, or one derived from its name.</param>
/// <param name="AdditionalRepositories">Repositories beyond the primary one, which stays in the fields above.</param>
public sealed record FactorySettingsView(
    Guid ProjectId, short RepoSource, string? RepoFullName,
    string DefaultBranch, string? LocalPathHint, string? DefaultAgentId,
    DateTimeOffset? UpdatedAt, uint Version, Guid? DefaultRunnerId = null,
    bool MultiRepoEnabled = false, string MultiRepoAvailability = "hidden",
    string PrimaryAlias = "main", string? PrimaryWorkingDirectory = null,
    IReadOnlyList<ProjectRepositoryView>? AdditionalRepositories = null);

/// <param name="RepoSource">0 a GitHub binding, 1 a repository on the runner's machine.</param>
public sealed record ProjectRepositoryView(
    string Alias, short RepoSource, string? RepoFullName, string? LocalPathHint, string? DefaultBranch, string? WorkingDirectory);

/// <param name="MultiRepoEnabled">The multiple-repositories switch; null leaves it.</param>
/// <param name="AdditionalRepositories">Replaces the repositories beyond the primary one; null leaves them.</param>
public sealed record UpdateFactorySettingsRequest(
    short RepoSource, string? RepoFullName, string? DefaultBranch,
    string? LocalPathHint, string? DefaultAgentId, uint Version, Guid? DefaultRunnerId = null,
    bool? MultiRepoEnabled = null, string? PrimaryAlias = null, string? PrimaryWorkingDirectory = null,
    IReadOnlyList<ProjectRepositoryView>? AdditionalRepositories = null);

public static class PlaybookEndpoints
{
    private const string StarterName = "Implement";
    /// <summary>The wiki's page limit, which the instructions become.</summary>
    private const int MaxInstructionsLength = 1_048_576;

    private const string StarterMarkdown = """
        # Implement

        Follow this loop for the work item assigned to you:

        1. Start with `whoami`, find the ready work, and claim the item before you write code.
        2. Read the whole item context, including its parent chain, comments, links, and relevant wiki pages, before you plan.
        3. Use the branch and delivery mode specified for this run, and begin commit subjects with the item key so the work links itself.
        4. Keep the claim alive with heartbeats and report progress by editing one `<!-- aictiq:progress -->` comment.
        5. When coding is done, run the project's tests and verify before delivering the work.
        6. For a pull-request run, open and link the pull request; for a direct run, push to the specified branch. Do not transition the item yourself; Aictiq applies this playbook's success or failure state after the run ends.
        7. For a UI change, post one comment with screenshots from passing e2e tests, beside the item's design images when it has them.
        8. If you stop before finishing, release the item cleanly.

        Treat a claim conflict as a signal to choose different work. After an edit conflict, re-read and reconcile once; never overwrite another person's change blindly.
        """;

    public static IEndpointRouteBuilder MapPlaybookEndpoints(this IEndpointRouteBuilder api)
    {
        var playbooks = api.MapGroup("/orgs/{orgSlug}/projects/{projectKey}/playbooks")
            .WithTags("Factory playbooks").RequireAuthorization();
        playbooks.MapGet("/", ListAsync).RequireProjectRole(ProjectRole.Member).RequireScope(Scopes.Read);
        playbooks.MapPost("/", CreateAsync).RequireProjectRole(ProjectRole.Admin).RequireProjectWritable().RequireScope(Scopes.Write);
        playbooks.MapPost("/starter", StarterAsync).RequireProjectRole(ProjectRole.Admin).RequireProjectWritable().RequireScope(Scopes.Write);
        playbooks.MapGet("/{playbookId:guid}", GetAsync).RequireProjectRole(ProjectRole.Member).RequireScope(Scopes.Read);
        playbooks.MapGet("/{playbookId:guid}/instructions", InstructionsAsync).RequireProjectRole(ProjectRole.Admin).RequireScope(Scopes.Read);
        playbooks.MapPatch("/{playbookId:guid}", UpdateAsync).RequireProjectRole(ProjectRole.Admin).RequireProjectWritable().RequireScope(Scopes.Write);
        playbooks.MapDelete("/{playbookId:guid}", DeleteAsync).RequireProjectRole(ProjectRole.Admin).RequireProjectWritable().RequireScope(Scopes.Write);
        playbooks.MapPut("/{playbookId:guid}/stages", PutStagesAsync).RequireProjectRole(ProjectRole.Admin).RequireProjectWritable().RequireScope(Scopes.Write);
        playbooks.MapPut("/{playbookId:guid}/default", PromoteAsync).RequireProjectRole(ProjectRole.Admin).RequireProjectWritable().RequireScope(Scopes.Write);

        var settings = api.MapGroup("/orgs/{orgSlug}/projects/{projectKey}/factory-settings")
            .WithTags("Factory settings").RequireAuthorization();
        settings.MapGet("/", GetSettingsAsync).RequireProjectRole(ProjectRole.Member).RequireScope(Scopes.Read);
        settings.MapPut("/", PutSettingsAsync).RequireProjectRole(ProjectRole.Admin).RequireProjectWritable().RequireScope(Scopes.Write);
        return api;
    }

    private static async Task<IResult> ListAsync(HttpContext http, AutomationDbContext db, CancellationToken ct)
    {
        var projectId = http.ResolvedProjectId()!.Value;
        var rows = await db.Playbooks.AsNoTracking().Where(row => row.ProjectId == projectId)
            .OrderByDescending(row => row.IsDefault).ThenBy(row => row.Name).ToListAsync(ct);
        var ids = rows.Select(row => row.Id).ToList();
        var stages = (await db.PlaybookStages.AsNoTracking().Where(stage => ids.Contains(stage.PlaybookId))
                .OrderBy(stage => stage.Position).ToListAsync(ct))
            .ToLookup(stage => stage.PlaybookId, stage => stage.StagePlaybookId);
        return Results.Ok(rows.Select(row => ToView(row, [.. stages[row.Id]])).ToList());
    }

    private static async Task<IResult> GetAsync(
        Guid playbookId, HttpContext http, AutomationDbContext db, CancellationToken ct)
    {
        var projectId = http.ResolvedProjectId()!.Value;
        var row = await db.Playbooks.AsNoTracking()
            .SingleOrDefaultAsync(playbook => playbook.Id == playbookId && playbook.ProjectId == projectId, ct);
        return row is null ? NotFound() : Results.Ok(ToView(row, await StageIdsAsync(db, row.Id, ct)));
    }

    private static async Task<IResult> InstructionsAsync(
        Guid playbookId, HttpContext http, AutomationDbContext db, IWikiPageContent content,
        IWikiPageCreator factoryPages, CancellationToken ct)
    {
        var projectId = http.ResolvedProjectId()!.Value;
        var row = await db.Playbooks.AsNoTracking()
            .SingleOrDefaultAsync(playbook => playbook.Id == playbookId && playbook.ProjectId == projectId, ct);
        if (row is null) return NotFound();
        var page = row.WikiPageId is { } pageId ? await content.GetMarkdownAsync(pageId, projectId, ct) : null;
        return Results.Ok(new PlaybookInstructionsView(page is null ? null : row.WikiPageId, page?.Title, page?.Markdown ?? "",
            page is not null && await factoryPages.IsInFactorySectionAsync(row.WikiPageId!.Value, projectId, ct)));
    }

    private static async Task<IResult> CreateAsync(
        CreatePlaybookRequest request, HttpContext http, AutomationDbContext db,
        ICurrentTenant tenant, ICurrentUser user, IWikiPageAccess pages, IWikiPageCreator factoryPages,
        IProjectWorkflowAccess workflows, TimeProvider clock, CancellationToken ct)
    {
        var projectId = http.ResolvedProjectId()!.Value;
        if (request.InstructionsMarkdown is null && request.WikiPageId is { } requestedPage
            && !await pages.CanReadAsync(requestedPage, projectId, user.UserId!, ct))
            return NotFound();
        var errors = await ValidateAsync(request.Name, request.Harness,
            request.OnSuccessStateId, request.OnFailureStateId, request.MaxMinutes,
            projectId, workflows, ct);
        await ValidateInstructionsAsync(errors, request.InstructionsMarkdown,
            request.InstructionsMarkdown is null ? request.WikiPageId : null, required: true, projectId, factoryPages, ct);
        var requiredTags = FleetTags.Normalize(request.RequiredTags);
        if (requiredTags is null) errors["requiredTags"] = [FleetTags.Rule];
        var repositorySteps = RepositoryPlans.Normalize(request.RepositorySteps);
        if (RepositoryPlans.Validate(repositorySteps) is { } stepsError) errors["repositorySteps"] = [stepsError];
        if (errors.Count > 0) return Validation(errors);
        // ux_playbooks_project_name would refuse the row anyway, but only after its page was written.
        if (await NameTakenAsync(db, projectId, request.Name!, null, ct))
            return Conflict("A playbook with this name already exists in this project.");

        var pageId = request.InstructionsMarkdown is { } markdown
            ? await factoryPages.CreateFactoryPageAsync(tenant.OrganizationId!.Value, projectId, user.UserId!, request.Name!.Trim(), markdown, ct)
            : request.WikiPageId;
        if (pageId is null) return Conflict("The wiki is not available to hold the instructions.");

        var now = clock.GetUtcNow();
        var row = new Playbook
        {
            OrganizationId = tenant.OrganizationId!.Value,
            ProjectId = projectId,
            Name = request.Name!.Trim(),
            WikiPageId = pageId,
            Harness = request.Harness!,
            OnSuccessStateId = request.OnSuccessStateId,
            OnFailureStateId = request.OnFailureStateId,
            MaxMinutes = request.MaxMinutes,
            WorkOnDefaultBranch = request.WorkOnDefaultBranch,
            RequiredTags = requiredTags!,
            RepositorySteps = repositorySteps,
            CreatedBy = user.UserId!,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Playbooks.Add(row);
        await db.SaveChangesAsync(ct);
        // Which pages are playbooks decides who may read them (IFactoryPages).
        await pages.InvalidateAsync(projectId, ct);
        return Results.Created($"{http.Request.Path}/{row.Id}", ToView(row));
    }

    private static async Task<IResult> UpdateAsync(
        Guid playbookId, UpdatePlaybookRequest request, HttpContext http, AutomationDbContext db,
        ICurrentTenant tenant, ICurrentUser user, IWikiPageAccess pages, IWikiPageCreator factoryPages,
        IProjectWorkflowAccess workflows, TimeProvider clock, CancellationToken ct)
    {
        var projectId = http.ResolvedProjectId()!.Value;
        var row = await db.Playbooks.SingleOrDefaultAsync(
            playbook => playbook.Id == playbookId && playbook.ProjectId == projectId, ct);
        if (row is null) return NotFound();
        if (request.Version != row.Version) return Conflict();

        var name = request.Name ?? row.Name;
        var pageId = request.WikiPageId ?? row.WikiPageId;
        var harness = request.Harness ?? row.Harness;
        var maxMinutes = request.MaxMinutes ?? row.MaxMinutes;
        var successStateId = request.HasOnSuccessStateId ? request.OnSuccessStateId : row.OnSuccessStateId;
        var failureStateId = request.HasOnFailureStateId ? request.OnFailureStateId : row.OnFailureStateId;
        if (request.InstructionsMarkdown is null && request.WikiPageId is { } requestedPage
            && !await pages.CanReadAsync(requestedPage, projectId, user.UserId!, ct))
            return NotFound();
        var errors = await ValidateAsync(name, harness,
            successStateId, failureStateId, maxMinutes,
            projectId, workflows, ct);
        // A playbook whose page predates the Factory section keeps working until its
        // instructions are next saved; only a page named in this request must be there.
        await ValidateInstructionsAsync(errors, request.InstructionsMarkdown,
            request.InstructionsMarkdown is null ? request.WikiPageId : null, required: pageId is null, projectId, factoryPages, ct);
        var requiredTags = request.RequiredTags is null ? row.RequiredTags : FleetTags.Normalize(request.RequiredTags);
        if (requiredTags is null) errors["requiredTags"] = [FleetTags.Rule];
        var repositorySteps = request.RepositorySteps is null ? row.RepositorySteps : RepositoryPlans.Normalize(request.RepositorySteps);
        if (RepositoryPlans.Validate(repositorySteps) is { } stepsError) errors["repositorySteps"] = [stepsError];
        if (errors.Count > 0) return Validation(errors);
        if (await NameTakenAsync(db, projectId, name, row.Id, ct))
            return Conflict("A playbook with this name already exists in this project.");

        if (request.InstructionsMarkdown is { } markdown)
        {
            // Written in place when the page is already the factory's; otherwise the
            // instructions move into the section and the old page stays as it was.
            if (pageId is not { } current || !await factoryPages.WriteFactoryPageAsync(current, projectId, user.UserId!, markdown, ct))
                pageId = await factoryPages.CreateFactoryPageAsync(tenant.OrganizationId!.Value, projectId, user.UserId!, name.Trim(), markdown, ct);
            if (pageId is null) return Conflict("The wiki is not available to hold the instructions.");
        }

        row.Name = name.Trim();
        row.WikiPageId = pageId;
        row.Harness = harness;
        row.OnSuccessStateId = successStateId;
        row.OnFailureStateId = failureStateId;
        row.MaxMinutes = maxMinutes;
        row.WorkOnDefaultBranch = request.WorkOnDefaultBranch ?? row.WorkOnDefaultBranch;
        row.RequiredTags = requiredTags!;
        row.RepositorySteps = repositorySteps;
        row.UpdatedAt = clock.GetUtcNow();
        db.Entry(row).Property(playbook => playbook.Version).OriginalValue = request.Version;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return Conflict(); }
        await pages.InvalidateAsync(projectId, ct);
        return Results.Ok(ToView(row, await StageIdsAsync(db, row.Id, ct)));
    }

    /// <summary>
    /// Replaces a playbook's follow-up playbooks with <see cref="PutPlaybookStagesRequest.StagePlaybookIds"/>,
    /// in that order. Each must be another playbook of the same project, at most once; a stage's
    /// own stages are allowed but never followed, so no chain can loop.
    /// </summary>
    private static async Task<IResult> PutStagesAsync(
        Guid playbookId, PutPlaybookStagesRequest request, HttpContext http, AutomationDbContext db,
        ICurrentTenant tenant, TimeProvider clock, CancellationToken ct)
    {
        var projectId = http.ResolvedProjectId()!.Value;
        var row = await db.Playbooks.SingleOrDefaultAsync(
            playbook => playbook.Id == playbookId && playbook.ProjectId == projectId, ct);
        if (row is null) return NotFound();

        var ids = request.StagePlaybookIds ?? [];
        string? error = null;
        if (ids.Count > PlaybookStage.MaxStages)
            error = $"A playbook can have at most {PlaybookStage.MaxStages} follow-up playbooks.";
        else if (ids.Contains(playbookId))
            error = "A playbook cannot follow itself.";
        else if (ids.Distinct().Count() != ids.Count)
            error = "A playbook can appear only once in a chain.";
        else if (await db.Playbooks.CountAsync(playbook => ids.Contains(playbook.Id) && playbook.ProjectId == projectId, ct) != ids.Count)
            error = "Choose playbooks of this project.";
        if (error is not null)
            return Validation(new Dictionary<string, string[]> { ["stagePlaybookIds"] = [error] });

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.PlaybookStages.Where(stage => stage.PlaybookId == playbookId).ExecuteDeleteAsync(ct);
        db.PlaybookStages.AddRange(ids.Select((stageId, position) => new PlaybookStage
        {
            OrganizationId = tenant.OrganizationId!.Value,
            PlaybookId = playbookId,
            StagePlaybookId = stageId,
            Position = position,
        }));
        row.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(ToView(row, [.. ids]));
    }

    private static async Task<IReadOnlyList<Guid>> StageIdsAsync(AutomationDbContext db, Guid playbookId, CancellationToken ct) =>
        await db.PlaybookStages.AsNoTracking().Where(stage => stage.PlaybookId == playbookId)
            .OrderBy(stage => stage.Position).Select(stage => stage.StagePlaybookId).ToListAsync(ct);

    private static async Task<IResult> DeleteAsync(
        Guid playbookId, HttpContext http, AutomationDbContext db, IWikiPageAccess pages, CancellationToken ct)
    {
        var projectId = http.ResolvedProjectId()!.Value;
        var row = await db.Playbooks.SingleOrDefaultAsync(
            playbook => playbook.Id == playbookId && playbook.ProjectId == projectId, ct);
        if (row is null) return NotFound();
        // A rule naming this playbook is what the FK RESTRICT is for; a friendly
        // front door for the violation GlobalExceptionHandler would otherwise turn into a
        // generic conflict, so a caller with no automation UI open still gets a real answer.
        if (await db.Rules.AsNoTracking().AnyAsync(rule => rule.PlaybookId == playbookId, ct))
        {
            return Results.Problem(
                title: "This playbook is used by an automation rule.",
                detail: "Delete or repoint the rule first, then delete the playbook.",
                type: ProblemTypes.PlaybookInUse,
                statusCode: StatusCodes.Status409Conflict);
        }
        db.Playbooks.Remove(row);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.RestrictViolation })
        {
            // A rule was created between the check above and this save - the same race
            // the unique-name index elsewhere in this file resolves the same way.
            return Results.Problem(
                title: "This playbook is used by an automation rule.",
                detail: "Delete or repoint the rule first, then delete the playbook.",
                type: ProblemTypes.PlaybookInUse,
                statusCode: StatusCodes.Status409Conflict);
        }
        await pages.InvalidateAsync(projectId, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> PromoteAsync(
        Guid playbookId, HttpContext http, AutomationDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var projectId = http.ResolvedProjectId()!.Value;
        var row = await db.Playbooks.SingleOrDefaultAsync(
            playbook => playbook.Id == playbookId && playbook.ProjectId == projectId, ct);
        if (row is null) return NotFound();
        if (!row.IsDefault)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await db.Playbooks.Where(playbook => playbook.ProjectId == projectId && playbook.IsDefault)
                .ExecuteUpdateAsync(setters => setters.SetProperty(playbook => playbook.IsDefault, false), ct);
            row.IsDefault = true;
            row.UpdatedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        return Results.Ok(ToView(row, await StageIdsAsync(db, row.Id, ct)));
    }

    private static async Task<IResult> StarterAsync(
        HttpContext http, AutomationDbContext db, ICurrentTenant tenant, ICurrentUser user,
        IWikiPageCreator pages, IWikiPageAccess access, TimeProvider clock, CancellationToken ct)
    {
        var projectId = http.ResolvedProjectId()!.Value;
        if (await db.Playbooks.AnyAsync(
                playbook => playbook.ProjectId == projectId && playbook.Name.ToLower() == StarterName.ToLower(), ct))
            return Conflict("The starter playbook already exists.");

        var pageId = await pages.CreateStarterPageAsync(
            tenant.OrganizationId!.Value, projectId, user.UserId!, StarterMarkdown, ct);
        if (pageId is null) return Conflict("The Factory/Implement wiki page already exists.");

        var now = clock.GetUtcNow();
        var row = new Playbook
        {
            OrganizationId = tenant.OrganizationId.Value,
            ProjectId = projectId,
            Name = StarterName,
            WikiPageId = pageId,
            Harness = Harnesses.Claude,
            MaxMinutes = 60,
            IsDefault = true,
            CreatedBy = user.UserId!,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Playbooks.Where(playbook => playbook.ProjectId == projectId && playbook.IsDefault)
            .ExecuteUpdateAsync(setters => setters.SetProperty(playbook => playbook.IsDefault, false), ct);
        db.Playbooks.Add(row);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        await access.InvalidateAsync(projectId, ct);
        return Results.Created(
            $"/api/v1/orgs/{http.Request.RouteValues["orgSlug"]}/projects/{http.Request.RouteValues["projectKey"]}/playbooks/{row.Id}",
            ToView(row));
    }

    private static Task<IResult> GetSettingsAsync(HttpContext http, AutomationDbContext db, IPlanLimits plans,
        ICurrentTenant tenant, CancellationToken ct) =>
        GetSettingsCoreAsync(http.ResolvedProjectId()!.Value, db, plans, tenant, ct);

    private static async Task<IResult> GetSettingsCoreAsync(Guid projectId, AutomationDbContext db, IPlanLimits plans,
        ICurrentTenant tenant, CancellationToken ct)
    {
        var row = await db.ProjectSettings.AsNoTracking()
            .SingleOrDefaultAsync(settings => settings.ProjectId == projectId, ct);
        var availability = await plans.GetMultiRepoAvailabilityAsync(tenant.OrganizationId!.Value, ct);
        var additional = await db.ProjectRepositories.AsNoTracking()
            .Where(repo => repo.ProjectId == projectId).OrderBy(repo => repo.Position).ToListAsync(ct);
        return Results.Ok(ToView(row ?? new ProjectFactorySettings
        {
            OrganizationId = tenant.OrganizationId!.Value,
            ProjectId = projectId,
            RepoSource = ProjectRepositorySource.RunnerLocal,
        }, availability, additional));
    }

    private static async Task<IResult> PutSettingsAsync(
        UpdateFactorySettingsRequest request, HttpContext http, AutomationDbContext db,
        ICurrentTenant tenant, IProjectAccess access, IAgentIdentities agents, IPlanLimits plans,
        TimeProvider clock, CancellationToken ct)
    {
        var projectId = http.ResolvedProjectId()!.Value;
        var errors = new Dictionary<string, string[]>();
        if (!Enum.IsDefined(typeof(ProjectRepositorySource), request.RepoSource))
            errors["repoSource"] = ["Choose GitHub binding or runner local."];
        var repo = Blank(request.RepoFullName);
        if (request.RepoSource == (short)ProjectRepositorySource.GitHubBinding && repo is null)
            errors["repoFullName"] = ["A GitHub repository is required for this source."];
        if (repo?.Length > 500)
            errors["repoFullName"] = ["Use 500 characters or fewer."];
        var branch = request.DefaultBranch?.Trim() ?? "main";
        if (branch.Length is < 1 or > 255)
            errors["defaultBranch"] = ["A default branch of 1-255 characters is required."];
        var path = Blank(request.LocalPathHint);
        if (path?.Length > 2000)
            errors["localPathHint"] = ["Use 2000 characters or fewer."];
        if (request.DefaultAgentId is { } agentId
            && !await IsAssignableAgentAsync(projectId, agentId, access, agents, ct))
            errors["defaultAgentId"] = ["The default agent must be an active agent that can see this project."];

        var row = await db.ProjectSettings.SingleOrDefaultAsync(settings => settings.ProjectId == projectId, ct);
        // Another organization's runners are outside the query filter, so they read as missing.
        // A runner disabled since it was chosen may stay; choosing one now may not.
        if (request.DefaultRunnerId is { } runnerId && runnerId != row?.DefaultRunnerId
            && !await db.Runners.AnyAsync(r => r.Id == runnerId && r.DeletedAt == null && r.DisabledAt == null, ct))
            errors["defaultRunnerId"] = ["Choose an enabled runner of this organization."];

        // Multiple repositories: the switch and its list change only on a paid hosted plan. A project
        // that downgraded keeps both, and saves its other settings as before.
        var existing = await db.ProjectRepositories.Where(repo => repo.ProjectId == projectId)
            .OrderBy(repo => repo.Position).ToListAsync(ct);
        var additional = request.AdditionalRepositories is null ? null : ValidateRepositories(request.AdditionalRepositories, errors);
        var primaryAlias = Blank(request.PrimaryAlias)?.ToLowerInvariant();
        if (primaryAlias is not null && !RepositoryPlans.IsAlias(primaryAlias))
            errors["primaryAlias"] = [RepositoryPlans.AliasRule];
        var (primaryDirectory, primaryDirectoryError) = RepositoryPlans.CleanWorkingDirectory(request.PrimaryWorkingDirectory);
        if (primaryDirectoryError is not null) errors["primaryWorkingDirectory"] = [primaryDirectoryError];
        var aliasAfter = primaryAlias ?? (row is null ? null : RepositoryPlans.PrimaryAlias(row));
        if (additional is not null && aliasAfter is not null && additional.Any(repo => repo.Alias == aliasAfter))
            errors["additionalRepositories"] = [$"{aliasAfter} is the primary repository's alias already."];
        var multiRepoChanges = (request.MultiRepoEnabled == true && row?.MultiRepoEnabled != true)
            || (primaryAlias is not null && primaryAlias != row?.PrimaryAlias)
            || (request.PrimaryWorkingDirectory is not null && primaryDirectory != row?.PrimaryWorkingDirectory)
            || (additional is not null && !SameRepositories(additional, existing));
        if (multiRepoChanges)
        {
            var availability = await plans.GetMultiRepoAvailabilityAsync(tenant.OrganizationId!.Value, ct);
            if (availability == MultiRepoAvailability.Hidden)
                errors["multiRepoEnabled"] = ["Multiple repositories are not available on this installation."];
            else if (availability == MultiRepoAvailability.UpgradeRequired)
                return Results.Problem(
                    title: "Plan limit reached.",
                    detail: "Multiple repositories and monorepo sub-packages are part of the paid Hosted plan. Upgrade this organization to turn them on.",
                    type: ProblemTypes.PlanLimit,
                    statusCode: StatusCodes.Status402PaymentRequired,
                    extensions: new Dictionary<string, object?> { ["limit"] = "multi_repo" });
        }
        if (errors.Count > 0) return Validation(errors);

        if (row is null)
        {
            if (request.Version != 0) return Conflict();
            row = new ProjectFactorySettings
            {
                OrganizationId = tenant.OrganizationId!.Value,
                ProjectId = projectId,
            };
            db.ProjectSettings.Add(row);
        }
        else
        {
            if (request.Version != row.Version) return Conflict();
            db.Entry(row).Property(settings => settings.Version).OriginalValue = request.Version;
        }

        row.RepoSource = (ProjectRepositorySource)request.RepoSource;
        row.RepoFullName = request.RepoSource == (short)ProjectRepositorySource.GitHubBinding ? repo : null;
        row.DefaultBranch = branch;
        row.LocalPathHint = request.RepoSource == (short)ProjectRepositorySource.RunnerLocal ? path : null;
        row.DefaultAgentId = request.DefaultAgentId;
        row.DefaultRunnerId = request.DefaultRunnerId;
        if (request.MultiRepoEnabled is { } enabled) row.MultiRepoEnabled = enabled;
        if (primaryAlias is not null) row.PrimaryAlias = primaryAlias;
        // Turning the switch on fixes the derived alias, so renaming the repository later renames no checkout.
        if (row.MultiRepoEnabled && row.PrimaryAlias is null) row.PrimaryAlias = RepositoryPlans.PrimaryAlias(row);
        if (request.PrimaryWorkingDirectory is not null) row.PrimaryWorkingDirectory = primaryDirectory;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (additional is not null && !SameRepositories(additional, existing))
        {
            db.ProjectRepositories.RemoveRange(existing);
            // The unique alias index is checked per statement: removals go out before the new rows.
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateConcurrencyException) { return Conflict(); }
            existing = [.. additional.Select((repo, index) => new ProjectRepository
            {
                OrganizationId = tenant.OrganizationId!.Value,
                ProjectId = projectId,
                Position = index,
                Alias = repo.Alias,
                RepoSource = repo.RepoSource,
                RepoFullName = repo.RepoFullName,
                LocalPathHint = repo.LocalPathHint,
                DefaultBranch = repo.DefaultBranch,
                WorkingDirectory = repo.WorkingDirectory,
            })];
            db.ProjectRepositories.AddRange(existing);
        }
        row.UpdatedAt = clock.GetUtcNow();
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return Conflict(); }
        await transaction.CommitAsync(ct);
        var view = await plans.GetMultiRepoAvailabilityAsync(tenant.OrganizationId!.Value, ct);
        return Results.Ok(ToView(row, view, existing));
    }

    /// <summary>The additional repositories as they are stored, or null with the reason in <paramref name="errors"/>.</summary>
    private static List<ProjectRepository>? ValidateRepositories(
        IReadOnlyList<ProjectRepositoryView> repositories, Dictionary<string, string[]> errors)
    {
        if (repositories.Count > ProjectRepository.MaxRepositories)
        {
            errors["additionalRepositories"] = [$"Use at most {ProjectRepository.MaxRepositories} additional repositories."];
            return null;
        }
        var result = new List<ProjectRepository>();
        var aliases = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < repositories.Count; index++)
        {
            var input = repositories[index];
            var key = $"additionalRepositories[{index}]";
            var alias = Blank(input.Alias)?.ToLowerInvariant();
            if (!RepositoryPlans.IsAlias(alias))
            {
                errors[$"{key}.alias"] = [RepositoryPlans.AliasRule];
                continue;
            }
            if (!aliases.Add(alias!))
            {
                errors[$"{key}.alias"] = [$"{alias} is used twice."];
                continue;
            }
            if (!Enum.IsDefined(typeof(ProjectRepositorySource), input.RepoSource))
            {
                errors[$"{key}.repoSource"] = ["Choose GitHub binding or runner local."];
                continue;
            }
            var source = (ProjectRepositorySource)input.RepoSource;
            var repo = Blank(input.RepoFullName);
            var path = Blank(input.LocalPathHint);
            if (source == ProjectRepositorySource.GitHubBinding && repo is null)
                errors[$"{key}.repoFullName"] = ["A GitHub repository is required for this source."];
            else if (repo?.Length > 500)
                errors[$"{key}.repoFullName"] = ["Use 500 characters or fewer."];
            if (path?.Length > 2000)
                errors[$"{key}.localPathHint"] = ["Use 2000 characters or fewer."];
            var branch = Blank(input.DefaultBranch) ?? "main";
            if (branch.Length > 255)
                errors[$"{key}.defaultBranch"] = ["A default branch of 1-255 characters is required."];
            var (directory, directoryError) = RepositoryPlans.CleanWorkingDirectory(input.WorkingDirectory);
            if (directoryError is not null)
                errors[$"{key}.workingDirectory"] = [directoryError];
            result.Add(new ProjectRepository
            {
                Alias = alias!,
                RepoSource = source,
                RepoFullName = source == ProjectRepositorySource.GitHubBinding ? repo : null,
                LocalPathHint = source == ProjectRepositorySource.RunnerLocal ? path : null,
                DefaultBranch = branch,
                WorkingDirectory = directory,
            });
        }
        return result;
    }

    private static bool SameRepositories(IReadOnlyList<ProjectRepository> wanted, IReadOnlyList<ProjectRepository> stored) =>
        wanted.Count == stored.Count && wanted.Zip(stored).All(pair =>
            pair.First.Alias == pair.Second.Alias && pair.First.RepoSource == pair.Second.RepoSource
            && pair.First.RepoFullName == pair.Second.RepoFullName && pair.First.LocalPathHint == pair.Second.LocalPathHint
            && pair.First.DefaultBranch == pair.Second.DefaultBranch && pair.First.WorkingDirectory == pair.Second.WorkingDirectory);

    private static Task<bool> NameTakenAsync(AutomationDbContext db, Guid projectId, string name, Guid? exceptId, CancellationToken ct)
    {
        var lowered = name.Trim().ToLower();
        return db.Playbooks.AnyAsync(playbook => playbook.ProjectId == projectId && playbook.Id != exceptId
            && playbook.Name.ToLower() == lowered, ct);
    }

    /// <summary>
    /// A playbook's instructions are written here, or name a page already in the wiki's
    /// Factory section: never a page people write for each other, such as Home.
    /// </summary>
    private static async Task ValidateInstructionsAsync(Dictionary<string, string[]> errors, string? markdown,
        Guid? pageId, bool required, Guid projectId, IWikiPageCreator factoryPages, CancellationToken ct)
    {
        if (markdown is not null)
        {
            if (string.IsNullOrWhiteSpace(markdown))
                errors["instructionsMarkdown"] = ["Write the instructions agents should follow."];
            else if (markdown.Length > MaxInstructionsLength)
                errors["instructionsMarkdown"] = ["Instructions may not exceed 1 MB."];
        }
        else if (pageId is { } page)
        {
            if (!await factoryPages.IsInFactorySectionAsync(page, projectId, ct))
                errors["wikiPageId"] = ["A playbook's page must be in the wiki's Factory section. Write the instructions on the playbook instead."];
        }
        else if (required)
            errors["instructionsMarkdown"] = ["Write the instructions agents should follow."];
    }

    private static async Task<Dictionary<string, string[]>> ValidateAsync(
        string? name, string? harness, Guid? successStateId, Guid? failureStateId,
        int maxMinutes, Guid projectId, IProjectWorkflowAccess workflows, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > Playbook.MaxNameLength)
            errors["name"] = ["A name of 1-100 characters is required."];
        if (!Harnesses.IsSupported(harness))
            errors["harness"] = ["Choose claude, codex, opencode, cursor, or copilot."];
        if (maxMinutes is < 5 or > 720)
            errors["maxMinutes"] = ["Choose between 5 and 720 minutes."];
        var states = new[] { successStateId, failureStateId }.Where(id => id.HasValue)
            .Select(id => id!.Value).Distinct().ToArray();
        if (!await workflows.StatesBelongToProjectAsync(projectId, states, ct))
        {
            if (successStateId is not null) errors["onSuccessStateId"] = ["State must belong to a workflow in this project."];
            if (failureStateId is not null) errors["onFailureStateId"] = ["State must belong to a workflow in this project."];
        }
        return errors;
    }

    /// <summary>Shared with <c>RuleEndpoints</c>: the agent a run or a rule names must be an active agent this project can see.</summary>
    internal static async Task<bool> IsAssignableAgentAsync(
        Guid projectId, string agentId, IProjectAccess access, IAgentIdentities agents, CancellationToken ct)
    {
        if (!(await access.ListProjectMemberIdsAsync(projectId, ct)).Contains(agentId, StringComparer.Ordinal))
            return false;
        return await agents.FindAsync(agentId, ct) is { IsActive: true };
    }

    private static PlaybookView ToView(Playbook row) => ToView(row, []);

    private static PlaybookView ToView(Playbook row, IReadOnlyList<Guid> stagePlaybookIds) => new(
        row.Id, row.ProjectId, row.Name, row.WikiPageId, row.Harness,
        row.OnSuccessStateId, row.OnFailureStateId, row.MaxMinutes, row.IsDefault,
        row.CreatedBy, row.CreatedAt, row.UpdatedAt, row.Version, row.WorkOnDefaultBranch, stagePlaybookIds, row.RequiredTags,
        row.RepositorySteps);

    private static FactorySettingsView ToView(ProjectFactorySettings row, MultiRepoAvailability availability,
        IReadOnlyList<ProjectRepository> additional) => new(
        row.ProjectId, (short)row.RepoSource, row.RepoFullName, row.DefaultBranch,
        row.LocalPathHint, row.DefaultAgentId, row.Version == 0 ? null : row.UpdatedAt, row.Version, row.DefaultRunnerId,
        row.MultiRepoEnabled, AvailabilityName(availability), RepositoryPlans.PrimaryAlias(row), row.PrimaryWorkingDirectory,
        [.. additional.Select(repo => new ProjectRepositoryView(repo.Alias, (short)repo.RepoSource, repo.RepoFullName,
            repo.LocalPathHint, repo.DefaultBranch, repo.WorkingDirectory))]);

    internal static string AvailabilityName(MultiRepoAvailability availability) => availability switch
    {
        MultiRepoAvailability.Available => "available",
        MultiRepoAvailability.UpgradeRequired => "upgrade",
        _ => "hidden",
    };

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static IResult Validation(Dictionary<string, string[]> errors) =>
        Results.ValidationProblem(errors, type: ProblemTypes.Validation);
    private static IResult Conflict(string? detail = null) => Results.Problem(
        title: "Conflict.", detail: detail ?? "The record changed - refresh and try again.",
        type: ProblemTypes.Conflict, statusCode: StatusCodes.Status409Conflict);
    private static IResult NotFound() => Results.Problem(
        title: "Not found.", detail: "The record does not exist, or you do not have access to it.",
        type: ProblemTypes.NotAMember, statusCode: StatusCodes.Status404NotFound);
}

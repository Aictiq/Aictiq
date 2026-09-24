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
    string CreatedBy, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, uint Version);

/// <param name="InstructionsMarkdown">
/// What agents should follow. Aictiq files it as a page in the wiki's Factory section, named
/// after the playbook. Give this or <paramref name="WikiPageId"/>, a page already in that section.
/// </param>
public sealed record CreatePlaybookRequest(
    string? Name, Guid? WikiPageId, string? Harness,
    Guid? OnSuccessStateId, Guid? OnFailureStateId, int MaxMinutes = 60, string? InstructionsMarkdown = null);

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
    /// <summary>New instructions: a new revision of the playbook's Factory page, or a new page there when it has none.</summary>
    public string? InstructionsMarkdown { get; init; }
    public uint Version { get; init; }

    [JsonIgnore] public bool HasOnSuccessStateId { get; private set; }
    [JsonIgnore] public bool HasOnFailureStateId { get; private set; }
}

public sealed record FactorySettingsView(
    Guid ProjectId, short RepoSource, string? RepoFullName,
    string DefaultBranch, string? LocalPathHint, string? DefaultAgentId,
    DateTimeOffset? UpdatedAt, uint Version);

public sealed record UpdateFactorySettingsRequest(
    short RepoSource, string? RepoFullName, string? DefaultBranch,
    string? LocalPathHint, string? DefaultAgentId, uint Version);

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
        3. Create a branch named from the item key and begin commit subjects with that key so the work links itself.
        4. Keep the claim alive with heartbeats and report progress by editing one `<!-- aictiq:progress -->` comment.
        5. When coding is done, run the project's tests and verify before opening the pull request.
        6. Link the pull request. Do not transition the item yourself; Aictiq applies this playbook's success or failure state after the run ends.
        7. If you stop before finishing, release the item cleanly.

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
        return Results.Ok(rows.Select(ToView).ToList());
    }

    private static async Task<IResult> GetAsync(
        Guid playbookId, HttpContext http, AutomationDbContext db, CancellationToken ct)
    {
        var projectId = http.ResolvedProjectId()!.Value;
        var row = await db.Playbooks.AsNoTracking()
            .SingleOrDefaultAsync(playbook => playbook.Id == playbookId && playbook.ProjectId == projectId, ct);
        return row is null ? NotFound() : Results.Ok(ToView(row));
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
        row.UpdatedAt = clock.GetUtcNow();
        db.Entry(row).Property(playbook => playbook.Version).OriginalValue = request.Version;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return Conflict(); }
        await pages.InvalidateAsync(projectId, ct);
        return Results.Ok(ToView(row));
    }

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
        return Results.Ok(ToView(row));
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

    private static Task<IResult> GetSettingsAsync(HttpContext http, AutomationDbContext db, CancellationToken ct) =>
        GetSettingsCoreAsync(http.ResolvedProjectId()!.Value, db, ct);

    private static async Task<IResult> GetSettingsCoreAsync(Guid projectId, AutomationDbContext db, CancellationToken ct)
    {
        var row = await db.ProjectSettings.AsNoTracking()
            .SingleOrDefaultAsync(settings => settings.ProjectId == projectId, ct);
        return Results.Ok(row is null
            ? new FactorySettingsView(projectId, (short)ProjectRepositorySource.RunnerLocal, null, "main", null, null, null, 0)
            : ToView(row));
    }

    private static async Task<IResult> PutSettingsAsync(
        UpdateFactorySettingsRequest request, HttpContext http, AutomationDbContext db,
        ICurrentTenant tenant, IProjectAccess access, IAgentIdentities agents,
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
        if (errors.Count > 0) return Validation(errors);

        var row = await db.ProjectSettings.SingleOrDefaultAsync(settings => settings.ProjectId == projectId, ct);
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
        row.UpdatedAt = clock.GetUtcNow();
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return Conflict(); }
        return Results.Ok(ToView(row));
    }

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
            errors["harness"] = ["Choose claude, codex, or opencode."];
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

    private static PlaybookView ToView(Playbook row) => new(
        row.Id, row.ProjectId, row.Name, row.WikiPageId, row.Harness,
        row.OnSuccessStateId, row.OnFailureStateId, row.MaxMinutes, row.IsDefault,
        row.CreatedBy, row.CreatedAt, row.UpdatedAt, row.Version);

    private static FactorySettingsView ToView(ProjectFactorySettings row) => new(
        row.ProjectId, (short)row.RepoSource, row.RepoFullName, row.DefaultBranch,
        row.LocalPathHint, row.DefaultAgentId, row.UpdatedAt, row.Version);

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

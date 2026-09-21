using System.Text.Json.Serialization;
using Aictiq.Modules.Automation.Domain;
using Aictiq.Modules.Automation.Mcp;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Aictiq.Modules.Automation.Endpoints;

/// <param name="LastFiring">The most recent time this rule fired — started a run or was
/// skipped — or null if it never has.</param>
public sealed record RuleView(
    Guid Id, Guid ProjectId, string ProjectKey, string ProjectName, string Name,
    Guid TriggerStateId, Guid? RequiredLabelId, Guid PlaybookId, string? PlaybookName,
    string AgentId, string? AgentName, bool Enabled, string CreatedBy,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, uint Version, RuleFiringSummary? LastFiring);

public sealed record RuleFiringSummary(DateTimeOffset At, string ItemKey, Guid? RunId, string? SkipReason);

/// <param name="RunStatus">The run's status as the run views name it (<c>"queued"</c>, <c>"succeeded"</c>, …), or null when this firing was skipped.</param>
public sealed record RuleFiringView(
    Guid ItemId, string ItemKey, Guid EventId, DateTimeOffset At, Guid? RunId, string? RunStatus, string? SkipReason);

public sealed record CreateRuleRequest(
    string? Name, Guid? TriggerStateId, Guid? RequiredLabelId, Guid? PlaybookId, string? AgentId, bool? Enabled = true);

/// <summary>
/// <see cref="RequiredLabelId"/> follows <c>UpdatePlaybookRequest</c>'s pattern for
/// <c>OnSuccessStateId</c>: the init accessor only runs for a property System.Text.Json
/// actually found in the body, so an absent field leaves the rule's label alone while an
/// explicit <c>null</c> clears it — <see cref="HasRequiredLabelId"/> is which one happened.
/// </summary>
public sealed class UpdateRuleRequest
{
    private Guid? _requiredLabelId;

    public string? Name { get; init; }
    public Guid? TriggerStateId { get; init; }
    public Guid? RequiredLabelId
    {
        get => _requiredLabelId;
        init { _requiredLabelId = value; HasRequiredLabelId = true; }
    }
    public Guid? PlaybookId { get; init; }
    public string? AgentId { get; init; }
    public bool? Enabled { get; init; }
    public uint Version { get; init; }

    [JsonIgnore] public bool HasRequiredLabelId { get; private set; }
}

/// <summary>
/// "When an item enters this state, start this playbook" — the conveyor belt behind the
/// workflow's stations. Every route needs project <b>Admin</b> and the factory
/// operator flag: a rule dispatches runs on its own, unattended, so authoring one takes
/// the same permission as starting one by hand — a stakeholder may see runs, never wire
/// up what starts them.
/// </summary>
public static class RuleEndpoints
{
    public static IEndpointRouteBuilder MapRuleEndpoints(this IEndpointRouteBuilder api)
    {
        var rules = api.MapGroup("/orgs/{orgSlug}/projects/{projectKey}/rules")
            .WithTags("Factory rules").RequireAuthorization();
        rules.MapGet("/", ListAsync)
            .RequireProjectRole(ProjectRole.Admin).RequireFactoryOperator().RequireScope(Scopes.Read);
        rules.MapPost("/", CreateAsync)
            .RequireProjectRole(ProjectRole.Admin).RequireFactoryOperator().RequireProjectWritable().RequireScope(Scopes.Write);
        rules.MapGet("/{ruleId:guid}", GetAsync)
            .RequireProjectRole(ProjectRole.Admin).RequireFactoryOperator().RequireScope(Scopes.Read);
        rules.MapPatch("/{ruleId:guid}", UpdateAsync)
            .RequireProjectRole(ProjectRole.Admin).RequireFactoryOperator().RequireProjectWritable().RequireScope(Scopes.Write);
        rules.MapDelete("/{ruleId:guid}", DeleteAsync)
            .RequireProjectRole(ProjectRole.Admin).RequireFactoryOperator().RequireProjectWritable().RequireScope(Scopes.Write);
        rules.MapGet("/{ruleId:guid}/firings", FiringsAsync)
            .RequireProjectRole(ProjectRole.Admin).RequireFactoryOperator().RequireScope(Scopes.Read);

        // Under the organization, like /orgs/{slug}/runs: the Factory → Rules tab groups
        // every project's rules in one screen rather than one project tab at a time.
        var orgRules = api.MapGroup("/orgs/{orgSlug}/rules")
            .WithTags("Factory rules").RequireAuthorization();
        orgRules.MapGet("/", OrgListAsync).RequireOrgRole(OrgRole.Member).RequireFactoryOperator().RequireScope(Scopes.Read);

        return api;
    }

    private static async Task<IResult> ListAsync(
        HttpContext http, AutomationDbContext db, IUserDirectory directory, CancellationToken ct)
    {
        var project = http.ResolvedProject()!;
        var rows = await db.Rules.AsNoTracking().Where(rule => rule.ProjectId == project.Id)
            .OrderBy(rule => rule.Name).ToListAsync(ct);
        var views = await ToViewsAsync(db, directory, rows, Solo(project), ct);
        return Results.Ok(views);
    }

    private static async Task<IResult> OrgListAsync(
        HttpContext http, AutomationDbContext db, ICurrentTenant tenant, ICurrentUser user,
        IProjectAccess access, IUserDirectory directory, CancellationToken ct)
    {
        var organizationId = tenant.OrganizationId!.Value;
        var userId = user.UserId!;
        var visible = await access.ListVisibleProjectIdsAsync(userId, organizationId, ct);
        var adminProjects = new Dictionary<Guid, ProjectRef>();
        foreach (var projectId in visible)
        {
            if (await access.GetProjectRoleAsync(userId, projectId, ct) is { } role
                && role.Satisfies(ProjectRole.Admin)
                && await access.GetProjectAsync(projectId, ct) is { } project)
            {
                adminProjects[projectId] = project;
            }
        }
        if (adminProjects.Count == 0)
        {
            return Results.Ok(Array.Empty<RuleView>());
        }

        var rows = await db.Rules.AsNoTracking()
            .Where(rule => adminProjects.Keys.Contains(rule.ProjectId)).ToListAsync(ct);
        var views = await ToViewsAsync(db, directory, rows, adminProjects, ct);
        return Results.Ok(views
            .OrderBy(view => view.ProjectKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(view => view.Name, StringComparer.OrdinalIgnoreCase)
            .ToList());
    }

    private static async Task<IResult> GetAsync(
        Guid ruleId, HttpContext http, AutomationDbContext db, IUserDirectory directory, CancellationToken ct)
    {
        var project = http.ResolvedProject()!;
        var row = await db.Rules.AsNoTracking()
            .SingleOrDefaultAsync(rule => rule.Id == ruleId && rule.ProjectId == project.Id, ct);
        if (row is null) return NotFound();
        return Results.Ok((await ToViewsAsync(db, directory, [row], Solo(project), ct))[0]);
    }

    private static async Task<IResult> CreateAsync(
        CreateRuleRequest request, HttpContext http, AutomationDbContext db, ICurrentTenant tenant, ICurrentUser user,
        IProjectWorkflowAccess workflows, IWorkItemLabels labels, IWikiPageAccess pages,
        IProjectAccess access, IAgentIdentities agents, IUserDirectory directory, TimeProvider clock, CancellationToken ct)
    {
        var project = http.ResolvedProject()!;
        var errors = await ValidateAsync(db, project.Id, user.UserId!,
            request.Name, request.TriggerStateId, request.RequiredLabelId, request.PlaybookId, request.AgentId,
            workflows, labels, pages, access, agents, ct);
        if (errors.Count > 0) return Validation(errors);

        var now = clock.GetUtcNow();
        var row = new Rule
        {
            OrganizationId = tenant.OrganizationId!.Value,
            ProjectId = project.Id,
            Name = request.Name!.Trim(),
            TriggerStateId = request.TriggerStateId!.Value,
            RequiredLabelId = request.RequiredLabelId,
            PlaybookId = request.PlaybookId!.Value,
            AgentUserId = request.AgentId!,
            Enabled = request.Enabled ?? true,
            CreatedBy = user.UserId!,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Rules.Add(row);
        await db.SaveChangesAsync(ct);
        var view = (await ToViewsAsync(db, directory, [row], Solo(project), ct))[0];
        return Results.Created($"{http.Request.Path}/{row.Id}", view);
    }

    private static async Task<IResult> UpdateAsync(
        Guid ruleId, UpdateRuleRequest request, HttpContext http, AutomationDbContext db, ICurrentUser user,
        IProjectWorkflowAccess workflows, IWorkItemLabels labels, IWikiPageAccess pages,
        IProjectAccess access, IAgentIdentities agents, IUserDirectory directory, TimeProvider clock, CancellationToken ct)
    {
        var project = http.ResolvedProject()!;
        var row = await db.Rules.SingleOrDefaultAsync(rule => rule.Id == ruleId && rule.ProjectId == project.Id, ct);
        if (row is null) return NotFound();
        if (request.Version != row.Version) return Conflict();

        var name = request.Name ?? row.Name;
        var triggerStateId = request.TriggerStateId ?? row.TriggerStateId;
        var requiredLabelId = request.HasRequiredLabelId ? request.RequiredLabelId : row.RequiredLabelId;
        var playbookId = request.PlaybookId ?? row.PlaybookId;
        var agentId = request.AgentId ?? row.AgentUserId;

        var errors = await ValidateAsync(db, project.Id, user.UserId!,
            name, triggerStateId, requiredLabelId, playbookId, agentId,
            workflows, labels, pages, access, agents, ct);
        if (errors.Count > 0) return Validation(errors);

        row.Name = name.Trim();
        row.TriggerStateId = triggerStateId;
        row.RequiredLabelId = requiredLabelId;
        row.PlaybookId = playbookId;
        row.AgentUserId = agentId;
        row.Enabled = request.Enabled ?? row.Enabled;
        row.UpdatedAt = clock.GetUtcNow();
        db.Entry(row).Property(rule => rule.Version).OriginalValue = request.Version;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return Conflict(); }
        return Results.Ok((await ToViewsAsync(db, directory, [row], Solo(project), ct))[0]);
    }

    private static async Task<IResult> DeleteAsync(
        Guid ruleId, HttpContext http, AutomationDbContext db, CancellationToken ct)
    {
        var project = http.ResolvedProject()!;
        var row = await db.Rules.SingleOrDefaultAsync(rule => rule.Id == ruleId && rule.ProjectId == project.Id, ct);
        if (row is null) return NotFound();
        // Firings cascade with the rule (fk_rule_firings_rules_rule_id); a run the rule
        // started is untouched — Run.RuleId carries no FK, on purpose.
        db.Rules.Remove(row);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> FiringsAsync(
        Guid ruleId, HttpContext http, AutomationDbContext db, CancellationToken ct)
    {
        var project = http.ResolvedProject()!;
        if (!await db.Rules.AsNoTracking().AnyAsync(rule => rule.Id == ruleId && rule.ProjectId == project.Id, ct))
        {
            return NotFound();
        }

        var firings = await db.RuleFirings.AsNoTracking().Where(firing => firing.RuleId == ruleId)
            .OrderByDescending(firing => firing.At).Take(50).ToListAsync(ct);
        var runIds = firings.Where(firing => firing.RunId is not null).Select(firing => firing.RunId!.Value).Distinct().ToList();
        var statuses = runIds.Count == 0
            ? new Dictionary<Guid, RunStatus>()
            : await db.Runs.AsNoTracking().Where(run => runIds.Contains(run.Id))
                .ToDictionaryAsync(run => run.Id, run => run.Status, ct);
        return Results.Ok(firings.Select(firing => new RuleFiringView(
            firing.ItemId, firing.ItemKey, firing.EventId, firing.At, firing.RunId,
            firing.RunId is { } runId && statuses.TryGetValue(runId, out var status) ? RunMcpViews.StatusName(status) : null,
            firing.SkipReason)).ToList());
    }

    // ── validation ───────────────────────────────────────────────────────────────────

    private static async Task<Dictionary<string, string[]>> ValidateAsync(
        AutomationDbContext db, Guid projectId, string userId,
        string? name, Guid? triggerStateId, Guid? requiredLabelId, Guid? playbookId, string? agentId,
        IProjectWorkflowAccess workflows, IWorkItemLabels labels, IWikiPageAccess pages,
        IProjectAccess access, IAgentIdentities agents, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > Rule.MaxNameLength)
            errors["name"] = ["A name of 1-100 characters is required."];
        if (triggerStateId is not { } stateId || !await workflows.StatesBelongToProjectAsync(projectId, [stateId], ct))
            errors["triggerStateId"] = ["State must belong to the project workflow."];
        if (requiredLabelId is { } labelId && !await labels.LabelsBelongToProjectAsync(projectId, [labelId], ct))
            errors["requiredLabelId"] = ["Label must belong to this project."];
        if (playbookId is null)
        {
            errors["playbookId"] = ["A playbook is required."];
        }
        else
        {
            var playbook = await db.Playbooks.AsNoTracking()
                .SingleOrDefaultAsync(candidate => candidate.Id == playbookId && candidate.ProjectId == projectId, ct);
            if (playbook is null)
                errors["playbookId"] = ["Playbook not found or no access."];
            else if (playbook.WikiPageId is not { } pageId || !await pages.CanReadAsync(pageId, projectId, userId, ct))
                errors["playbookId"] = ["The playbook's wiki page must exist and be readable by you."];
        }
        if (string.IsNullOrWhiteSpace(agentId)
            || !await PlaybookEndpoints.IsAssignableAgentAsync(projectId, agentId, access, agents, ct))
            errors["agentId"] = ["The agent must be an active agent that can see this project."];
        return errors;
    }

    // ── views ────────────────────────────────────────────────────────────────────────

    private static Dictionary<Guid, ProjectRef> Solo(ProjectRef project) => new() { [project.Id] = project };

    private static async Task<List<RuleView>> ToViewsAsync(
        AutomationDbContext db, IUserDirectory directory, IReadOnlyList<Rule> rows,
        IReadOnlyDictionary<Guid, ProjectRef> projects, CancellationToken ct)
    {
        if (rows.Count == 0) return [];

        var playbookIds = rows.Select(rule => rule.PlaybookId).Distinct().ToList();
        var playbooks = await db.Playbooks.AsNoTracking().Where(playbook => playbookIds.Contains(playbook.Id))
            .Select(playbook => new { playbook.Id, playbook.Name }).ToDictionaryAsync(row => row.Id, row => row.Name, ct);
        var people = await directory.GetAsync([.. rows.Select(rule => rule.AgentUserId).Distinct()], ct);
        var lastFirings = await LastFiringsAsync(db, [.. rows.Select(rule => rule.Id)], ct);

        return rows.Select(rule =>
        {
            var project = projects.GetValueOrDefault(rule.ProjectId);
            return new RuleView(
                rule.Id, rule.ProjectId, project?.Key ?? "", project?.Name ?? "", rule.Name,
                rule.TriggerStateId, rule.RequiredLabelId, rule.PlaybookId, playbooks.GetValueOrDefault(rule.PlaybookId),
                rule.AgentUserId, people.TryGetValue(rule.AgentUserId, out var agent) ? agent.DisplayName : null,
                rule.Enabled, rule.CreatedBy, rule.CreatedAt, rule.UpdatedAt, rule.Version,
                lastFirings.GetValueOrDefault(rule.Id));
        }).ToList();
    }

    /// <summary>
    /// The one most recent firing per rule, in a single query — <c>DISTINCT ON</c> rather
    /// than a group-by-then-first, which Npgsql's LINQ translation does not support for an
    /// unaggregated row. Raw SQL, snake_case columns, aliased to the record's property names.
    /// </summary>
    private static async Task<Dictionary<Guid, RuleFiringSummary>> LastFiringsAsync(
        AutomationDbContext db, IReadOnlyCollection<Guid> ruleIds, CancellationToken ct)
    {
        if (ruleIds.Count == 0) return [];
        var ids = ruleIds.ToArray();
        // No column aliases: EFCore.NamingConventions maps the row's own snake_case names
        // (rule_id, item_key, …) onto the record's properties, exactly as it would a mapped
        // entity — an explicit "AS \"PascalCase\"" alias defeats that and EF cannot find the
        // column it expects for any name that isn't a single lowercase word.
        var rows = await db.Database.SqlQuery<LastFiringRow>($"""
            SELECT DISTINCT ON (rule_id) rule_id, at, item_key, run_id, skip_reason
            FROM automation.rule_firings
            WHERE rule_id = ANY({ids})
            ORDER BY rule_id, at DESC
            """).ToListAsync(ct);
        return rows.ToDictionary(row => row.RuleId, row => new RuleFiringSummary(row.At, row.ItemKey, row.RunId, row.SkipReason));
    }

    private sealed record LastFiringRow(Guid RuleId, DateTimeOffset At, string ItemKey, Guid? RunId, string? SkipReason);

    private static IResult Validation(Dictionary<string, string[]> errors) =>
        Results.ValidationProblem(errors, type: ProblemTypes.Validation);
    private static IResult Conflict(string? detail = null) => Results.Problem(
        title: "Conflict.", detail: detail ?? "The record changed — refresh and try again.",
        type: ProblemTypes.Conflict, statusCode: StatusCodes.Status409Conflict);
    private static IResult NotFound() => Results.Problem(
        title: "Not found.", detail: "The record does not exist, or you do not have access to it.",
        type: ProblemTypes.NotAMember, statusCode: StatusCodes.Status404NotFound);
}

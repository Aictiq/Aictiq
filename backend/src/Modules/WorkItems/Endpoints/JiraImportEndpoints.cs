using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Aictiq.Modules.WorkItems.Domain;
using Aictiq.Modules.WorkItems.Jira;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Tenancy;

namespace Aictiq.Modules.WorkItems.Endpoints;

/// <summary>What every step of the wizard sends: the token travels with each request and is never kept by the API.</summary>
public record JiraConnectRequest(string? SiteUrl, string? Email, string? ApiToken)
{
    public override string ToString() => $"{nameof(JiraConnectRequest)} {{ SiteUrl = {SiteUrl}, Email = {Email} }}";
}
public sealed record JiraPreviewRequest(string? SiteUrl, string? Email, string? ApiToken, string? JiraProjectKey) : JiraConnectRequest(SiteUrl, Email, ApiToken)
{
    public override string ToString() => $"{nameof(JiraPreviewRequest)} {{ SiteUrl = {SiteUrl}, Email = {Email}, JiraProjectKey = {JiraProjectKey} }}";
}
public sealed record StartJiraImportRequest(string? SiteUrl, string? Email, string? ApiToken, string? JiraProjectKey,
    IReadOnlyDictionary<string, WorkItemType>? IssueTypes, IReadOnlyDictionary<string, Guid>? Statuses) : JiraConnectRequest(SiteUrl, Email, ApiToken)
{
    public override string ToString() => $"{nameof(StartJiraImportRequest)} {{ SiteUrl = {SiteUrl}, Email = {Email}, JiraProjectKey = {JiraProjectKey} }}";
}

public sealed record JiraConnectionView(string SiteUrl, string AccountName, IReadOnlyList<JiraProjectSummary> Projects);
public sealed record JiraIssueTypePreview(string Name, WorkItemType SuggestedType);
public sealed record JiraStatusPreview(string Name, string Category, Guid SuggestedStateId);
public sealed record JiraUserPreview(string AccountId, string DisplayName, string? MatchedUserId, string? MatchedDisplayName);
public sealed record JiraStateOption(Guid Id, string Name, WorkflowStateCategory Category);
public sealed record JiraImportPreview(JiraProjectSummary JiraProject, int IssueCount, int AlreadyImported, IReadOnlyList<JiraIssueTypePreview> IssueTypes,
    IReadOnlyList<JiraStatusPreview> Statuses, IReadOnlyList<JiraUserPreview> Users, IReadOnlyList<JiraStateOption> States);
public sealed record JiraImportJobView(Guid Id, string JiraProjectKey, string JiraProjectName, string SiteUrl, JiraImportStatus Status, int TotalIssues,
    int ProcessedIssues, int CreatedItems, int SkippedIssues, int CreatedComments, int CreatedAttachments, int CreatedSprints,
    IReadOnlyList<JiraImportFailure> Failures, DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt);

/// <summary>
/// Project settings, Import from Jira: connect, pick a project, confirm the mapping, then watch
/// the job. Every step re-presents the credentials, so the API holds no Jira session between
/// requests; only a queued job keeps the token, protected, until it ends.
/// </summary>
public static class JiraImportEndpoints
{
    public static IEndpointRouteBuilder MapJiraImportEndpoints(this IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/orgs/{orgSlug}/projects/{projectKey}").WithTags("Jira import").RequireAuthorization();
        group.MapPost("/import/jira/connect", Connect).RequireProjectRole(ProjectRole.Member).RequireProjectWritable().RequireScope(Scopes.Write);
        group.MapPost("/import/jira/preview", Preview).RequireProjectRole(ProjectRole.Member).RequireProjectWritable().RequireScope(Scopes.Write);
        group.MapPost("/import/jira", Start).RequireProjectRole(ProjectRole.Member).RequireProjectWritable().RequireScope(Scopes.Write);
        group.MapGet("/imports/jira", List).RequireProjectRole(ProjectRole.Member).RequireScope(Scopes.Read);
        group.MapGet("/imports/jira/{jobId:guid}", Status).RequireProjectRole(ProjectRole.Member).RequireScope(Scopes.Read);
        return api;
    }

    private static async Task<IResult> Connect(JiraConnectRequest request, JiraClient jira, IOptions<JiraImportOptions> options, CancellationToken ct)
    {
        if (Credentials(request, options.Value, out var credentials) is { } invalid) return invalid;
        try
        {
            var me = await jira.MyselfAsync(credentials, ct);
            var projects = await jira.ProjectsAsync(credentials, ct);
            return Results.Ok(new JiraConnectionView(credentials.Site.ToString().TrimEnd('/'), me.DisplayName, projects));
        }
        catch (JiraException exception) { return JiraProblem(exception); }
    }

    private static async Task<IResult> Preview(JiraPreviewRequest request, HttpContext http, WorkItemsDbContext db, JiraClient jira, IUserDirectory directory,
        IProjectAccess access, ICurrentTenant tenant, IOptions<JiraImportOptions> options, CancellationToken ct)
    {
        if (Credentials(request, options.Value, out var credentials) is { } invalid) return invalid;
        if (string.IsNullOrWhiteSpace(request.JiraProjectKey)) return Invalid("jiraProjectKey", "Pick a Jira project.");
        var project = http.ResolvedProject()!;
        try
        {
            var source = await jira.ProjectAsync(credentials, request.JiraProjectKey.Trim(), ct);
            if (source is null) return Invalid("jiraProjectKey", "That Jira project does not exist or this account cannot see it.");
            var types = await jira.IssueTypesAsync(credentials, source.Key, ct);
            var count = await jira.CountAsync(credentials, JiraImporter.Jql(source.Key), ct);
            var users = await jira.AssignableUsersAsync(credentials, source.Key, ct);
            var states = await StatesAsync(db, tenant.OrganizationId!.Value, project.Id, ct);
            var initial = states.First(s => s.IsInitial);

            var statuses = types.SelectMany(t => t.Statuses).GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                .Select(s => new JiraStatusPreview(s.Name, s.Category, (JiraImporter.SuggestState(states, s.Name, s.Category) ?? initial).Id)).ToList();
            var issueTypes = types.GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                .Select(t => new JiraIssueTypePreview(t.Name, JiraImporter.SuggestType(t.Name, t.Subtask))).ToList();

            var members = (await access.ListProjectMemberIdsAsync(project.Id, ct)).ToHashSet(StringComparer.Ordinal);
            var matches = new Dictionary<string, string?>();
            foreach (var user in users.Where(u => !string.IsNullOrWhiteSpace(u.Email)))
                matches[user.AccountId] = await directory.FindByEmailAsync(user.Email!.Trim(), ct) is { } found && members.Contains(found.Id) ? found.Id : null;
            var names = await directory.GetAsync(matches.Values.OfType<string>().Distinct().ToList(), ct);
            var people = users.OrderBy(u => u.DisplayName, StringComparer.OrdinalIgnoreCase).Select(u =>
            {
                var id = matches.GetValueOrDefault(u.AccountId);
                return new JiraUserPreview(u.AccountId, u.DisplayName, id, id is null ? null : names.GetValueOrDefault(id)?.DisplayName);
            }).ToList();

            var prefix = source.Key + "-";
            var already = await db.Items.CountAsync(x => x.ProjectId == project.Id && x.ExternalRef != null && x.ExternalRef.StartsWith(prefix), ct);
            return Results.Ok(new JiraImportPreview(source, count, already, issueTypes, statuses, people,
                states.OrderBy(s => s.Position).Select(s => new JiraStateOption(s.Id, s.Name, s.Category)).ToList()));
        }
        catch (JiraException exception) { return JiraProblem(exception); }
    }

    private static async Task<IResult> Start(StartJiraImportRequest request, HttpContext http, WorkItemsDbContext db, JiraClient jira, JiraTokenProtector tokens,
        ICurrentUser user, ICurrentTenant tenant, IOptions<JiraImportOptions> options, TimeProvider clock, CancellationToken ct)
    {
        if (Credentials(request, options.Value, out var credentials) is { } invalid) return invalid;
        if (string.IsNullOrWhiteSpace(request.JiraProjectKey)) return Invalid("jiraProjectKey", "Pick a Jira project.");
        var project = http.ResolvedProject()!;
        var organizationId = tenant.OrganizationId!.Value;
        var states = await StatesAsync(db, organizationId, project.Id, ct);
        var types = request.IssueTypes ?? new Dictionary<string, WorkItemType>();
        var statuses = request.Statuses ?? new Dictionary<string, Guid>();
        if (types.Values.Any(t => !Enum.IsDefined(t))) return Invalid("issueTypes", "Map each Jira issue type to an Aictiq item type.");
        if (statuses.Values.Any(id => states.All(s => s.Id != id))) return Invalid("statuses", "Map each Jira status to a state of this project's workflow.");
        if (await db.JiraImportJobs.AnyAsync(x => x.ProjectId == project.Id && (x.Status == JiraImportStatus.Pending || x.Status == JiraImportStatus.Running), ct))
            return Results.Problem("An import from Jira is already running for this project. Wait for it to finish.", statusCode: StatusCodes.Status409Conflict, type: ProblemTypes.Conflict);

        JiraProjectSummary? source; int count;
        try
        {
            // Checked again here rather than trusted from the preview: the token may have been revoked since.
            source = await jira.ProjectAsync(credentials, request.JiraProjectKey.Trim(), ct);
            if (source is null) return Invalid("jiraProjectKey", "That Jira project does not exist or this account cannot see it.");
            count = await jira.CountAsync(credentials, JiraImporter.Jql(source.Key), ct);
        }
        catch (JiraException exception) { return JiraProblem(exception); }

        var job = new JiraImportJob
        {
            OrganizationId = organizationId, ProjectId = project.Id, ProjectKey = project.Key, OrganizationSlug = http.Request.RouteValues["orgSlug"]?.ToString() ?? "",
            RequestedBy = user.UserId!, SiteUrl = credentials.Site.ToString().TrimEnd('/'), Email = credentials.Email, ProtectedToken = tokens.Protect(credentials.ApiToken),
            JiraProjectKey = source.Key, JiraProjectName = source.Name.Length > 255 ? source.Name[..255] : source.Name,
            IssueTypeMap = types.Where(x => !string.IsNullOrWhiteSpace(x.Key)).ToDictionary(x => x.Key, x => x.Value),
            StatusMap = statuses.Where(x => !string.IsNullOrWhiteSpace(x.Key)).ToDictionary(x => x.Key, x => x.Value),
            Status = JiraImportStatus.Pending, TotalIssues = count, CreatedAt = clock.GetUtcNow(),
        };
        db.JiraImportJobs.Add(job); await db.SaveChangesAsync(ct);
        return Results.Accepted($"/api/v1/orgs/{job.OrganizationSlug}/projects/{project.Key}/imports/jira/{job.Id}", View(job));
    }

    private static async Task<IResult> List(HttpContext http, WorkItemsDbContext db, CancellationToken ct)
    {
        var projectId = http.ResolvedProjectId()!.Value;
        var jobs = await db.JiraImportJobs.AsNoTracking().Where(x => x.ProjectId == projectId).OrderByDescending(x => x.CreatedAt).Take(10).ToListAsync(ct);
        return Results.Ok(jobs.Select(View));
    }

    private static async Task<IResult> Status(Guid jobId, HttpContext http, WorkItemsDbContext db, CancellationToken ct)
    {
        var projectId = http.ResolvedProjectId()!.Value;
        var job = await db.JiraImportJobs.AsNoTracking().FirstOrDefaultAsync(x => x.Id == jobId && x.ProjectId == projectId, ct);
        return job is null ? Results.NotFound() : Results.Ok(View(job));
    }

    private static async Task<List<WorkflowState>> StatesAsync(WorkItemsDbContext db, Guid organizationId, Guid projectId, CancellationToken ct)
    {
        await WorkflowEndpoints.EnsureDefaultAsync(db, organizationId, projectId, ct);
        return await db.WorkflowStates.AsNoTracking().Where(s => db.Workflows.Any(w => w.Id == s.WorkflowId && w.ProjectId == projectId && w.IsDefault)).ToListAsync(ct);
    }

    private static IResult? Credentials(JiraConnectRequest request, JiraImportOptions policy, out JiraCredentials credentials)
    {
        credentials = null!;
        var errors = new Dictionary<string, string[]>();
        if (!JiraClient.TryNormalizeSite(request.SiteUrl, policy, out var site, out var siteError)) errors["siteUrl"] = [siteError];
        var email = request.Email?.Trim() ?? "";
        if (email.Length is 0 or > 320 || !email.Contains('@')) errors["email"] = ["Enter the email address of your Atlassian account."];
        var token = request.ApiToken?.Trim() ?? "";
        if (token.Length is 0 or > 1024) errors["apiToken"] = ["Enter a Jira API token. Create one at id.atlassian.com, under Security."];
        if (errors.Count > 0) return Results.ValidationProblem(errors);
        credentials = new JiraCredentials(site, email, token);
        return null;
    }

    private static IResult JiraProblem(JiraException exception) =>
        Invalid(exception is JiraAuthException ? "apiToken" : "siteUrl", exception.Message);

    private static IResult Invalid(string field, string message) => Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });

    internal static JiraImportJobView View(JiraImportJob x) => new(x.Id, x.JiraProjectKey, x.JiraProjectName, x.SiteUrl, x.Status, x.TotalIssues, x.ProcessedIssues,
        x.CreatedItems, x.SkippedIssues, x.CreatedComments, x.CreatedAttachments, x.CreatedSprints, x.Failures, x.CreatedAt, x.StartedAt, x.CompletedAt);
}

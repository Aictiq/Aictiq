using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Aictiq.Modules.Integrations.Contracts;
using Aictiq.Modules.Integrations.Domain;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Outbox;
using Aictiq.SharedKernel.Tenancy;

namespace Aictiq.Modules.Integrations.Endpoints;

public sealed record GitHubInstallationView(long InstallationId, string AccountLogin, string AccountType,
    GitHubInstallationStatus Status, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record GitHubRepositoryView(long Id, string FullName, long InstallationId);
public sealed record RepoBindingView(Guid Id, long RepoId, long InstallationId, string FullName,
    Guid? OnPullRequestOpenedStateId, Guid? OnPullRequestMergedStateId, DateTimeOffset CreatedAt);
public sealed record BindRepositoryRequest(long RepoId);
public sealed record UpdatePullRequestRulesRequest(Guid? OnPullRequestOpenedStateId, Guid? OnPullRequestMergedStateId);
public sealed record GitHubDeliveryView(string DeliveryId, long? InstallationId, string EventType, string? Action,
    string Status, DateTimeOffset ReceivedAt, DateTimeOffset? ProcessedAt, string? LastError);

public static class GitHubEndpoints
{
    /// <summary>Maps versioned, authenticated organization/project routes.</summary>
    public static IEndpointRouteBuilder MapGitHubApiEndpoints(this IEndpointRouteBuilder api)
    {
        var orgs = api.MapGroup("/orgs/{orgSlug}/github").WithTags("GitHub").RequireAuthorization();
        orgs.MapGet("/install-url", InstallUrl).RequireOrgRole(OrgRole.Admin).RequireScope(Scopes.Admin);
        orgs.MapGet("/installations", ListInstallations).RequireOrgRole(OrgRole.Admin).RequireScope(Scopes.Read);
        orgs.MapGet("/installations/{installationId:long}/repositories", ListRepositories)
            .RequireOrgRole(OrgRole.Admin).RequireScope(Scopes.Read);
        orgs.MapDelete("/installations/{installationId:long}", Disconnect).RequireOrgRole(OrgRole.Admin).RequireScope(Scopes.Admin);
        orgs.MapGet("/deliveries", ListDeliveries).RequireOrgRole(OrgRole.Admin).RequireScope(Scopes.Read);
        orgs.MapPost("/deliveries/{deliveryId}/reprocess", Reprocess).RequireOrgRole(OrgRole.Admin).RequireScope(Scopes.Admin);

        var projects = api.MapGroup("/orgs/{orgSlug}/projects/{projectKey}/github/bindings")
            .WithTags("GitHub").RequireAuthorization();
        projects.MapGet("/", ListBindings).RequireProjectRole(ProjectRole.Guest).RequireScope(Scopes.Read);
        projects.MapPost("/", Bind).RequireProjectRole(ProjectRole.Admin).RequireProjectWritable().RequireScope(Scopes.Write);
        projects.MapPut("/{repoId:long}/pull-request-rules", UpdatePullRequestRules).RequireProjectRole(ProjectRole.Admin).RequireProjectWritable().RequireScope(Scopes.Write);
        projects.MapDelete("/{repoId:long}", Unbind).RequireProjectRole(ProjectRole.Admin).RequireProjectWritable().RequireScope(Scopes.Write);
        return api;
    }

    /// <summary>
    /// Maps the unversioned redirect endpoint expected by the GitHub App setup URL. It
    /// must not be inside an organization route: GitHub only returns installation_id and
    /// the signed state carries the organization selected before leaving Aictiq.
    /// </summary>
    public static IEndpointRouteBuilder MapGitHubSetupEndpoint(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/integrations/github/setup", Setup).WithTags("GitHub").RequireAuthorization().RequireScope(Scopes.Admin);
        return endpoints;
    }

    /// <summary>Maps GitHub's public webhook URL, outside the versioned API prefix.</summary>
    public static IEndpointRouteBuilder MapGitHubWebhookEndpoint(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/webhooks/github", ReceiveWebhook).WithTags("GitHub").AllowAnonymous();
        return endpoints;
    }

    private static IResult InstallUrl(ICurrentTenant tenant, IOptions<GitHubOptions> options, TimeProvider clock)
    {
        var configured = options.Value;
        if (!configured.IsConfigured) return Results.NotFound();
        var state = GitHubInstallState.Create(tenant.OrganizationId!.Value, configured.WebhookSecret!, clock);
        return Results.Ok(new { url = $"https://github.com/apps/{configured.AppSlug}/installations/new?state={Uri.EscapeDataString(state)}" });
    }

    private static async Task<IResult> Setup(
        long? installation_id,
        string? state,
        IntegrationsDbContext db,
        ICurrentUser user,
        IProjectAccess access,
        GitHubAppClient github,
        IOptions<GitHubOptions> options,
        TimeProvider clock,
        CancellationToken ct)
    {
        var configured = options.Value;
        if (!configured.IsConfigured) return Results.NotFound();
        if (installation_id is not > 0 || !GitHubInstallState.TryValidate(state, configured.WebhookSecret!, clock, out var organizationId))
            return Results.BadRequest(new { error = "Invalid or expired GitHub installation state." });
        if (user.UserId is not { } userId || await access.GetOrgRoleAsync(userId, organizationId, ct) is not { } role || !role.Satisfies(OrgRole.Admin))
            return Results.NotFound();

        var account = await github.GetInstallationAsync(installation_id.Value, ct);
        var installation = await db.GitHubInstallations.IgnoreQueryFilters()
            .SingleOrDefaultAsync(x => x.InstallationId == installation_id.Value, ct);
        var now = clock.GetUtcNow();
        if (installation is null)
        {
            db.GitHubInstallations.Add(new GitHubInstallation
            {
                OrganizationId = organizationId,
                InstallationId = installation_id.Value,
                AccountLogin = account.Login,
                AccountType = account.Type,
                Status = GitHubInstallationStatus.Active,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
        else if (installation.OrganizationId == organizationId)
        {
            installation.AccountLogin = account.Login;
            installation.AccountType = account.Type;
            installation.Status = GitHubInstallationStatus.Active;
            installation.UpdatedAt = now;
        }
        else
        {
            // An installation belongs to one Aictiq organization. Do not silently move
            // it when a stale/forged-looking browser callback names another organization.
            return Results.Conflict(new { error = "This GitHub installation is already linked to another organization." });
        }
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { installationId = installation_id.Value, accountLogin = account.Login });
    }

    private static async Task<IResult> ListInstallations(IntegrationsDbContext db, CancellationToken ct) =>
        Results.Ok(await db.GitHubInstallations.AsNoTracking().OrderBy(x => x.AccountLogin)
            .Select(x => new GitHubInstallationView(x.InstallationId, x.AccountLogin, x.AccountType, x.Status, x.CreatedAt, x.UpdatedAt))
            .ToListAsync(ct));

    private static async Task<IResult> ListRepositories(long installationId, IntegrationsDbContext db, GitHubAppClient github, CancellationToken ct)
    {
        if (!await db.GitHubInstallations.AnyAsync(x => x.InstallationId == installationId && x.Status == GitHubInstallationStatus.Active, ct))
            return Results.NotFound();
        var repositories = await github.ListRepositoriesAsync(installationId, ct);
        return Results.Ok(repositories.Select(x => new GitHubRepositoryView(x.Id, x.FullName, x.InstallationId)));
    }

    private static async Task<IResult> Disconnect(long installationId, IntegrationsDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var installation = await db.GitHubInstallations.SingleOrDefaultAsync(x => x.InstallationId == installationId, ct);
        if (installation is null) return Results.NotFound();
        // GitHub will independently send its installation.deleted webhook. Marking this
        // locally first immediately makes bindings unusable without dropping their audit history.
        installation.Status = GitHubInstallationStatus.Deleted;
        installation.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> ListDeliveries(IntegrationsDbContext db, int page = 1, int pageSize = 50, CancellationToken ct = default)
    {
        page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 1, 100);
        var rows = await db.GitHubDeliveries.AsNoTracking().OrderByDescending(x => x.ReceivedAt).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new GitHubDeliveryView(x.DeliveryId, x.InstallationId, x.EventType, null, "received", x.ReceivedAt, null, null)).ToListAsync(ct);
        return Results.Ok(rows);
    }

    private static async Task<IResult> Reprocess(string deliveryId, IntegrationsDbContext db, CancellationToken ct)
    {
        if (!await db.GitHubDeliveries.AnyAsync(x => x.DeliveryId == deliveryId, ct)) return Results.NotFound();
        db.Set<OutboxMessage>().Add(OutboxMessage.From(new GitHubDeliveryReceived(deliveryId)));
        await db.SaveChangesAsync(ct);
        return Results.Accepted();
    }

    private static async Task<IResult> ListBindings(HttpContext http, IntegrationsDbContext db, CancellationToken ct)
    {
        var projectId = http.ResolvedProjectId()!.Value;
        return Results.Ok(await db.RepoBindings.AsNoTracking().Where(x => x.ProjectId == projectId)
            .OrderBy(x => x.FullName).Select(x => ToView(x)).ToListAsync(ct));
    }

    private static async Task<IResult> Bind(BindRepositoryRequest request, HttpContext http, IntegrationsDbContext db,
        GitHubAppClient github, ICurrentTenant tenant, TimeProvider clock, CancellationToken ct)
    {
        if (request.RepoId <= 0) return Results.ValidationProblem(new Dictionary<string, string[]> { ["repoId"] = ["A GitHub repository id is required."] });
        var projectId = http.ResolvedProjectId()!.Value;
        if (await db.RepoBindings.AnyAsync(x => x.ProjectId == projectId && x.RepoId == request.RepoId, ct))
            return Results.Conflict(new { error = "This repository is already bound to the project." });

        var installations = await db.GitHubInstallations.AsNoTracking()
            .Where(x => x.Status == GitHubInstallationStatus.Active).Select(x => x.InstallationId).ToListAsync(ct);
        GitHubRepository? repository = null;
        foreach (var installationId in installations)
        {
            repository = (await github.ListRepositoriesAsync(installationId, ct)).FirstOrDefault(x => x.Id == request.RepoId);
            if (repository is not null) break;
        }
        if (repository is null) return Results.NotFound();

        var binding = new RepoBinding
        {
            OrganizationId = tenant.OrganizationId!.Value,
            ProjectId = projectId,
            InstallationId = repository.InstallationId,
            RepoId = repository.Id,
            FullName = repository.FullName,
            CreatedAt = clock.GetUtcNow(),
        };
        db.RepoBindings.Add(binding);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { return Results.Conflict(new { error = "This repository is already bound to the project." }); }
        return Results.Created($"{http.Request.Path}/{binding.RepoId}", ToView(binding));
    }

    private static async Task<IResult> Unbind(long repoId, HttpContext http, IntegrationsDbContext db, CancellationToken ct)
    {
        var binding = await db.RepoBindings.SingleOrDefaultAsync(x => x.ProjectId == http.ResolvedProjectId()!.Value && x.RepoId == repoId, ct);
        if (binding is null) return Results.NotFound();
        db.RepoBindings.Remove(binding);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> UpdatePullRequestRules(UpdatePullRequestRulesRequest request, long repoId, HttpContext http,
        IntegrationsDbContext db, CancellationToken ct)
    {
        var binding = await db.RepoBindings.SingleOrDefaultAsync(x => x.ProjectId == http.ResolvedProjectId()!.Value && x.RepoId == repoId, ct);
        if (binding is null) return Results.NotFound();
        binding.OnPullRequestOpenedStateId = request.OnPullRequestOpenedStateId;
        binding.OnPullRequestMergedStateId = request.OnPullRequestMergedStateId;
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToView(binding));
    }

    private static async Task<IResult> ReceiveWebhook(HttpRequest request, IntegrationsDbContext db,
        IOptions<GitHubOptions> options, TimeProvider clock, CancellationToken ct)
    {
        var configured = options.Value;
        if (!configured.IsConfigured) return Results.NotFound();
        var deliveryId = request.Headers["X-GitHub-Delivery"].ToString();
        var eventType = request.Headers["X-GitHub-Event"].ToString();
        if (string.IsNullOrWhiteSpace(deliveryId) || string.IsNullOrWhiteSpace(eventType)) return Results.BadRequest();
        using var payload = new MemoryStream();
        await request.Body.CopyToAsync(payload, ct);
        if (!GitHubWebhookSignature.IsValid(request.Headers["X-Hub-Signature-256"], payload.GetBuffer().AsSpan(0, checked((int)payload.Length)), configured.WebhookSecret))
            return Results.Unauthorized();

        long? installationId = null;
        string? action = null;
        try
        {
            using var json = JsonDocument.Parse(payload.ToArray());
            if (json.RootElement.TryGetProperty("action", out var actionNode)) action = actionNode.GetString();
            if (json.RootElement.TryGetProperty("installation", out var installation) && installation.TryGetProperty("id", out var installationIdNode))
                installationId = installationIdNode.GetInt64();
        }
        catch (JsonException) { return Results.BadRequest(); }

        Guid? organizationId = null;
        if (installationId is { } id)
            organizationId = await db.GitHubInstallations.IgnoreQueryFilters().Where(x => x.InstallationId == id)
                .Select(x => (Guid?)x.OrganizationId).SingleOrDefaultAsync(ct);
        if (eventType == "installation" && installationId is { } changedInstallationId)
        {
            var status = action switch
            {
                "deleted" => GitHubInstallationStatus.Deleted,
                "suspend" or "suspended" => GitHubInstallationStatus.Suspended,
                "unsuspend" => GitHubInstallationStatus.Active,
                _ => (GitHubInstallationStatus?)null,
            };
            if (status is { } nextStatus)
            {
                var installation = await db.GitHubInstallations.IgnoreQueryFilters()
                    .SingleOrDefaultAsync(x => x.InstallationId == changedInstallationId, ct);
                if (installation is not null)
                {
                    installation.Status = nextStatus;
                    installation.UpdatedAt = clock.GetUtcNow();
                }
            }
        }
        db.GitHubDeliveries.Add(new GitHubDelivery
        {
            DeliveryId = deliveryId,
            InstallationId = installationId,
            OrganizationId = organizationId,
            EventType = eventType,
            Payload = Encoding.UTF8.GetString(payload.GetBuffer(), 0, checked((int)payload.Length)),
            ReceivedAt = clock.GetUtcNow(),
        });
        db.Set<OutboxMessage>().Add(OutboxMessage.From(new GitHubDeliveryReceived(deliveryId)));
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // GitHub redelivers on any ambiguous response. DeliveryId is the inbox PK,
            // therefore a replay has already been durably accepted and is a 200 no-op.
            return Results.Ok();
        }
        return Results.Accepted();
    }

    private static RepoBindingView ToView(RepoBinding binding) =>
        new(binding.Id, binding.RepoId, binding.InstallationId, binding.FullName,
            binding.OnPullRequestOpenedStateId, binding.OnPullRequestMergedStateId, binding.CreatedAt);
}

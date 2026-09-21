using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Aictiq.Modules.Automation;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Tenancy;
using Aictiq.Modules.Notifications.Delivery;

namespace Aictiq.Api.Realtime;

/// <summary>Authenticated project subscription endpoint. Authorization is checked per join.</summary>
[Authorize]
public sealed class ProjectHub : Hub
{
    private readonly IProjectAccess _projects;
    private readonly INotificationPresence? _presence;
    private readonly AutomationDbContext? _runs;
    private readonly IOrganizationLookup? _organizations;
    private readonly AmbientCurrentTenant? _tenant;

    /// <summary>
    /// Exactly one constructor, deliberately: SignalR activates a hub through
    /// <c>ActivatorUtilities</c>, which refuses a type whose constructors are all
    /// satisfiable rather than picking one. A second overload for tests therefore breaks
    /// every real connection — the hub throws in <c>OnConnectedAsync</c> and the browser
    /// sees only a closed socket. The optional parameters give tests the same short call
    /// without a second constructor.
    /// </summary>
    public ProjectHub(IProjectAccess projects, INotificationPresence? presence = null, AutomationDbContext? runs = null,
        IOrganizationLookup? organizations = null, AmbientCurrentTenant? tenant = null) =>
        (_projects, _presence, _runs, _organizations, _tenant) = (projects, presence, runs, organizations, tenant);
    public const string Path = "/hubs/projects";
    public static string Group(Guid projectId) => $"project:{projectId}";
    public static string UserGroup(string userId) => $"user:{userId}";
    /// <summary>The live log of one run; its subscribers passed the operator-gated log reads.</summary>
    public static string RunGroup(Guid runId) => $"run:{runId}";

    public override async Task OnConnectedAsync()
    {
        var userId = Context.UserIdentifier ?? Context.User?.FindFirst("sub")?.Value;
        if (!string.IsNullOrWhiteSpace(userId))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, UserGroup(userId), Context.ConnectionAborted);
            if (_presence is not null) await _presence.SeenAsync(userId, Context.ConnectionAborted);
        }
        await base.OnConnectedAsync();
    }

    /// <param name="organizationSlug">
    /// The organization the page is in. Optional only in the sense that null is accepted: a
    /// project key is unique per organization, so without it someone who can see two
    /// projects with the same key cannot join either.
    /// </param>
    public async Task JoinProject(string projectKey, string? organizationSlug = null)
    {
        var principal = Context.User;
        var userId = Context.UserIdentifier ?? principal?.FindFirst("sub")?.Value;
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(projectKey))
            throw new HubException("Project not found.");

        // The hub is reachable with a personal access token too, and must not be a way
        // around what that token was narrowed to: a token without `read` receives nothing,
        // and a token bound to one organization cannot subscribe to another's projects.
        var scopes = principal?.FindAll(PrincipalClaims.Scope).Select(claim => claim.Value).ToArray() ?? [];
        if (!ScopeRequirements.IsSatisfiedBy(scopes, Scopes.Read))
            throw new HubException("Project not found.");
        Guid? boundOrganization = Guid.TryParse(principal?.FindFirst(PrincipalClaims.Organization)?.Value, out var bound) ? bound : null;

        var project = await _projects.FindVisibleProjectAsync(userId, projectKey, boundOrganization, organizationSlug, Context.ConnectionAborted);
        if (project is null)
            throw new HubException("Project not found.");

        await Groups.AddToGroupAsync(Context.ConnectionId, Group(project.Id), Context.ConnectionAborted);
    }

    public Task LeaveProject(string projectId) =>
        Guid.TryParse(projectId, out var id)
            ? Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(id), Context.ConnectionAborted)
            : Task.CompletedTask;

    /// <summary>
    /// Subscribes to one run's live log — raw harness output, so the audience is the one
    /// <c>GET /orgs/{slug}/runs/{id}/log</c> admits: a member who can see the run's project
    /// <em>and</em> operates the factory. A hub call has no route to resolve a tenant from,
    /// so the organization comes from the slug (or a bound token's claim) and the run is
    /// then read inside it, through the ordinary tenant filter and RLS — never past them.
    /// Every refusal is the same "not found", so trying teaches nothing.
    /// </summary>
    public async Task JoinRun(string runId, string? organizationSlug = null)
    {
        var principal = Context.User;
        var userId = Context.UserIdentifier ?? principal?.FindFirst("sub")?.Value;
        if (string.IsNullOrWhiteSpace(userId) || !Guid.TryParse(runId, out var id)
            || _runs is null || _organizations is null || _tenant is null)
            throw new HubException("Run not found.");

        var scopes = principal?.FindAll(PrincipalClaims.Scope).Select(claim => claim.Value).ToArray() ?? [];
        if (!ScopeRequirements.IsSatisfiedBy(scopes, Scopes.Read))
            throw new HubException("Run not found.");
        Guid? organizationId = Guid.TryParse(principal?.FindFirst(PrincipalClaims.Organization)?.Value, out var bound) ? bound : null;
        if (!string.IsNullOrWhiteSpace(organizationSlug))
        {
            var organization = await _organizations.FindBySlugAsync(organizationSlug.Trim(), Context.ConnectionAborted);
            // A token bound to one organization cannot follow another's runs.
            if (organization is null || (organizationId is { } claimed && claimed != organization.Id))
                throw new HubException("Run not found.");
            organizationId = organization.Id;
        }
        if (organizationId is not { } orgId)
            throw new HubException("Run not found.");

        using var tenant = _tenant.Use(orgId);
        if (await _projects.GetOrgRoleAsync(userId, orgId, Context.ConnectionAborted) is null)
            throw new HubException("Run not found.");
        var projectId = await _runs.Runs.AsNoTracking().Where(r => r.Id == id)
            .Select(r => (Guid?)r.ProjectId).SingleOrDefaultAsync(Context.ConnectionAborted);
        if (projectId is null
            || await _projects.GetProjectRoleAsync(userId, projectId.Value, Context.ConnectionAborted) is null
            || !await _projects.CanOperateFactoryAsync(userId, orgId, Context.ConnectionAborted))
            throw new HubException("Run not found.");

        await Groups.AddToGroupAsync(Context.ConnectionId, RunGroup(id), Context.ConnectionAborted);
    }

    public Task LeaveRun(string runId) =>
        Guid.TryParse(runId, out var id)
            ? Groups.RemoveFromGroupAsync(Context.ConnectionId, RunGroup(id), Context.ConnectionAborted)
            : Task.CompletedTask;

    /// <summary>The client calls this with its normal heartbeat. A persisted timestamp is
    /// deliberately used because notification delivery happens in the Workers process.</summary>
    public async Task StayActive()
    {
        var userId = Context.UserIdentifier ?? Context.User?.FindFirst("sub")?.Value;
        if (!string.IsNullOrWhiteSpace(userId) && _presence is not null) await _presence.SeenAsync(userId, Context.ConnectionAborted);
    }
}

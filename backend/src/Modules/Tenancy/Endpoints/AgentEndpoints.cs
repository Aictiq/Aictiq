using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Npgsql;
using Aictiq.Modules.Tenancy.Access;
using Aictiq.Modules.Tenancy.Domain;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Tenancy;

namespace Aictiq.Modules.Tenancy.Endpoints;

/// <param name="OwnerName">
/// The person answerable for it. Shown everywhere the agent is, because "whose agent is
/// this" is the first question anyone asks about an action they did not expect.
/// </param>
public sealed record AgentView(
    string UserId, string DisplayName, string Email, string OwnerUserId, string OwnerName,
    OrgRole Role, bool IsActive, DateTimeOffset CreatedAt, DateTimeOffset? LastActiveAt,
    int TokenCount);

public sealed record CreateAgentRequest(string? DisplayName, IReadOnlyList<Guid>? ProjectIds);

public sealed record UpdateAgentRequest(string? DisplayName, bool? IsActive);

public sealed record CreateAgentTokenRequest(
    string? Name, IReadOnlyList<string>? Scopes, int? ExpiresInDays);

/// <summary>
/// Agents: bot members of an organization, owned by a person, carrying a token instead of
/// a password.
///
/// The split is the point. Identity owns what an <em>account</em> is - that is where the
/// row lives and where the token is hashed - and this module owns where it <em>belongs</em>
/// and who may act for it. So every endpoint here answers a Tenancy question first (is the
/// caller an admin of this organization, is that agent even in it) and then asks
/// <see cref="IAgentIdentities"/> to do the account part.
/// </summary>
public static class AgentEndpoints
{
    public static IEndpointRouteBuilder MapAgentEndpoints(this IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/orgs/{orgSlug}/agents")
            .WithTags("Agents")
            .RequireAuthorization();

        MapList(group);
        MapCreate(group);
        MapUpdate(group);
        MapDisable(group);
        MapTokens(group);

        return api;
    }

    private static void MapList(RouteGroupBuilder group)
    {
        group.MapGet("/", async (
            TenancyDbContext db,
            IAgentIdentities agents,
            IUserDirectory directory,
            CancellationToken cancellationToken) =>
        {
            var memberships = await db.Members
                .AsNoTracking()
                .Select(m => new { m.UserId, m.Role, m.JoinedAt })
                .ToListAsync(cancellationToken);

            // Which of this organization's members are agents is Identity's answer, and it
            // comes back for the whole roster at once rather than one lookup per row.
            var found = await agents.GetAsync([.. memberships.Select(m => m.UserId)], cancellationToken);
            if (found.Count == 0)
            {
                return Results.Ok(new List<AgentView>());
            }

            var owners = await directory.GetAsync(
                [.. found.Values.Select(a => a.OwnerUserId).Distinct()], cancellationToken);

            var views = new List<AgentView>();
            foreach (var membership in memberships.Where(m => found.ContainsKey(m.UserId)))
            {
                var agent = found[membership.UserId];
                var tokens = await agents.ListTokensAsync(agent.Id, cancellationToken);
                views.Add(ToView(agent, membership.Role, owners, tokens));
            }

            return Results.Ok(views.OrderBy(v => v.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList());
        })
        // Guests see agents for the same reason they see people: an agent is a member of
        // the organization and its work is signed with its name.
        .RequireOrgRole(OrgRole.Guest)
        .RequireScope(Scopes.Read);
    }

    private static void MapCreate(RouteGroupBuilder group)
    {
        group.MapPost("/", async (
            CreateAgentRequest request,
            string orgSlug,
            TenancyDbContext db,
            ICurrentTenant tenant,
            ICurrentUser user,
            IAgentIdentities agents,
            IUserDirectory directory,
            IPlanLimits planLimits,
            HybridCache cache,
            NpgsqlDataSource dataSource,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var organizationId = tenant.OrganizationId!.Value;

            var displayName = request.DisplayName?.Trim() ?? "";
            if (displayName.Length is 0 or > 100)
            {
                return Results.ValidationProblem(
                    new Dictionary<string, string[]>
                    {
                        ["displayName"] = ["Name it after what it does - 'claude-dev', 'release-bot'."]
                    },
                    type: ProblemTypes.Validation);
            }

            // An agent takes a seat like anyone else: it is a member of the organization,
            // and pretending otherwise would make seats a thing you can dodge by automating.
            var seat = await planLimits.CanAddAgentSeatAsync(organizationId, 1, cancellationToken);
            if (!seat.Allowed)
            {
                return Results.Problem(
                    title: "Plan limit reached.",
                    detail: seat.Reason ?? "This organization cannot add another member on its current plan.",
                    type: ProblemTypes.PlanLimit,
                    statusCode: StatusCodes.Status402PaymentRequired,
                    extensions: new Dictionary<string, object?> { ["limit"] = seat.Limit, ["upgradeUrl"] = seat.UpgradeUrl });
            }

            // Projects are resolved before the account exists, so a bad id fails before
            // anything has been created rather than leaving an agent in no project.
            var projectIds = await ResolveProjectsAsync(db, request.ProjectIds, cancellationToken);
            if (projectIds is null)
            {
                return Results.ValidationProblem(
                    new Dictionary<string, string[]> { ["projectIds"] = ["No such project."] },
                    type: ProblemTypes.Validation);
            }

            var agent = await agents.CreateAsync(orgSlug, displayName, user.UserId!, cancellationToken);

            var now = timeProvider.GetUtcNow();
            // Member, not Guest: an agent is here to do work. What it may actually reach is
            // decided by its projects and its token's scopes.
            db.Members.Add(OrganizationMember.Create(organizationId, agent.Id, OrgRole.Member, now));
            foreach (var projectId in projectIds)
            {
                db.ProjectMembers.Add(ProjectMember.Create(
                    organizationId, projectId, agent.Id, ProjectRole.Member, user.UserId!, now));
            }

            await db.SaveChangesAsync(cancellationToken);
            await TenancyCache.InvalidateAsync(cache, dataSource, organizationId, orgSlug, cancellationToken);

            var owners = await directory.GetAsync([agent.OwnerUserId], cancellationToken);
            return Results.Created($"/api/v1/orgs/{orgSlug}/agents/{agent.Id}",
                ToView(agent, OrgRole.Member, owners, []));
        })
        .RequireOrgRole(OrgRole.Admin)
        .RequireScope(Scopes.Admin);
    }

    private static void MapUpdate(RouteGroupBuilder group)
    {
        group.MapPatch("/{agentId}", async (
            string agentId,
            UpdateAgentRequest request,
            TenancyDbContext db,
            IAgentIdentities agents,
            IUserDirectory directory,
            CancellationToken cancellationToken) =>
        {
            var membership = await FindMembershipAsync(db, agentId, cancellationToken);
            if (membership is null || await agents.FindAsync(agentId, cancellationToken) is null)
            {
                return TenancyResults.NotFound();
            }

            var displayName = request.DisplayName?.Trim();
            if (displayName is { Length: 0 } or { Length: > 100 })
            {
                return Results.ValidationProblem(
                    new Dictionary<string, string[]> { ["displayName"] = ["Use 1-100 characters."] },
                    type: ProblemTypes.Validation);
            }

            await agents.UpdateAsync(agentId, displayName, request.IsActive, cancellationToken);

            var updated = (await agents.FindAsync(agentId, cancellationToken))!;
            var owners = await directory.GetAsync([updated.OwnerUserId], cancellationToken);
            var tokens = await agents.ListTokensAsync(agentId, cancellationToken);

            return Results.Ok(ToView(updated, membership.Role, owners, tokens));
        })
        .RequireOrgRole(OrgRole.Admin)
        .RequireScope(Scopes.Admin);
    }

    private static void MapDisable(RouteGroupBuilder group)
    {
        group.MapDelete("/{agentId}", async (
            string agentId,
            TenancyDbContext db,
            IAgentIdentities agents,
            CancellationToken cancellationToken) =>
        {
            var membership = await FindMembershipAsync(db, agentId, cancellationToken);
            if (membership is null || !await agents.DisableAsync(agentId, cancellationToken))
            {
                return TenancyResults.NotFound();
            }

            // Disabled and its tokens revoked - never deleted. The agent's id is written
            // into every item it touched, and removing the row would leave that history
            // pointing at nothing. Its membership stays for the same reason.
            return Results.NoContent();
        })
        .RequireOrgRole(OrgRole.Admin)
        .RequireScope(Scopes.Admin);
    }

    private static void MapTokens(RouteGroupBuilder group)
    {
        group.MapGet("/{agentId}/tokens", async (
            string agentId,
            TenancyDbContext db,
            ICurrentUser user,
            IAgentIdentities agents,
            IProjectAccess access,
            ICurrentTenant tenant,
            CancellationToken cancellationToken) =>
        {
            if (await AuthorizeForAgentAsync(db, agents, access, user, tenant, agentId, cancellationToken)
                is { } refusal)
            {
                return refusal;
            }

            return Results.Ok(await agents.ListTokensAsync(agentId, cancellationToken));
        })
        .RequireOrgRole(OrgRole.Guest)
        .RequireScope(Scopes.Read);

        group.MapPost("/{agentId}/tokens", async (
            string agentId,
            CreateAgentTokenRequest request,
            TenancyDbContext db,
            ICurrentTenant tenant,
            ICurrentUser user,
            IAgentIdentities agents,
            IProjectAccess access,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            if (await AuthorizeForAgentAsync(db, agents, access, user, tenant, agentId, cancellationToken)
                is { } refusal)
            {
                return refusal;
            }

            var errors = new Dictionary<string, string[]>();
            var name = request.Name?.Trim() ?? "";
            if (name.Length is 0 or > 60)
            {
                errors["name"] = ["Name it after where it will run - 'CI', 'workstation'."];
            }

            var scopes = (request.Scopes ?? []).Select(s => s.Trim().ToLowerInvariant()).Distinct().ToList();
            if (scopes.Except(Scopes.All, StringComparer.Ordinal).Any())
            {
                errors["scopes"] = [$"Pick from: {string.Join(", ", Scopes.All)}."];
            }

            if (request.ExpiresInDays is { } days && days is < 1 or > 3650)
            {
                errors["expiresInDays"] = ["Between 1 and 3650 days, or leave it out."];
            }

            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors, type: ProblemTypes.Validation);
            }

            var now = timeProvider.GetUtcNow();
            var issued = await agents.IssueTokenAsync(
                agentId, tenant.OrganizationId!.Value, name, scopes,
                now.AddDays(request.ExpiresInDays ?? 365), cancellationToken);

            return Results.Created($"/api/v1/orgs/{tenant.OrganizationId}/agents/{agentId}/tokens", issued);
        })
        .RequireOrgRole(OrgRole.Guest)
        .RequireScope(Scopes.Admin);

        group.MapDelete("/{agentId}/tokens/{tokenId:guid}", async (
            string agentId,
            Guid tokenId,
            TenancyDbContext db,
            ICurrentTenant tenant,
            ICurrentUser user,
            IAgentIdentities agents,
            IProjectAccess access,
            CancellationToken cancellationToken) =>
        {
            if (await AuthorizeForAgentAsync(db, agents, access, user, tenant, agentId, cancellationToken)
                is { } refusal)
            {
                return refusal;
            }

            return await agents.RevokeTokenAsync(agentId, tokenId, cancellationToken)
                ? Results.NoContent()
                : TenancyResults.NotFound();
        })
        .RequireOrgRole(OrgRole.Guest)
        .RequireScope(Scopes.Admin);
    }

    // --------------------------------------------------------------------------- helpers

    /// <summary>
    /// Its owner, or an administrator of the organization it is in. Returns the refusal to
    /// send, or null when the caller may act for this agent.
    ///
    /// The owner is included because they are the person answerable for it: making them ask
    /// an admin to rotate their own agent's token would mean the credential outlives the
    /// moment it should have been replaced.
    /// </summary>
    private static async Task<IResult?> AuthorizeForAgentAsync(
        TenancyDbContext db, IAgentIdentities agents, IProjectAccess access, ICurrentUser user,
        ICurrentTenant tenant, string agentId, CancellationToken cancellationToken)
    {
        var membership = await FindMembershipAsync(db, agentId, cancellationToken);
        var agent = membership is null ? null : await agents.FindAsync(agentId, cancellationToken);
        if (agent is null)
        {
            return TenancyResults.NotFound();
        }

        if (string.Equals(agent.OwnerUserId, user.UserId, StringComparison.Ordinal))
        {
            return null;
        }

        var role = await access.GetOrgRoleAsync(user.UserId!, tenant.OrganizationId!.Value, cancellationToken);
        return role is { } actual && actual.Satisfies(OrgRole.Admin)
            ? null
            : TenancyResults.Forbidden("Only this agent's owner or an organization admin can manage its tokens.");
    }

    /// <summary>
    /// Tenant-filtered, so an agent id from another organization simply is not found -
    /// which is also what stops an admin here reaching an agent over there.
    /// </summary>
    private static Task<OrganizationMember?> FindMembershipAsync(
        TenancyDbContext db, string agentId, CancellationToken cancellationToken) =>
        db.Members.AsNoTracking().FirstOrDefaultAsync(m => m.UserId == agentId, cancellationToken);

    /// <summary>Null when any id names something that is not a project here.</summary>
    private static async Task<List<Guid>?> ResolveProjectsAsync(
        TenancyDbContext db, IReadOnlyList<Guid>? requested, CancellationToken cancellationToken)
    {
        if (requested is null || requested.Count == 0)
        {
            return [];
        }

        var ids = requested.Distinct().ToList();
        var found = await db.Projects
            .AsNoTracking()
            .Where(p => ids.Contains(p.Id))
            .Select(p => p.Id)
            .ToListAsync(cancellationToken);

        return found.Count == ids.Count ? found : null;
    }

    private static AgentView ToView(
        AgentIdentity agent, OrgRole role, IReadOnlyDictionary<string, UserSummary> owners,
        IReadOnlyList<AgentTokenSummary> tokens) =>
        new(agent.Id,
            agent.DisplayName,
            agent.Email,
            agent.OwnerUserId,
            owners.TryGetValue(agent.OwnerUserId, out var owner) ? owner.DisplayName : "Someone who left",
            role,
            agent.IsActive,
            agent.CreatedAt,
            // What "last active" means for an agent: when one of its tokens was last used.
            tokens.Select(t => t.LastUsedAt).Where(t => t is not null).DefaultIfEmpty(null).Max(),
            tokens.Count);
}

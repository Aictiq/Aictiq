using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Aictiq.Api.Infrastructure;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Tenancy;

namespace Aictiq.IntegrationTests.Tenancy;

/// <summary>
/// A miniature host carrying the real tenant middleware and the real authorization
/// filters, with fake membership data behind them.
///
/// The full API exercises the same pipeline against real data in
/// <see cref="OrganizationsTests"/>; this fixture covers the combinations that would need
/// test-only routes to reach - project roles before projects exist, token scopes before
/// personal access tokens do. What matters here is the pipeline itself: middleware order,
/// route values, and the status code each refusal produces.
/// </summary>
public sealed class AuthorizationPipelineFixture : IAsyncDisposable
{
    public const string Acme = "acme";
    public static readonly Guid AcmeId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid GlobexId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid WebProjectId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private readonly WebApplication _app;

    public FakeAccess Access { get; } = new();

    public AuthorizationPipelineFixture()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();

        builder.Services.AddRoutingCore();
        // Only for the middleware's challenge of a caller with no identity; the principal
        // itself comes from the header middleware below.
        builder.Services.AddAuthentication(ChallengeOnly.Name)
            .AddScheme<AuthenticationSchemeOptions, ChallengeOnly>(ChallengeOnly.Name, null);
        builder.Services.AddAuthorization();
        builder.Services.AddProblemDetails();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<ICurrentUser, HttpContextCurrentUser>();
        builder.Services.AddScoped<AmbientCurrentTenant>();
        builder.Services.AddScoped<ICurrentTenant>(sp => sp.GetRequiredService<AmbientCurrentTenant>());
        builder.Services.AddSingleton<IOrganizationLookup>(new FakeOrganizations());
        builder.Services.AddSingleton<IProjectAccess>(Access);

        _app = builder.Build();

        _app.UseRouting();
        // Stands in for UseAuthentication: the tests describe the caller with headers
        // rather than minting real JWTs, which would test the token format, not this.
        _app.Use(async (context, next) =>
        {
            var identity = new ClaimsIdentity(authenticationType: "test");
            if (context.Request.Headers.TryGetValue("X-Test-User", out var userId) && userId.Count > 0)
            {
                identity.AddClaim(new Claim("sub", userId.ToString()));
            }
            if (context.Request.Headers.TryGetValue("X-Test-Org", out var org) && org.Count > 0)
            {
                identity.AddClaim(new Claim(PrincipalClaims.Organization, org.ToString()));
            }
            foreach (var scope in context.Request.Headers["X-Test-Scope"])
            {
                if (!string.IsNullOrEmpty(scope))
                {
                    identity.AddClaim(new Claim(PrincipalClaims.Scope, scope));
                }
            }

            context.User = new ClaimsPrincipal(identity);
            await next();
        });
        _app.UseTenantResolution();
        _app.UseAuthorization();

        var orgs = _app.MapGroup("/orgs/{orgSlug}");

        orgs.MapGet("/whoami", (ICurrentTenant tenant) => Results.Ok(new { org = tenant.OrganizationId }))
            .RequireOrgRole(OrgRole.Guest);

        orgs.MapGet("/admin-only", () => Results.Ok("ok")).RequireOrgRole(OrgRole.Admin);
        orgs.MapGet("/member-only", () => Results.Ok("ok")).RequireOrgRole(OrgRole.Member);
        orgs.MapPost("/factory", () => Results.Ok("ok")).RequireOrgRole(OrgRole.Guest).RequireFactoryOperator();

        orgs.MapGet("/projects/{projectKey}/read",
                (HttpContext http) => Results.Ok(new { projectId = http.ResolvedProjectId() }))
            .RequireProjectRole(ProjectRole.Guest);

        orgs.MapGet("/projects/{projectKey}/write", () => Results.Ok("ok"))
            .RequireProjectRole(ProjectRole.Member);

        orgs.MapPost("/write", () => Results.Ok("ok"))
            .RequireOrgRole(OrgRole.Member)
            .RequireScope(Scopes.Write);

        _app.Start();
    }

    public HttpClient Client() => _app.GetTestClient();

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    /// <summary>Authenticates nobody; its challenge is the stock 401.</summary>
    private sealed class ChallengeOnly(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string Name = "challenge-only";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.NoResult());
    }

    private sealed class FakeOrganizations : IOrganizationLookup
    {
        private static readonly Dictionary<string, OrganizationRef> BySlug = new()
        {
            [Acme] = new OrganizationRef(AcmeId, Acme, "Acme"),
            ["globex"] = new OrganizationRef(GlobexId, "globex", "Globex"),
        };

        public Task<OrganizationRef?> FindBySlugAsync(string slug, CancellationToken cancellationToken = default) =>
            Task.FromResult(BySlug.GetValueOrDefault(slug));

        public Task<OrganizationRef?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(BySlug.Values.FirstOrDefault(o => o.Id == id));
    }

    /// <summary>Membership the tests set up per case.</summary>
    public sealed class FakeAccess : IProjectAccess
    {
        public Dictionary<(string UserId, Guid OrgId), OrgRole> OrgRoles { get; } = [];
        public Dictionary<(Guid OrgId, string Key), Guid> Projects { get; } = [];

        /// <summary>Project ids the fixture should report as archived, for the writable filter.</summary>
        public HashSet<Guid> Archived { get; } = [];
        public Dictionary<(string UserId, Guid ProjectId), ProjectRole> ProjectRoles { get; } = [];

        /// <summary>Members whose stored factory flag is off. Everyone else follows the role rule.</summary>
        public HashSet<(string UserId, Guid OrgId)> NotOperators { get; } = [];

        public Task<bool> CanOperateFactoryAsync(
            string userId, Guid organizationId, CancellationToken cancellationToken = default) =>
            Task.FromResult(OrgRoles.TryGetValue((userId, organizationId), out var role)
                && Aictiq.Modules.Tenancy.Domain.MembershipRules.CanOperateFactory(
                    role, !NotOperators.Contains((userId, organizationId))));

        public Task<OrgRole?> GetOrgRoleAsync(
            string userId, Guid organizationId, CancellationToken cancellationToken = default) =>
            Task.FromResult(
                OrgRoles.TryGetValue((userId, organizationId), out var role) ? role : (OrgRole?)null);

        public Task<ProjectRef?> FindProjectAsync(
            Guid organizationId, string projectKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(
                Projects.TryGetValue((organizationId, projectKey), out var id)
                    ? new ProjectRef(id, projectKey, projectKey, Archived.Contains(id))
                    : null);

        public Task<ProjectRef?> FindVisibleProjectAsync(
            string userId, string projectKey, Guid? organizationId = null, string? organizationSlug = null,
            CancellationToken cancellationToken = default)
        {
            var matches = Projects.Where(entry => entry.Key.Key == projectKey
                && (organizationId is null || entry.Key.OrgId == organizationId)
                && ProjectRoles.ContainsKey((userId, entry.Value))).ToList();
            return Task.FromResult<ProjectRef?>(matches.Count == 1
                ? new ProjectRef(matches[0].Value, projectKey, projectKey, Archived.Contains(matches[0].Value))
                : null);
        }

        public Task<ProjectRole?> GetProjectRoleAsync(
            string userId, Guid projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult(
                ProjectRoles.TryGetValue((userId, projectId), out var role) ? role : (ProjectRole?)null);

        public Task<IReadOnlyList<string>> ListProjectMemberIdsAsync(
            Guid projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(
                [.. ProjectRoles.Where(kv => kv.Key.ProjectId == projectId).Select(kv => kv.Key.UserId)]);

        public Task<IReadOnlyList<Guid>> ListVisibleProjectIdsAsync(
            string userId, Guid organizationId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Guid>>(
                [.. ProjectRoles.Where(kv => kv.Key.UserId == userId).Select(kv => kv.Key.ProjectId).Distinct()]);
    }
}

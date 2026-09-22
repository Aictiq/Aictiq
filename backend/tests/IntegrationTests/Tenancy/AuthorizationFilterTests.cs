using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Authorization;

namespace Aictiq.IntegrationTests.Tenancy;

/// <summary>
/// What each refusal looks like from outside. The distinction these assert - 404 when the
/// caller cannot see the resource at all, 403 only once they can - is the difference
/// between "you may not do that" and "there is something here you may not do", and only
/// the second is safe to say across a tenant boundary.
/// </summary>
[Trait("Category", "Tenancy")]
public sealed class AuthorizationFilterTests : IAsyncLifetime
{
    private AuthorizationPipelineFixture _pipeline = null!;

    private const string Alice = "alice";
    private const string Mallory = "mallory";

    public ValueTask InitializeAsync()
    {
        _pipeline = new AuthorizationPipelineFixture();

        // Alice owns Acme and administers its WEB project. Mallory is a member of Globex
        // and of nothing in Acme.
        _pipeline.Access.OrgRoles[(Alice, AuthorizationPipelineFixture.AcmeId)] = OrgRole.Owner;
        _pipeline.Access.OrgRoles[(Mallory, AuthorizationPipelineFixture.GlobexId)] = OrgRole.Member;
        _pipeline.Access.Projects[(AuthorizationPipelineFixture.AcmeId, "WEB")] =
            AuthorizationPipelineFixture.WebProjectId;
        _pipeline.Access.ProjectRoles[(Alice, AuthorizationPipelineFixture.WebProjectId)] = ProjectRole.Admin;

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _pipeline.DisposeAsync();

    private HttpClient As(string? userId, string? boundOrg = null, params string[] scopes)
    {
        var client = _pipeline.Client();
        if (userId is not null) client.DefaultRequestHeaders.Add("X-Test-User", userId);
        if (boundOrg is not null) client.DefaultRequestHeaders.Add("X-Test-Org", boundOrg);
        foreach (var scope in scopes) client.DefaultRequestHeaders.Add("X-Test-Scope", scope);
        return client;
    }

    private static async Task AssertProblemAsync(
        HttpResponseMessage response, HttpStatusCode status, string type, CancellationToken ct)
    {
        Assert.Equal(status, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(ct);
        Assert.Equal(type, problem!.Type);
    }

    // ------------------------------------------------------------------ organizations

    [Fact]
    public async Task a_member_reaches_an_endpoint_and_the_tenant_is_established()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = As(Alice);

        var response = await client.GetAsync("/orgs/acme/whoami", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<WhoAmI>(ct);
        Assert.Equal(AuthorizationPipelineFixture.AcmeId, body!.Org);
    }

    [Fact]
    public async Task a_non_member_gets_404_not_403()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = As(Mallory);

        // Mallory is a real, signed-in user - just not of Acme. A 403 here would tell her
        // the "acme" slug is taken.
        await AssertProblemAsync(
            await client.GetAsync("/orgs/acme/whoami", ct),
            HttpStatusCode.NotFound, ProblemTypes.NotAMember, ct);
    }

    [Fact]
    public async Task an_unknown_organization_is_indistinguishable_from_one_you_cannot_see()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = As(Alice);

        var unknown = await client.GetAsync("/orgs/does-not-exist/whoami", ct);
        using var stranger = As(Mallory);
        var forbidden = await stranger.GetAsync("/orgs/acme/whoami", ct);

        // Same status and same problem type, so slug probing learns nothing.
        Assert.Equal(forbidden.StatusCode, unknown.StatusCode);
        await AssertProblemAsync(unknown, HttpStatusCode.NotFound, ProblemTypes.NotAMember, ct);
    }

    [Fact]
    public async Task an_anonymous_caller_gets_404_as_well()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = As(userId: null);

        await AssertProblemAsync(
            await client.GetAsync("/orgs/acme/whoami", ct),
            HttpStatusCode.NotFound, ProblemTypes.NotAMember, ct);
    }

    [Fact]
    public async Task a_member_whose_role_is_too_low_gets_403_because_they_can_already_see_the_org()
    {
        var ct = TestContext.Current.CancellationToken;
        _pipeline.Access.OrgRoles[("guest", AuthorizationPipelineFixture.AcmeId)] = OrgRole.Guest;
        using var client = As("guest");

        // Nothing is revealed: a guest already knows Acme exists.
        await AssertProblemAsync(
            await client.GetAsync("/orgs/acme/admin-only", ct),
            HttpStatusCode.Forbidden, ProblemTypes.InsufficientRole, ct);
    }

    [Theory]
    [InlineData(OrgRole.Owner, true, HttpStatusCode.OK)]
    [InlineData(OrgRole.Admin, true, HttpStatusCode.OK)]
    [InlineData(OrgRole.Member, true, HttpStatusCode.OK)]
    [InlineData(OrgRole.Member, false, HttpStatusCode.Forbidden)]
    [InlineData(OrgRole.Guest, true, HttpStatusCode.Forbidden)]
    // An Owner's stored false means nothing: they register the runners.
    [InlineData(OrgRole.Owner, false, HttpStatusCode.OK)]
    public async Task only_a_factory_operator_gets_past_the_factory_filter(
        OrgRole role, bool storedFlag, HttpStatusCode expected)
    {
        var ct = TestContext.Current.CancellationToken;
        _pipeline.Access.OrgRoles[("operator", AuthorizationPipelineFixture.AcmeId)] = role;
        if (!storedFlag) _pipeline.Access.NotOperators.Add(("operator", AuthorizationPipelineFixture.AcmeId));
        else _pipeline.Access.NotOperators.Remove(("operator", AuthorizationPipelineFixture.AcmeId));
        using var client = As("operator");

        var response = await client.PostAsync("/orgs/acme/factory", content: null, ct);

        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.Forbidden)
        {
            await AssertProblemAsync(response, HttpStatusCode.Forbidden, ProblemTypes.FactoryNotPermitted, ct);
        }
    }

    [Fact]
    public async Task the_factory_filter_does_not_tell_an_outsider_the_organization_exists()
    {
        var ct = TestContext.Current.CancellationToken;
        using var stranger = As(Mallory);

        await AssertProblemAsync(
            await stranger.PostAsync("/orgs/acme/factory", content: null, ct),
            HttpStatusCode.NotFound, ProblemTypes.NotAMember, ct);
    }

    [Theory]
    [InlineData(OrgRole.Owner)]
    [InlineData(OrgRole.Admin)]
    public async Task a_role_satisfies_every_requirement_at_or_below_it(OrgRole role)
    {
        var ct = TestContext.Current.CancellationToken;
        _pipeline.Access.OrgRoles[("someone", AuthorizationPipelineFixture.AcmeId)] = role;
        using var client = As("someone");

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/orgs/acme/admin-only", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/orgs/acme/member-only", ct)).StatusCode);
    }

    /// <summary>
    /// A token bound to one organization must not work against another, even when its
    /// owner belongs to both - binding it is the only thing that limits a leaked token's
    /// blast radius to one tenant.
    /// </summary>
    [Fact]
    public async Task a_token_bound_to_another_organization_cannot_be_used_here()
    {
        var ct = TestContext.Current.CancellationToken;
        _pipeline.Access.OrgRoles[(Alice, AuthorizationPipelineFixture.GlobexId)] = OrgRole.Member;

        using var bound = As(Alice, boundOrg: AuthorizationPipelineFixture.GlobexId.ToString());

        await AssertProblemAsync(
            await bound.GetAsync("/orgs/acme/whoami", ct),
            HttpStatusCode.NotFound, ProblemTypes.NotAMember, ct);

        // The same token works against the organization it is bound to.
        Assert.Equal(HttpStatusCode.OK, (await bound.GetAsync("/orgs/globex/whoami", ct)).StatusCode);
    }

    // ------------------------------------------------------------------ projects

    [Fact]
    public async Task a_project_member_reaches_the_endpoint_and_the_project_is_already_resolved()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = As(Alice);

        var response = await client.GetAsync("/orgs/acme/projects/WEB/read", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ProjectBody>(ct);
        // The filter looked the project up; the endpoint must not have to do it again.
        Assert.Equal(AuthorizationPipelineFixture.WebProjectId, body!.ProjectId);
    }

    [Fact]
    public async Task an_unknown_project_key_is_404()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = As(Alice);

        await AssertProblemAsync(
            await client.GetAsync("/orgs/acme/projects/NOPE/read", ct),
            HttpStatusCode.NotFound, ProblemTypes.NotAMember, ct);
    }

    [Fact]
    public async Task an_org_member_who_is_not_on_the_project_gets_404_not_403()
    {
        var ct = TestContext.Current.CancellationToken;
        _pipeline.Access.OrgRoles[("bob", AuthorizationPipelineFixture.AcmeId)] = OrgRole.Member;
        using var client = As("bob");

        // Bob can see the organization, but a private project he is not on must not even
        // confirm its key is in use.
        await AssertProblemAsync(
            await client.GetAsync("/orgs/acme/projects/WEB/read", ct),
            HttpStatusCode.NotFound, ProblemTypes.NotAMember, ct);
    }

    [Fact]
    public async Task a_project_guest_can_read_but_not_write()
    {
        var ct = TestContext.Current.CancellationToken;
        _pipeline.Access.OrgRoles[("carol", AuthorizationPipelineFixture.AcmeId)] = OrgRole.Member;
        _pipeline.Access.ProjectRoles[("carol", AuthorizationPipelineFixture.WebProjectId)] = ProjectRole.Guest;
        using var client = As("carol");

        Assert.Equal(
            HttpStatusCode.OK,
            (await client.GetAsync("/orgs/acme/projects/WEB/read", ct)).StatusCode);

        await AssertProblemAsync(
            await client.GetAsync("/orgs/acme/projects/WEB/write", ct),
            HttpStatusCode.Forbidden, ProblemTypes.InsufficientRole, ct);
    }

    // ------------------------------------------------------------------ token scopes

    [Fact]
    public async Task a_session_has_no_scopes_and_is_therefore_unscoped()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = As(Alice);

        // Scopes narrow a token below its owner's permissions; they never grant. A browser
        // session carries none and must not be treated as permitted nothing.
        Assert.Equal(
            HttpStatusCode.OK,
            (await client.PostAsync("/orgs/acme/write", content: null, ct)).StatusCode);
    }

    [Fact]
    public async Task a_read_only_token_cannot_write()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = As(Alice, boundOrg: null, Scopes.Read);

        await AssertProblemAsync(
            await client.PostAsync("/orgs/acme/write", content: null, ct),
            HttpStatusCode.Forbidden, ProblemTypes.InsufficientScope, ct);
    }

    [Fact]
    public async Task a_write_token_can_write()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = As(Alice, boundOrg: null, Scopes.Read, Scopes.Write);

        Assert.Equal(
            HttpStatusCode.OK,
            (await client.PostAsync("/orgs/acme/write", content: null, ct)).StatusCode);
    }

    [Fact]
    public async Task the_admin_scope_implies_the_others()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = As(Alice, boundOrg: null, Scopes.Admin);

        Assert.Equal(
            HttpStatusCode.OK,
            (await client.PostAsync("/orgs/acme/write", content: null, ct)).StatusCode);
    }

    /// <summary>
    /// Membership is checked before the scope is: a stranger with a perfectly valid
    /// write-scoped token must still be told nothing about this organization.
    /// </summary>
    [Fact]
    public async Task membership_is_checked_before_scope()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = As(Mallory, boundOrg: null, Scopes.Write);

        await AssertProblemAsync(
            await client.PostAsync("/orgs/acme/write", content: null, ct),
            HttpStatusCode.NotFound, ProblemTypes.NotAMember, ct);
    }

    private sealed record WhoAmI(Guid? Org);
    private sealed record ProjectBody(Guid? ProjectId);
}

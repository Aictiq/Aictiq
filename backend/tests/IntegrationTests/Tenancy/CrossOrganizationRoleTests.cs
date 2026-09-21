using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.Tenancy.Domain;
using Aictiq.Modules.Tenancy.Endpoints;
using Aictiq.Modules.WorkItems.Domain;
using Aictiq.Modules.WorkItems.Endpoints;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Paging;

namespace Aictiq.IntegrationTests.Tenancy;

/// <summary>
/// One person, several organizations, a different standing in each.
///
/// The common shape in practice: someone owns their own organization and is a
/// *stakeholder* — a Member with the factory flag off — in a customer's, where
/// they are named on one private project and nothing else. Every answer the API gives
/// must be about the organization in the URL and nothing else: the role, the factory
/// flag, the project ladder and the roster all have to switch when the slug does.
///
/// The interleaving is the point of most of these. Membership, the factory flag and
/// project roles are all cached (<c>TenancyCache</c>), keyed per organization; a key that
/// forgot the organization would pass any test that touched one organization at a time
/// and hand an owner's answer to a stakeholder in production.
/// </summary>
[Trait("Category", "Tenancy")]
[Collection("postgres")]
public sealed class CrossOrganizationRoleTests(PostgresFixture postgres, GarageFixture garage) : IAsyncLifetime
{
    private const string Hers = "dana-co";
    private const string Theirs = "acme";
    private const string Third = "globex";

    private ApiTestContext _context = null!;

    /// <summary>Alice owns <c>acme</c> and <c>globex</c>; Dana owns <c>dana-co</c>.</summary>
    private HttpClient _alice = null!;
    private HttpClient _dana = null!;
    private string _aliceId = null!;
    private string _danaId = null!;

    private ProjectView _acmePrivate = null!;
    private ProjectView _acmeOpen = null!;
    private ProjectView _globexOpen = null!;
    private ProjectView _hers = null!;

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        _context = await ApiTestContext.CreateAsync(postgres, garage, "cross_org_roles");

        var alice = await _context.RegisterAsync("alice@test.local", "Alice", "Anderson");
        _alice = _context.ClientFor(alice);
        _aliceId = alice.User.Id;

        var dana = await _context.RegisterAsync("dana@test.local", "Dana", "Dell");
        _dana = _context.ClientFor(dana);
        _danaId = dana.User.Id;

        await CreateOrganizationAsync(_alice, "Acme", Theirs, ct);
        await CreateOrganizationAsync(_alice, "Globex", Third, ct);
        // Dana already belongs to nothing and starts her own: owning one organization is
        // not conditional on belonging to no other.
        await CreateOrganizationAsync(_dana, "Dana & Co", Hers, ct);

        _hers = await CreateProjectAsync(_dana, Hers, "DANA", ProjectVisibility.Organization, ct);
        _acmePrivate = await CreateProjectAsync(_alice, Theirs, "SEC", ProjectVisibility.Private, ct);
        _acmeOpen = await CreateProjectAsync(_alice, Theirs, "PROD", ProjectVisibility.Organization, ct);
        _globexOpen = await CreateProjectAsync(_alice, Third, "GLX", ProjectVisibility.Organization, ct);

        // A stakeholder in someone else's organization: Member, no factory, named on the
        // one private project she was brought in for.
        await InviteAsync(Theirs, OrgRole.Member, canOperateFactory: false,
            projectId: _acmePrivate.Id, projectRole: ProjectRole.Member, ct);
        // …and a Guest in a third, to prove the Guest ceiling is per organization too.
        await InviteAsync(Third, OrgRole.Guest, canOperateFactory: null, projectId: null, projectRole: null, ct);
    }

    public async ValueTask DisposeAsync()
    {
        _alice.Dispose();
        _dana.Dispose();
        await _context.DisposeAsync();
    }

    // ------------------------------------------------------------------ the switcher's view

    [Fact]
    public async Task one_person_holds_a_different_role_in_each_organization_at_once()
    {
        var ct = TestContext.Current.CancellationToken;

        var mine = (await _dana.GetFromJsonAsync<List<OrganizationSummary>>(
            "/api/v1/orgs", ApiTestContext.Json, ct))!;

        Assert.Equal([Theirs, Hers, Third], mine.Select(o => o.Slug).ToArray());
        var acme = mine.Single(o => o.Slug == Theirs);
        var hers = mine.Single(o => o.Slug == Hers);
        var globex = mine.Single(o => o.Slug == Third);

        Assert.Equal(OrgRole.Owner, hers.Role);
        Assert.Equal(OrgRole.Member, acme.Role);
        Assert.Equal(OrgRole.Guest, globex.Role);
        // The switcher greys the Factory on this, per row, not once for the person.
        Assert.True(hers.CanOperateFactory);
        Assert.False(acme.CanOperateFactory);
        Assert.False(globex.CanOperateFactory);
    }

    [Fact]
    public async Task the_organization_record_answers_for_the_slug_in_the_url()
    {
        var ct = TestContext.Current.CancellationToken;

        // Deliberately alternating: a cache keyed on the person alone would answer the
        // second call with the first call's role.
        var hers = await OrganizationAsync(Hers, ct);
        var theirs = await OrganizationAsync(Theirs, ct);
        var hersAgain = await OrganizationAsync(Hers, ct);
        var theirsAgain = await OrganizationAsync(Theirs, ct);

        Assert.Equal(OrgRole.Owner, hers.Role);
        Assert.Equal(OrgRole.Owner, hersAgain.Role);
        Assert.True(hers.CanOperateFactory);
        Assert.Equal(OrgRole.Member, theirs.Role);
        Assert.Equal(OrgRole.Member, theirsAgain.Role);
        Assert.False(theirs.CanOperateFactory);
    }

    // ------------------------------------------------------------- what each role may do

    [Fact]
    public async Task owning_one_organization_grants_nothing_in_another()
    {
        var ct = TestContext.Current.CancellationToken;

        var hers = await OrganizationAsync(Hers, ct);
        var renameHers = await _dana.PatchAsJsonAsync($"/api/v1/orgs/{Hers}",
            new UpdateOrganizationRequest("Dana & Co.", null, null, null, hers.Version), ApiTestContext.Json, ct);

        var theirs = await OrganizationAsync(Theirs, ct);
        var renameTheirs = await _dana.PatchAsJsonAsync($"/api/v1/orgs/{Theirs}",
            new UpdateOrganizationRequest("Dana's Acme", null, null, null, theirs.Version), ApiTestContext.Json, ct);

        Assert.Equal(HttpStatusCode.OK, renameHers.StatusCode);
        // 403, not 404: she can see acme — she is in it — she simply may not run it.
        Assert.Equal(HttpStatusCode.Forbidden, renameTheirs.StatusCode);

        // And she cannot hand herself the role that would let her.
        var promote = await _dana.PutAsJsonAsync($"/api/v1/orgs/{Theirs}/members/{_danaId}",
            new UpdateMemberRequest(OrgRole.Owner), ApiTestContext.Json, ct);
        Assert.Equal(HttpStatusCode.Forbidden, promote.StatusCode);

        var demote = await _dana.PutAsJsonAsync($"/api/v1/orgs/{Theirs}/members/{_aliceId}",
            new UpdateMemberRequest(OrgRole.Member), ApiTestContext.Json, ct);
        Assert.Equal(HttpStatusCode.Forbidden, demote.StatusCode);
    }

    [Fact]
    public async Task the_factory_flag_is_per_membership_and_not_per_person()
    {
        var ct = TestContext.Current.CancellationToken;

        // GET /orgs/{slug}/rules is Member + factory operator, which is exactly the pair
        // under test: she is a Member of both and an operator in only one.
        var hers = await _dana.GetAsync($"/api/v1/orgs/{Hers}/rules", ct);
        var theirs = await _dana.GetAsync($"/api/v1/orgs/{Theirs}/rules", ct);
        var hersAgain = await _dana.GetAsync($"/api/v1/orgs/{Hers}/rules", ct);

        Assert.Equal(HttpStatusCode.OK, hers.StatusCode);
        Assert.Equal(HttpStatusCode.OK, hersAgain.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, theirs.StatusCode);
        Assert.Equal(ProblemTypes.FactoryNotPermitted, await ProblemTypeAsync(theirs, ct));
    }

    [Fact]
    public async Task an_owner_elsewhere_is_still_only_a_project_member_here()
    {
        var ct = TestContext.Current.CancellationToken;

        // Her own project: organization Owner, therefore project Admin implicitly.
        var hers = await ProjectAsync(_dana, Hers, _hers.Key, ct);
        // The private project she was named on: exactly what the explicit row says.
        var named = await ProjectAsync(_dana, Theirs, _acmePrivate.Key, ct);
        // Organization-visible in the same organization: a Member, no more.
        var open = await ProjectAsync(_dana, Theirs, _acmeOpen.Key, ct);
        // Organization-visible where she is a Guest: the ceiling holds.
        var guested = await ProjectAsync(_dana, Third, _globexOpen.Key, ct);

        Assert.Equal(ProjectRole.Admin, hers.Role);
        Assert.Equal(ProjectRole.Member, named.Role);
        Assert.Equal(ProjectRole.Member, open.Role);
        Assert.Equal(ProjectRole.Guest, guested.Role);
    }

    [Fact]
    public async Task a_private_project_she_is_not_named_on_stays_invisible()
    {
        var ct = TestContext.Current.CancellationToken;
        var hidden = await CreateProjectAsync(_alice, Theirs, "OPS", ProjectVisibility.Private, ct);

        var response = await _dana.GetAsync($"/api/v1/orgs/{Theirs}/projects/{hidden.Key}", ct);
        var listed = (await _dana.GetFromJsonAsync<List<ProjectView>>(
            $"/api/v1/orgs/{Theirs}/projects", ApiTestContext.Json, ct))!;

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal([_acmeOpen.Key, _acmePrivate.Key], listed.Select(p => p.Key).Order().ToArray());
    }

    [Fact]
    public async Task she_works_in_both_organizations_in_one_session()
    {
        var ct = TestContext.Current.CancellationToken;

        // Alternating writes: the tenant is established per request from the URL, so a
        // session that is an Owner in one and a stakeholder in the other never has to
        // choose which one it is "in".
        var here = await CreateItemAsync(_dana, Hers, _hers.Key, "Plan the quarter", ct);
        var there = await CreateItemAsync(_dana, Theirs, _acmePrivate.Key, "Rotate the certificate", ct);
        var hereAgain = await CreateItemAsync(_dana, Hers, _hers.Key, "Book the venue", ct);

        Assert.StartsWith($"{_hers.Key}-", here.Key, StringComparison.Ordinal);
        Assert.StartsWith($"{_acmePrivate.Key}-", there.Key, StringComparison.Ordinal);
        Assert.StartsWith($"{_hers.Key}-", hereAgain.Key, StringComparison.Ordinal);

        // A Guest may not write, even to an organization-visible project, and even though
        // she is an Owner one slug over.
        var refused = await _dana.PostAsJsonAsync(
            $"/api/v1/orgs/{Third}/projects/{_globexOpen.Key}/items/",
            NewItem("Not mine to add"), ApiTestContext.Json, ct);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    [Fact]
    public async Task an_item_key_is_only_addressable_through_its_own_organization()
    {
        var ct = TestContext.Current.CancellationToken;
        var theirs = await CreateItemAsync(_dana, Theirs, _acmePrivate.Key, "Rotate the certificate", ct);

        // Same person, same session, the other organization's slug: the tenant filter is
        // what answers, and it answers 404 rather than reaching across.
        var throughHers = await _dana.GetAsync($"/api/v1/orgs/{Hers}/items/{theirs.Key}", ct);
        var throughTheirs = await _dana.GetAsync($"/api/v1/orgs/{Theirs}/items/{theirs.Key}", ct);

        Assert.Equal(HttpStatusCode.NotFound, throughHers.StatusCode);
        Assert.Equal(HttpStatusCode.OK, throughTheirs.StatusCode);
    }

    [Fact]
    public async Task the_roster_she_sees_is_the_one_of_the_organization_she_asked_about()
    {
        var ct = TestContext.Current.CancellationToken;

        var hers = await RosterAsync(Hers, ct);
        var theirs = await RosterAsync(Theirs, ct);
        var guested = await RosterAsync(Third, ct);

        Assert.Equal([_danaId], hers.Items.Select(m => m.UserId).ToArray());
        Assert.Equal(new[] { _aliceId, _danaId }.Order().ToArray(), theirs.Items.Select(m => m.UserId).Order().ToArray());
        // A Member sees addresses; a Guest does not, in the very same session.
        Assert.All(theirs.Items, m => Assert.NotNull(m.Email));
        Assert.All(guested.Items, m => Assert.Null(m.Email));
    }

    [Fact]
    public async Task losing_a_role_in_one_organization_leaves_the_other_untouched()
    {
        var ct = TestContext.Current.CancellationToken;

        // Warm every cached answer first: this is the shape that a per-person cache key
        // or a too-broad invalidation would get wrong.
        Assert.Equal(OrgRole.Owner, (await OrganizationAsync(Hers, ct)).Role);
        Assert.Equal(OrgRole.Member, (await OrganizationAsync(Theirs, ct)).Role);

        (await _alice.DeleteAsync($"/api/v1/orgs/{Theirs}/members/{_danaId}", ct)).EnsureSuccessStatusCode();

        var gone = await _dana.GetAsync($"/api/v1/orgs/{Theirs}", ct);
        var stillHers = await OrganizationAsync(Hers, ct);

        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        Assert.Equal(OrgRole.Owner, stillHers.Role);
        Assert.True(stillHers.CanOperateFactory);
    }

    [Fact]
    public async Task being_made_an_operator_in_one_organization_does_not_travel()
    {
        var ct = TestContext.Current.CancellationToken;

        // Warm the refusal, then lift it — the cached "no" must not outlive the change,
        // and the *other* organization's answer must not move with it.
        Assert.Equal(HttpStatusCode.Forbidden, (await _dana.GetAsync($"/api/v1/orgs/{Theirs}/rules", ct)).StatusCode);

        var updated = await _alice.PutAsJsonAsync($"/api/v1/orgs/{Theirs}/members/{_danaId}",
            new UpdateMemberRequest(null, true), ApiTestContext.Json, ct);
        updated.EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.OK, (await _dana.GetAsync($"/api/v1/orgs/{Theirs}/rules", ct)).StatusCode);
        // Still a Member there, still an Owner here.
        Assert.Equal(OrgRole.Member, (await OrganizationAsync(Theirs, ct)).Role);
        Assert.Equal(OrgRole.Owner, (await OrganizationAsync(Hers, ct)).Role);

        // A Guest's column is never a choice, whatever she is elsewhere.
        var guestOperator = await _alice.PutAsJsonAsync($"/api/v1/orgs/{Third}/members/{_danaId}",
            new UpdateMemberRequest(null, true), ApiTestContext.Json, ct);
        Assert.Equal(HttpStatusCode.BadRequest, guestOperator.StatusCode);
    }

    // --------------------------------------------------------------------------- helpers

    private async Task<OrganizationView> CreateOrganizationAsync(
        HttpClient client, string name, string slug, CancellationToken ct)
    {
        var response = await client.PostAsJsonAsync("/api/v1/orgs",
            new CreateOrganizationRequest(name, slug, null, null), ApiTestContext.Json, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<OrganizationView>(ApiTestContext.Json, ct))!;
    }

    private async Task<ProjectView> CreateProjectAsync(
        HttpClient client, string slug, string key, ProjectVisibility visibility, CancellationToken ct)
    {
        var response = await client.PostAsJsonAsync($"/api/v1/orgs/{slug}/projects",
            new CreateProjectRequest($"Project {key}", key, null, visibility, null, null),
            ApiTestContext.Json, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProjectView>(ApiTestContext.Json, ct))!;
    }

    /// <summary>Dana joins the way anyone joins: a link, accepted by the account she already has.</summary>
    private async Task InviteAsync(string slug, OrgRole role, bool? canOperateFactory,
        Guid? projectId, ProjectRole? projectRole, CancellationToken ct)
    {
        var invited = await _alice.PostAsJsonAsync($"/api/v1/orgs/{slug}/invitations",
            new CreateInvitationRequest("dana@test.local", role, projectId, projectRole, canOperateFactory),
            ApiTestContext.Json, ct);
        invited.EnsureSuccessStatusCode();
        var link = (await invited.Content.ReadFromJsonAsync<InvitationLink>(ApiTestContext.Json, ct))!;
        (await _dana.PostAsync($"/api/v1/invitations/{link.Token}/accept", null, ct)).EnsureSuccessStatusCode();
    }

    private async Task<OrganizationView> OrganizationAsync(string slug, CancellationToken ct) =>
        (await _dana.GetFromJsonAsync<OrganizationView>($"/api/v1/orgs/{slug}", ApiTestContext.Json, ct))!;

    private async Task<ProjectView> ProjectAsync(
        HttpClient client, string slug, string key, CancellationToken ct) =>
        (await client.GetFromJsonAsync<ProjectView>(
            $"/api/v1/orgs/{slug}/projects/{key}", ApiTestContext.Json, ct))!;

    private async Task<PagedResult<MemberView>> RosterAsync(string slug, CancellationToken ct) =>
        (await _dana.GetFromJsonAsync<PagedResult<MemberView>>(
            $"/api/v1/orgs/{slug}/members", ApiTestContext.Json, ct))!;

    private static CreateWorkItemRequest NewItem(string title) =>
        new(WorkItemType.Story, title, null, null, null, null, null, null, null, null, null, null, null, null);

    private async Task<WorkItemView> CreateItemAsync(
        HttpClient client, string slug, string projectKey, string title, CancellationToken ct)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/orgs/{slug}/projects/{projectKey}/items/", NewItem(title), ApiTestContext.Json, ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(ct));
        return (await response.Content.ReadFromJsonAsync<WorkItemView>(ApiTestContext.Json, ct))!;
    }

    private static async Task<string> ProblemTypeAsync(HttpResponseMessage response, CancellationToken ct) =>
        (await response.Content.ReadFromJsonAsync<ProblemDetails>(ct))!.Type!;
}

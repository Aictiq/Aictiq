using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.Tenancy.Domain;
using Aictiq.Modules.Tenancy.Endpoints;
using Aictiq.SharedKernel.Authorization;

namespace Aictiq.IntegrationTests.Tenancy;

/// <summary>
/// Projects over HTTP: who may create them, who can see them, and what archiving closes.
///
/// The interesting cases are all about the two role ladders meeting — the matrix itself is
/// proved without a database in <see cref="ProjectAccessRulesTests"/>; what is under test
/// here is that the endpoints, the query filter and the cache all agree with it.
/// </summary>
[Trait("Category", "Tenancy")]
[Collection("postgres")]
public sealed class ProjectsTests(PostgresFixture postgres, GarageFixture garage) : IAsyncLifetime
{
    private ApiTestContext _context = null!;
    private const string Acme = "acme";
    private Guid _acmeId;

    private readonly Dictionary<OrgRole, HttpClient> _clients = [];
    private readonly Dictionary<OrgRole, string> _ids = [];
    private HttpClient _outsider = null!;

    public async ValueTask InitializeAsync()
    {
        _context = await ApiTestContext.CreateAsync(postgres, garage, "projects");

        var alice = await _context.RegisterAsync("alice@test.local", "Alice", "Anderson");
        _clients[OrgRole.Owner] = _context.ClientFor(alice);
        _ids[OrgRole.Owner] = alice.User.Id;

        var response = await _clients[OrgRole.Owner].PostAsJsonAsync("/api/v1/orgs",
            new CreateOrganizationRequest("Acme", Acme, null, null), TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        _acmeId = (await response.Content.ReadFromJsonAsync<OrganizationView>(
            ApiTestContext.Json, TestContext.Current.CancellationToken))!.Id;

        foreach (var (role, email, first, last) in new[]
        {
            (OrgRole.Admin, "bob@test.local", "Bob", "Brooks"),
            (OrgRole.Member, "carol@test.local", "Carol", "Carter"),
            (OrgRole.Guest, "dave@test.local", "Dave", "Doyle"),
        })
        {
            var auth = await _context.RegisterAsync(email, first, last);
            _clients[role] = _context.ClientFor(auth);
            _ids[role] = auth.User.Id;
            await AddMemberAsync(auth.User.Id, role);
        }

        _outsider = _context.ClientFor(await _context.RegisterAsync("mallory@test.local", "Mallory", "Mahoney"));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients.Values)
        {
            client.Dispose();
        }
        _outsider.Dispose();
        await _context.DisposeAsync();
    }

    // -------------------------------------------------------------------------- creating

    [Fact]
    public async Task creating_a_project_derives_a_key_and_names_the_creator_its_admin()
    {
        var ct = TestContext.Current.CancellationToken;

        var project = await CreateAsync(_clients[OrgRole.Owner], "Acme Website", ct: ct);

        // Initials, because that is what a person would have typed: AW, not ACMEWEB.
        Assert.Equal("AW", project.Key);
        Assert.Equal(ProjectRole.Admin, project.Role);
        Assert.Equal(ProjectVisibility.Organization, project.Visibility);
        Assert.False(project.IsArchived);
    }

    [Fact]
    public async Task a_derived_key_that_is_taken_gets_a_digit_rather_than_an_error()
    {
        var ct = TestContext.Current.CancellationToken;

        var first = await CreateAsync(_clients[OrgRole.Owner], "Acme Website", ct: ct);
        var second = await CreateAsync(_clients[OrgRole.Owner], "Andes Wine", ct: ct);

        Assert.Equal("AW", first.Key);
        Assert.Equal("AW2", second.Key);
    }

    [Theory]
    [InlineData("a")]          // too short, and lower case
    [InlineData("1ABC")]       // must start with a letter
    [InlineData("HAS SPACE")]
    [InlineData("TOOLONGAKEY")]
    public async Task a_malformed_key_is_refused_before_it_reaches_the_database(string key)
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await PostAsync(_clients[OrgRole.Owner],
            new CreateProjectRequest("Whatever", key, null, null, null, null), ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task a_key_typed_in_lower_case_is_normalised_rather_than_rejected()
    {
        var ct = TestContext.Current.CancellationToken;

        var project = await CreateAsync(_clients[OrgRole.Owner], "Website", "web", ct);

        Assert.Equal("WEB", project.Key);
    }

    [Fact]
    public async Task two_projects_here_cannot_share_a_key_or_a_name()
    {
        var ct = TestContext.Current.CancellationToken;
        await CreateAsync(_clients[OrgRole.Owner], "Website", "WEB", ct);

        var sameKey = await PostAsync(_clients[OrgRole.Owner],
            new CreateProjectRequest("Something else", "WEB", null, null, null, null), ct);
        // Case-insensitively: "Website" and "website" would be indistinguishable in a list.
        var sameName = await PostAsync(_clients[OrgRole.Owner],
            new CreateProjectRequest("WEBSITE", "WS", null, null, null, null), ct);

        Assert.Equal(HttpStatusCode.BadRequest, sameKey.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, sameName.StatusCode);
    }

    [Fact]
    public async Task a_member_can_create_a_project_unless_the_organization_says_otherwise()
    {
        var ct = TestContext.Current.CancellationToken;

        var allowed = await PostAsync(_clients[OrgRole.Member],
            new CreateProjectRequest("Member Project", null, null, null, null, null), ct);
        Assert.Equal(HttpStatusCode.Created, allowed.StatusCode);

        await SetMembersCanCreateProjectsAsync(false, ct);

        var refused = await PostAsync(_clients[OrgRole.Member],
            new CreateProjectRequest("Second Member Project", null, null, null, null, null), ct);
        // The setting binds members only — the people who set it are never subject to it.
        var byAdmin = await PostAsync(_clients[OrgRole.Admin],
            new CreateProjectRequest("Admin Project", null, null, null, null, null), ct);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(HttpStatusCode.Created, byAdmin.StatusCode);
    }

    [Fact]
    public async Task a_guest_cannot_create_a_project_and_an_outsider_sees_nothing_at_all()
    {
        var ct = TestContext.Current.CancellationToken;

        var byGuest = await PostAsync(_clients[OrgRole.Guest],
            new CreateProjectRequest("Guest Project", null, null, null, null, null), ct);
        var byStranger = await PostAsync(_outsider,
            new CreateProjectRequest("Stranger Project", null, null, null, null, null), ct);

        Assert.Equal(HttpStatusCode.Forbidden, byGuest.StatusCode);
        // 404, not 403: a stranger must not learn that this organization exists.
        Assert.Equal(HttpStatusCode.NotFound, byStranger.StatusCode);
    }

    // --------------------------------------------------------------------- seeing them

    [Fact]
    public async Task an_organization_visible_project_is_listed_for_everyone_at_their_own_rank()
    {
        var ct = TestContext.Current.CancellationToken;
        await CreateAsync(_clients[OrgRole.Owner], "Website", "WEB", ct);

        var asAdmin = Assert.Single(await ListAsync(_clients[OrgRole.Admin], ct));
        var asMember = Assert.Single(await ListAsync(_clients[OrgRole.Member], ct));
        var asGuest = Assert.Single(await ListAsync(_clients[OrgRole.Guest], ct));

        Assert.Equal(ProjectRole.Admin, asAdmin.Role);
        Assert.Equal(ProjectRole.Member, asMember.Role);
        Assert.Equal(ProjectRole.Guest, asGuest.Role);
    }

    [Fact]
    public async Task a_private_project_is_invisible_to_everyone_who_is_not_on_it()
    {
        var ct = TestContext.Current.CancellationToken;
        await CreateAsync(_clients[OrgRole.Owner], "Secret", "SEC", ct, ProjectVisibility.Private);

        // The organization's admins are implicitly its project admins, so they still see it.
        Assert.Single(await ListAsync(_clients[OrgRole.Admin], ct));
        Assert.Empty(await ListAsync(_clients[OrgRole.Member], ct));
        Assert.Empty(await ListAsync(_clients[OrgRole.Guest], ct));

        var byUrl = await _clients[OrgRole.Member].GetAsync($"/api/v1/orgs/{Acme}/projects/SEC", ct);
        var imaginary = await _clients[OrgRole.Member].GetAsync($"/api/v1/orgs/{Acme}/projects/NOPE", ct);

        // Identical answers: a 403 on the first would confirm that SEC exists.
        Assert.Equal(HttpStatusCode.NotFound, byUrl.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, imaginary.StatusCode);
    }

    [Fact]
    public async Task being_added_to_a_private_project_is_what_opens_it()
    {
        var ct = TestContext.Current.CancellationToken;
        await CreateAsync(_clients[OrgRole.Owner], "Secret", "SEC", ct, ProjectVisibility.Private);

        await SetProjectRoleAsync("SEC", _ids[OrgRole.Member], ProjectRole.Member, ct);

        var project = Assert.Single(await ListAsync(_clients[OrgRole.Member], ct));
        Assert.Equal(ProjectRole.Member, project.Role);
        // …and the cached "not a member" answer did not outlive the change.
        Assert.Equal(HttpStatusCode.OK,
            (await _clients[OrgRole.Member].GetAsync($"/api/v1/orgs/{Acme}/projects/SEC", ct)).StatusCode);
    }

    [Fact]
    public async Task an_organization_guest_stays_a_guest_however_they_are_added()
    {
        var ct = TestContext.Current.CancellationToken;
        await CreateAsync(_clients[OrgRole.Owner], "Website", "WEB", ct);

        await SetProjectRoleAsync("WEB", _ids[OrgRole.Guest], ProjectRole.Admin, ct);

        // The ceiling is the whole point of the Guest role; it must not be liftable one
        // project at a time.
        Assert.Equal(ProjectRole.Guest, Assert.Single(await ListAsync(_clients[OrgRole.Guest], ct)).Role);
        var patch = await PatchAsync(_clients[OrgRole.Guest], "WEB", new UpdateProjectRequest(
            "Renamed", null, null, null, null, 0), ct);
        Assert.Equal(HttpStatusCode.Forbidden, patch.StatusCode);
    }

    [Fact]
    public async Task an_explicit_guest_membership_does_not_demote_an_organization_admin()
    {
        var ct = TestContext.Current.CancellationToken;
        var project = await CreateAsync(_clients[OrgRole.Owner], "Website", "WEB", ct);

        await SetProjectRoleAsync("WEB", _ids[OrgRole.Admin], ProjectRole.Guest, ct);

        // Explicit roles add; they never subtract.
        var seen = await GetAsync(_clients[OrgRole.Admin], "WEB", ct);
        Assert.Equal(ProjectRole.Admin, seen.Role);
        Assert.Equal(project.Id, seen.Id);
    }

    // -------------------------------------------------------------------------- editing

    [Fact]
    public async Task renaming_keeps_the_key_and_the_key_cannot_be_changed()
    {
        var ct = TestContext.Current.CancellationToken;
        var project = await CreateAsync(_clients[OrgRole.Owner], "Website", "WEB", ct);

        var response = await PatchAsync(_clients[OrgRole.Owner], "WEB",
            new UpdateProjectRequest("Marketing Site", "Now with a description", null, null, null, project.Version),
            ct);
        response.EnsureSuccessStatusCode();
        var updated = (await response.Content.ReadFromJsonAsync<ProjectView>(ApiTestContext.Json, ct))!;

        // The key is in every item id the team will ever quote, so there is no field for it.
        Assert.Equal("WEB", updated.Key);
        Assert.Equal("Marketing Site", updated.Name);
        Assert.Equal("Now with a description", updated.Description);
    }

    [Fact]
    public async Task an_empty_string_clears_a_field_and_a_missing_one_leaves_it_alone()
    {
        var ct = TestContext.Current.CancellationToken;
        var project = await CreateAsync(_clients[OrgRole.Owner], "Website", "WEB", ct);

        var described = await PatchApplyAsync(_clients[OrgRole.Owner], "WEB",
            new UpdateProjectRequest(null, "A description", null, "🌐", "#4F46E5", project.Version), ct);
        var renamed = await PatchApplyAsync(_clients[OrgRole.Owner], "WEB",
            new UpdateProjectRequest("Renamed", null, null, null, null, described.Version), ct);
        var cleared = await PatchApplyAsync(_clients[OrgRole.Owner], "WEB",
            new UpdateProjectRequest(null, "", null, "", "", renamed.Version), ct);

        Assert.Equal("A description", described.Description);
        Assert.Equal("🌐", described.Icon);
        // Normalised, so two spellings of one colour do not read as two colours.
        Assert.Equal("#4f46e5", described.Color);

        Assert.Equal("A description", renamed.Description);
        Assert.Null(cleared.Description);
        Assert.Null(cleared.Icon);
        Assert.Null(cleared.Color);
    }

    [Fact]
    public async Task a_stale_version_loses_to_whoever_saved_first()
    {
        var ct = TestContext.Current.CancellationToken;
        var project = await CreateAsync(_clients[OrgRole.Owner], "Website", "WEB", ct);

        var first = await PatchAsync(_clients[OrgRole.Owner], "WEB",
            new UpdateProjectRequest("First", null, null, null, null, project.Version), ct);
        var second = await PatchAsync(_clients[OrgRole.Admin], "WEB",
            new UpdateProjectRequest("Second", null, null, null, null, project.Version), ct);

        first.EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task a_member_cannot_edit_a_project_they_are_only_a_member_of()
    {
        var ct = TestContext.Current.CancellationToken;
        var project = await CreateAsync(_clients[OrgRole.Owner], "Website", "WEB", ct);

        var response = await PatchAsync(_clients[OrgRole.Member], "WEB",
            new UpdateProjectRequest("Renamed", null, null, null, null, project.Version), ct);

        // Visible, so 403 rather than 404: they can already see it exists.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ------------------------------------------------------------------------ archiving

    [Fact]
    public async Task an_archived_project_stays_readable_and_refuses_every_write()
    {
        var ct = TestContext.Current.CancellationToken;
        var project = await CreateAsync(_clients[OrgRole.Owner], "Website", "WEB", ct);

        var archived = await ArchiveAsync("WEB", archive: true, ct);
        Assert.True(archived.IsArchived);

        // Still there, still readable: archiving keeps the team's history, it does not
        // hide it.
        Assert.Equal(HttpStatusCode.OK,
            (await _clients[OrgRole.Owner].GetAsync($"/api/v1/orgs/{Acme}/projects/WEB", ct)).StatusCode);

        var patch = await PatchAsync(_clients[OrgRole.Owner], "WEB",
            new UpdateProjectRequest("Renamed", null, null, null, null, archived.Version), ct);
        var addMember = await _clients[OrgRole.Owner].PutAsJsonAsync(
            $"/api/v1/orgs/{Acme}/projects/WEB/members/{_ids[OrgRole.Member]}",
            new UpdateProjectMemberRequest(ProjectRole.Member), ApiTestContext.Json, ct);

        Assert.Equal(HttpStatusCode.Conflict, patch.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, addMember.StatusCode);

        var problem = await patch.Content.ReadFromJsonAsync<ProblemDetails>(ct);
        Assert.Equal("https://aictiq.com/problems/project-archived", problem!.Type);
        Assert.Equal(project.Id, archived.Id);
    }

    [Fact]
    public async Task un_archiving_makes_the_same_write_succeed()
    {
        var ct = TestContext.Current.CancellationToken;
        await CreateAsync(_clients[OrgRole.Owner], "Website", "WEB", ct);
        await ArchiveAsync("WEB", archive: true, ct);

        var restored = await ArchiveAsync("WEB", archive: false, ct);
        var patch = await PatchAsync(_clients[OrgRole.Owner], "WEB",
            new UpdateProjectRequest("Renamed", null, null, null, null, restored.Version), ct);

        Assert.False(restored.IsArchived);
        patch.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task archived_projects_are_out_of_the_list_unless_they_are_asked_for()
    {
        var ct = TestContext.Current.CancellationToken;
        await CreateAsync(_clients[OrgRole.Owner], "Website", "WEB", ct);
        await CreateAsync(_clients[OrgRole.Owner], "Old Thing", "OLD", ct);
        await ArchiveAsync("OLD", archive: true, ct);

        var live = await ListAsync(_clients[OrgRole.Owner], ct);
        var all = await ListAsync(_clients[OrgRole.Owner], ct, includeArchived: true);

        Assert.Equal("WEB", Assert.Single(live).Key);
        Assert.Equal(2, all.Count);
    }

    // -------------------------------------------------------------------------- members

    [Fact]
    public async Task the_member_list_shows_implicit_members_as_implicit()
    {
        var ct = TestContext.Current.CancellationToken;
        await CreateAsync(_clients[OrgRole.Owner], "Website", "WEB", ct);

        var members = await ListMembersAsync(_clients[OrgRole.Owner], "WEB", ct);

        // Everyone in an organization-visible project, at their effective role — and only
        // the creator is named on it explicitly.
        Assert.Equal(4, members.Count);
        Assert.Single(members, m => m.UserId == _ids[OrgRole.Owner] && !m.IsImplicit);
        Assert.All(members.Where(m => m.UserId != _ids[OrgRole.Owner]), m => Assert.True(m.IsImplicit));
        Assert.Equal(ProjectRole.Guest, members.Single(m => m.UserId == _ids[OrgRole.Guest]).Role);
    }

    [Fact]
    public async Task someone_outside_the_organization_cannot_be_added_to_a_project()
    {
        var ct = TestContext.Current.CancellationToken;
        await CreateAsync(_clients[OrgRole.Owner], "Website", "WEB", ct);

        var response = await _clients[OrgRole.Owner].PutAsJsonAsync(
            $"/api/v1/orgs/{Acme}/projects/WEB/members/nobody-at-all",
            new UpdateProjectMemberRequest(ProjectRole.Member), ApiTestContext.Json, ct);

        // There is no foreign key to lean on across schemas, so this is the check.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task leaving_a_project_is_yours_to_do_but_removing_someone_else_is_not()
    {
        var ct = TestContext.Current.CancellationToken;
        await CreateAsync(_clients[OrgRole.Owner], "Secret", "SEC", ct, ProjectVisibility.Private);
        await SetProjectRoleAsync("SEC", _ids[OrgRole.Member], ProjectRole.Member, ct);
        await SetProjectRoleAsync("SEC", _ids[OrgRole.Guest], ProjectRole.Guest, ct);

        var removingSomeoneElse = await _clients[OrgRole.Member].DeleteAsync(
            $"/api/v1/orgs/{Acme}/projects/SEC/members/{_ids[OrgRole.Guest]}", ct);
        var leaving = await _clients[OrgRole.Member].DeleteAsync(
            $"/api/v1/orgs/{Acme}/projects/SEC/members/{_ids[OrgRole.Member]}", ct);

        Assert.Equal(HttpStatusCode.Forbidden, removingSomeoneElse.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, leaving.StatusCode);
        // And the private project closes behind them on the very next request.
        Assert.Equal(HttpStatusCode.NotFound,
            (await _clients[OrgRole.Member].GetAsync($"/api/v1/orgs/{Acme}/projects/SEC", ct)).StatusCode);
    }

    [Fact]
    public async Task there_is_no_implicit_membership_to_remove()
    {
        var ct = TestContext.Current.CancellationToken;
        await CreateAsync(_clients[OrgRole.Owner], "Website", "WEB", ct);

        var response = await _clients[OrgRole.Owner].DeleteAsync(
            $"/api/v1/orgs/{Acme}/projects/WEB/members/{_ids[OrgRole.Member]}", ct);

        // Removing it would mean removing them from the organization, which is a different
        // decision made on a different screen.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // --------------------------------------------------------------------------- helpers

    private Task<HttpResponseMessage> PostAsync(
        HttpClient client, CreateProjectRequest request, CancellationToken ct) =>
        client.PostAsJsonAsync($"/api/v1/orgs/{Acme}/projects", request, ApiTestContext.Json, ct);

    private async Task<ProjectView> CreateAsync(
        HttpClient client, string name, string? key = null, CancellationToken ct = default,
        ProjectVisibility? visibility = null)
    {
        var response = await PostAsync(client, new CreateProjectRequest(name, key, null, visibility, null, null), ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProjectView>(ApiTestContext.Json, ct))!;
    }

    private async Task<List<ProjectView>> ListAsync(
        HttpClient client, CancellationToken ct, bool includeArchived = false)
    {
        var suffix = includeArchived ? "?includeArchived=true" : "";
        return (await client.GetFromJsonAsync<List<ProjectView>>(
            $"/api/v1/orgs/{Acme}/projects{suffix}", ApiTestContext.Json, ct))!;
    }

    private async Task<ProjectView> GetAsync(HttpClient client, string key, CancellationToken ct) =>
        (await client.GetFromJsonAsync<ProjectView>(
            $"/api/v1/orgs/{Acme}/projects/{key}", ApiTestContext.Json, ct))!;

    private Task<HttpResponseMessage> PatchAsync(
        HttpClient client, string key, UpdateProjectRequest request, CancellationToken ct) =>
        client.PatchAsJsonAsync($"/api/v1/orgs/{Acme}/projects/{key}", request, ApiTestContext.Json, ct);

    private async Task<ProjectView> PatchApplyAsync(
        HttpClient client, string key, UpdateProjectRequest request, CancellationToken ct)
    {
        var response = await PatchAsync(client, key, request, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProjectView>(ApiTestContext.Json, ct))!;
    }

    private async Task<ProjectView> ArchiveAsync(string key, bool archive, CancellationToken ct)
    {
        var path = archive ? "archive" : "unarchive";
        var response = await _clients[OrgRole.Owner].PostAsync(
            $"/api/v1/orgs/{Acme}/projects/{key}/{path}", null, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProjectView>(ApiTestContext.Json, ct))!;
    }

    private async Task<List<ProjectMemberView>> ListMembersAsync(
        HttpClient client, string key, CancellationToken ct) =>
        (await client.GetFromJsonAsync<List<ProjectMemberView>>(
            $"/api/v1/orgs/{Acme}/projects/{key}/members", ApiTestContext.Json, ct))!;

    private async Task SetProjectRoleAsync(string key, string userId, ProjectRole role, CancellationToken ct)
    {
        var response = await _clients[OrgRole.Owner].PutAsJsonAsync(
            $"/api/v1/orgs/{Acme}/projects/{key}/members/{userId}",
            new UpdateProjectMemberRequest(role), ApiTestContext.Json, ct);
        response.EnsureSuccessStatusCode();
    }

    private async Task SetMembersCanCreateProjectsAsync(bool allowed, CancellationToken ct)
    {
        var current = (await _clients[OrgRole.Owner].GetFromJsonAsync<OrganizationView>(
            $"/api/v1/orgs/{Acme}", ApiTestContext.Json, ct))!;
        var response = await _clients[OrgRole.Owner].PatchAsJsonAsync($"/api/v1/orgs/{Acme}",
            new UpdateOrganizationRequest(null, null, null, allowed, current.Version), ApiTestContext.Json, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Straight to the table: how someone joined is not what these tests are about.</summary>
    private async Task AddMemberAsync(string userId, OrgRole role)
    {
        await using var connection = new NpgsqlConnection(_context.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO tenancy.organization_members (organization_id, user_id, role, joined_at, can_operate_factory)
            VALUES (@org, @user, @role, now(), @role <> 3)
            """, connection);
        command.Parameters.AddWithValue("org", _acmeId);
        command.Parameters.AddWithValue("user", userId);
        command.Parameters.AddWithValue("role", (short)role);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}

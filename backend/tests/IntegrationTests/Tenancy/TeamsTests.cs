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
/// Teams inside a project.
///
/// Two invariants carry this ticket and both are the database's: exactly one default team
/// per project, and a name that is unique per project however it is capitalised. The rest
/// is about who may change a team - a project admin, or the team's own lead, which is the
/// whole reason <c>is_lead</c> is a flag rather than a project role.
/// </summary>
[Trait("Category", "Tenancy")]
public sealed class TeamsTests(PostgresFixture postgres, GarageFixture garage) : IAsyncLifetime
{
    private ApiTestContext _context = null!;
    private const string Acme = "acme";
    private const string Web = "WEB";
    private Guid _acmeId;

    private readonly Dictionary<OrgRole, HttpClient> _clients = [];
    private readonly Dictionary<OrgRole, string> _ids = [];
    private HttpClient _outsider = null!;

    /// <summary>The default team the project was created with.</summary>
    private TeamView _default = null!;

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        _context = await ApiTestContext.CreateAsync(postgres, garage, "teams");

        var alice = await _context.RegisterAsync("alice@test.local", "Alice", "Anderson");
        _clients[OrgRole.Owner] = _context.ClientFor(alice);
        _ids[OrgRole.Owner] = alice.User.Id;

        var created = await _clients[OrgRole.Owner].PostAsJsonAsync("/api/v1/orgs",
            new CreateOrganizationRequest("Acme", Acme, null, null), ct);
        created.EnsureSuccessStatusCode();
        _acmeId = (await created.Content.ReadFromJsonAsync<OrganizationView>(ApiTestContext.Json, ct))!.Id;

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

        var project = await _clients[OrgRole.Owner].PostAsJsonAsync($"/api/v1/orgs/{Acme}/projects",
            new CreateProjectRequest("Acme Website", Web, null, null, null, null), ApiTestContext.Json, ct);
        project.EnsureSuccessStatusCode();

        _default = Assert.Single(await ListAsync(_clients[OrgRole.Owner], ct));
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

    // ------------------------------------------------------------------- the default team

    [Fact]
    public async Task creating_a_project_creates_its_default_team()
    {
        // One transaction with the project, so there is never a moment in which a project
        // has no team for work to land on.
        Assert.True(_default.IsDefault);
        Assert.Equal("Acme Website", _default.Name);
        Assert.Equal(Web, _default.Key);
        Assert.Equal(14, _default.SprintLengthDays);
        Assert.Equal([1, 2, 3, 4, 5], _default.WorkingDays);
        Assert.Equal(EstimationUnit.Points, _default.EstimationUnit);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task a_project_cannot_end_up_with_two_defaults_or_none()
    {
        var ct = TestContext.Current.CancellationToken;
        var second = await CreateAsync("Platform", ct: ct);

        // Promoting demotes the incumbent in the same transaction.
        var promoted = await PatchApplyAsync(_clients[OrgRole.Owner], second.Id,
            new UpdateTeamRequest(null, null, null, null, null, true, second.Version), ct);
        var teams = await ListAsync(_clients[OrgRole.Owner], ct);

        Assert.True(promoted.IsDefault);
        Assert.Single(teams, t => t.IsDefault);
        Assert.Equal(second.Id, teams.Single(t => t.IsDefault).Id);

        // And there is no way to say "no default" at all.
        var demote = await PatchAsync(_clients[OrgRole.Owner], promoted.Id,
            new UpdateTeamRequest(null, null, null, null, null, false, promoted.Version), ct);
        Assert.Equal(HttpStatusCode.BadRequest, demote.StatusCode);
    }

    [Fact]
    public async Task the_database_refuses_a_second_default_even_when_the_endpoint_is_bypassed()
    {
        var ct = TestContext.Current.CancellationToken;
        var second = await CreateAsync("Platform", ct: ct);

        // Straight past the application: two admins promoting different teams at the same
        // instant would each pass any check the endpoint could make on its own, so the
        // guarantee has to be the index.
        await using var connection = new NpgsqlConnection(_context.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(
            "UPDATE tenancy.teams SET is_default = true WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", second.Id);

        var failure = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync(ct));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, failure.SqlState);
    }

    [Fact]
    public async Task the_default_team_cannot_be_deleted()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _clients[OrgRole.Owner].DeleteAsync(TeamPath(_default.Id), ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(ct);
        Assert.Equal("https://aictiq.com/problems/conflict", problem!.Type);
    }

    [Fact]
    public async Task a_team_can_be_deleted_once_it_is_no_longer_the_default()
    {
        var ct = TestContext.Current.CancellationToken;
        var second = await CreateAsync("Platform", ct: ct);

        var response = await _clients[OrgRole.Owner].DeleteAsync(TeamPath(second.Id), ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Single(await ListAsync(_clients[OrgRole.Owner], ct));
    }

    // -------------------------------------------------------------------------- creating

    [Fact]
    public async Task a_new_team_gets_a_derived_key_and_is_never_the_default()
    {
        var ct = TestContext.Current.CancellationToken;

        var team = await CreateAsync("Core Platform", ct: ct);

        Assert.Equal("CP", team.Key);
        Assert.False(team.IsDefault);
    }

    [Fact]
    public async Task two_teams_in_one_project_cannot_share_a_name_or_a_key()
    {
        var ct = TestContext.Current.CancellationToken;
        await CreateAsync("Platform", "PLAT", ct);

        // Case-insensitively: two teams called "Platform" and "platform" would be
        // indistinguishable in every picker.
        var sameName = await PostAsync(_clients[OrgRole.Owner], new CreateTeamRequest("PLATFORM", "P2"), ct);
        var sameKey = await PostAsync(_clients[OrgRole.Owner], new CreateTeamRequest("Something else", "PLAT"), ct);

        Assert.Equal(HttpStatusCode.BadRequest, sameName.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, sameKey.StatusCode);
    }

    [Fact]
    public async Task the_same_team_name_is_free_in_another_project()
    {
        var ct = TestContext.Current.CancellationToken;
        await CreateAsync("Platform", ct: ct);

        var other = await _clients[OrgRole.Owner].PostAsJsonAsync($"/api/v1/orgs/{Acme}/projects",
            new CreateProjectRequest("Mobile App", "MOB", null, null, null, null), ApiTestContext.Json, ct);
        other.EnsureSuccessStatusCode();

        var response = await _clients[OrgRole.Owner].PostAsJsonAsync(
            $"/api/v1/orgs/{Acme}/projects/MOB/teams", new CreateTeamRequest("Platform", null),
            ApiTestContext.Json, ct);

        // Uniqueness is scoped to the project, as every composite index in a tenant schema
        // is scoped to what owns the rows.
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task only_a_project_admin_can_create_or_delete_a_team()
    {
        var ct = TestContext.Current.CancellationToken;
        var second = await CreateAsync("Platform", ct: ct);

        var byMember = await PostAsync(_clients[OrgRole.Member], new CreateTeamRequest("Sneaky", null), ct);
        var deleteByMember = await _clients[OrgRole.Member].DeleteAsync(TeamPath(second.Id), ct);
        var byStranger = await PostAsync(_outsider, new CreateTeamRequest("Sneaky", null), ct);

        Assert.Equal(HttpStatusCode.Forbidden, byMember.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, deleteByMember.StatusCode);
        // 404, not 403: a stranger must not learn that this project exists.
        Assert.Equal(HttpStatusCode.NotFound, byStranger.StatusCode);
    }

    // -------------------------------------------------------------------------- settings

    [Fact]
    public async Task planning_settings_round_trip()
    {
        var ct = TestContext.Current.CancellationToken;

        var updated = await PatchApplyAsync(_clients[OrgRole.Owner], _default.Id,
            new UpdateTeamRequest("Web Team", 7, [1, 2, 3, 4], EstimationUnit.Hours,
                "Europe/Sarajevo", null, _default.Version), ct);

        Assert.Equal("Web Team", updated.Name);
        Assert.Equal(7, updated.SprintLengthDays);
        Assert.Equal([1, 2, 3, 4], updated.WorkingDays);
        Assert.Equal(EstimationUnit.Hours, updated.EstimationUnit);
        Assert.Equal("Europe/Sarajevo", updated.TimeZone);
    }

    [Fact]
    public async Task an_empty_time_zone_returns_the_team_to_the_organizations()
    {
        var ct = TestContext.Current.CancellationToken;
        var overridden = await PatchApplyAsync(_clients[OrgRole.Owner], _default.Id,
            new UpdateTeamRequest(null, null, null, null, "Europe/Sarajevo", null, _default.Version), ct);

        var cleared = await PatchApplyAsync(_clients[OrgRole.Owner], _default.Id,
            new UpdateTeamRequest(null, null, null, null, "", null, overridden.Version), ct);

        Assert.Equal("Europe/Sarajevo", overridden.TimeZone);
        Assert.Null(cleared.TimeZone);
    }

    [Theory]
    [InlineData(0, null, null)]                       // a sprint of no days
    [InlineData(29, null, null)]                      // past four weeks it is not a sprint
    [InlineData(null, new[] { 7 }, null)]             // there is no eighth day
    [InlineData(null, new int[0], null)]              // a team that works no days
    [InlineData(null, new[] { 1, 1 }, null)]          // the same day twice
    [InlineData(null, null, "Mars/Olympus_Mons")]
    public async Task nonsense_settings_are_refused(int? sprintDays, int[]? workingDays, string? timeZone)
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await PatchAsync(_clients[OrgRole.Owner], _default.Id,
            new UpdateTeamRequest(null, sprintDays, workingDays, null, timeZone, null, _default.Version), ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task a_stale_version_loses_to_whoever_saved_first()
    {
        var ct = TestContext.Current.CancellationToken;

        var first = await PatchAsync(_clients[OrgRole.Owner], _default.Id,
            new UpdateTeamRequest("First", null, null, null, null, null, _default.Version), ct);
        var second = await PatchAsync(_clients[OrgRole.Admin], _default.Id,
            new UpdateTeamRequest("Second", null, null, null, null, null, _default.Version), ct);

        first.EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    // ------------------------------------------------------------------------ the roster

    [Fact]
    public async Task a_lead_runs_their_own_team_without_running_the_project()
    {
        var ct = TestContext.Current.CancellationToken;
        await SetMemberAsync(_default.Id, _ids[OrgRole.Member], isLead: true, ct: ct);

        // Carol is an ordinary organization member and therefore only a project Member -
        // but she leads this team, so its settings and its roster are hers.
        var settings = await PatchAsync(_clients[OrgRole.Member], _default.Id,
            new UpdateTeamRequest("Carol's Team", 7, null, null, null, null, _default.Version), ct);
        var roster = await _clients[OrgRole.Member].PutAsJsonAsync(
            $"{TeamPath(_default.Id)}/members/{_ids[OrgRole.Guest]}",
            new UpdateTeamMemberRequest(null, 4m), ApiTestContext.Json, ct);

        settings.EnsureSuccessStatusCode();
        roster.EnsureSuccessStatusCode();

        // The project itself is still not hers.
        var project = await _clients[OrgRole.Member].PostAsJsonAsync(
            $"/api/v1/orgs/{Acme}/projects/{Web}/teams", new CreateTeamRequest("Another", null),
            ApiTestContext.Json, ct);
        Assert.Equal(HttpStatusCode.Forbidden, project.StatusCode);
    }

    [Fact]
    public async Task a_member_who_is_not_a_lead_cannot_change_the_team()
    {
        var ct = TestContext.Current.CancellationToken;
        await SetMemberAsync(_default.Id, _ids[OrgRole.Member], isLead: false, ct: ct);

        var response = await PatchAsync(_clients[OrgRole.Member], _default.Id,
            new UpdateTeamRequest("Renamed", null, null, null, null, null, _default.Version), ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task someone_who_cannot_see_the_project_cannot_be_put_on_its_team()
    {
        var ct = TestContext.Current.CancellationToken;
        var privateProject = await _clients[OrgRole.Owner].PostAsJsonAsync(
            $"/api/v1/orgs/{Acme}/projects",
            new CreateProjectRequest("Secret", "SEC", null, ProjectVisibility.Private, null, null),
            ApiTestContext.Json, ct);
        privateProject.EnsureSuccessStatusCode();

        var teams = (await _clients[OrgRole.Owner].GetFromJsonAsync<List<TeamView>>(
            $"/api/v1/orgs/{Acme}/projects/SEC/teams", ApiTestContext.Json, ct))!;

        var response = await _clients[OrgRole.Owner].PutAsJsonAsync(
            $"/api/v1/orgs/{Acme}/projects/SEC/teams/{teams[0].Id}/members/{_ids[OrgRole.Member]}",
            new UpdateTeamMemberRequest(null, null), ApiTestContext.Json, ct);

        // No foreign key across schemas to lean on, so the endpoint is the check -
        // recorded as a known gap for the RLS pass.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task a_capacity_edit_does_not_quietly_demote_a_lead()
    {
        var ct = TestContext.Current.CancellationToken;
        await SetMemberAsync(_default.Id, _ids[OrgRole.Member], isLead: true, ct: ct);

        var updated = await SetMemberAsync(_default.Id, _ids[OrgRole.Member], isLead: null, capacity: 6m, ct: ct);

        // PUT, but absent fields keep their value: the client sent a capacity, not a
        // statement about leadership.
        Assert.True(updated.IsLead);
        Assert.Equal(6m, updated.CapacityHoursPerDay);
    }

    [Fact]
    public async Task an_impossible_capacity_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _clients[OrgRole.Owner].PutAsJsonAsync(
            $"{TeamPath(_default.Id)}/members/{_ids[OrgRole.Member]}",
            new UpdateTeamMemberRequest(null, 30m), ApiTestContext.Json, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task leaving_a_team_is_yours_to_do()
    {
        var ct = TestContext.Current.CancellationToken;
        await SetMemberAsync(_default.Id, _ids[OrgRole.Member], isLead: false, ct: ct);
        await SetMemberAsync(_default.Id, _ids[OrgRole.Guest], isLead: false, ct: ct);

        var leaving = await _clients[OrgRole.Member].DeleteAsync(
            $"{TeamPath(_default.Id)}/members/{_ids[OrgRole.Member]}", ct);
        var removingSomeoneElse = await _clients[OrgRole.Member].DeleteAsync(
            $"{TeamPath(_default.Id)}/members/{_ids[OrgRole.Guest]}", ct);

        Assert.Equal(HttpStatusCode.NoContent, leaving.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, removingSomeoneElse.StatusCode);
    }

    [Fact]
    public async Task everyone_on_the_project_can_read_the_teams_and_a_stranger_cannot()
    {
        var ct = TestContext.Current.CancellationToken;

        // A project Guest reads; they simply cannot write.
        Assert.Single(await ListAsync(_clients[OrgRole.Guest], ct));

        var response = await _outsider.GetAsync($"/api/v1/orgs/{Acme}/projects/{Web}/teams", ct);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task a_team_id_from_another_project_is_not_found_rather_than_borrowed()
    {
        var ct = TestContext.Current.CancellationToken;
        var other = await _clients[OrgRole.Owner].PostAsJsonAsync($"/api/v1/orgs/{Acme}/projects",
            new CreateProjectRequest("Mobile App", "MOB", null, null, null, null), ApiTestContext.Json, ct);
        other.EnsureSuccessStatusCode();

        var mobileTeams = (await _clients[OrgRole.Owner].GetFromJsonAsync<List<TeamView>>(
            $"/api/v1/orgs/{Acme}/projects/MOB/teams", ApiTestContext.Json, ct))!;

        // The id alone would find the row; scoping the lookup by the project in the URL is
        // what stops one project's team being addressed through another's.
        var response = await _clients[OrgRole.Owner].GetAsync(TeamPath(mobileTeams[0].Id), ct);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task an_archived_project_freezes_its_teams()
    {
        var ct = TestContext.Current.CancellationToken;
        (await _clients[OrgRole.Owner].PostAsync(
            $"/api/v1/orgs/{Acme}/projects/{Web}/archive", null, ct)).EnsureSuccessStatusCode();

        var read = await _clients[OrgRole.Owner].GetAsync($"/api/v1/orgs/{Acme}/projects/{Web}/teams", ct);
        var write = await PatchAsync(_clients[OrgRole.Owner], _default.Id,
            new UpdateTeamRequest("Renamed", null, null, null, null, null, _default.Version), ct);

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, write.StatusCode);
    }

    // --------------------------------------------------------------------------- helpers

    private static string TeamsPath => $"/api/v1/orgs/{Acme}/projects/{Web}/teams";

    private static string TeamPath(Guid teamId) => $"{TeamsPath}/{teamId}";

    private Task<HttpResponseMessage> PostAsync(
        HttpClient client, CreateTeamRequest request, CancellationToken ct) =>
        client.PostAsJsonAsync(TeamsPath, request, ApiTestContext.Json, ct);

    private async Task<TeamView> CreateAsync(string name, string? key = null, CancellationToken ct = default)
    {
        var response = await PostAsync(_clients[OrgRole.Owner], new CreateTeamRequest(name, key), ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TeamView>(ApiTestContext.Json, ct))!;
    }

    private async Task<List<TeamView>> ListAsync(HttpClient client, CancellationToken ct) =>
        (await client.GetFromJsonAsync<List<TeamView>>(TeamsPath, ApiTestContext.Json, ct))!;

    private Task<HttpResponseMessage> PatchAsync(
        HttpClient client, Guid teamId, UpdateTeamRequest request, CancellationToken ct) =>
        client.PatchAsJsonAsync(TeamPath(teamId), request, ApiTestContext.Json, ct);

    private async Task<TeamView> PatchApplyAsync(
        HttpClient client, Guid teamId, UpdateTeamRequest request, CancellationToken ct)
    {
        var response = await PatchAsync(client, teamId, request, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TeamView>(ApiTestContext.Json, ct))!;
    }

    private async Task<TeamMemberView> SetMemberAsync(
        Guid teamId, string userId, bool? isLead, decimal? capacity = null, CancellationToken ct = default)
    {
        var response = await _clients[OrgRole.Owner].PutAsJsonAsync(
            $"{TeamPath(teamId)}/members/{userId}",
            new UpdateTeamMemberRequest(isLead, capacity), ApiTestContext.Json, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TeamMemberView>(ApiTestContext.Json, ct))!;
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

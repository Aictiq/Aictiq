using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.Tenancy.Domain;
using Aictiq.Modules.Tenancy.Endpoints;
using Aictiq.Modules.WorkItems.Domain;
using Aictiq.Modules.WorkItems.Endpoints;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Paging;

namespace Aictiq.IntegrationTests.WorkItems;

/// <summary>
/// Labels over HTTP: who may manage the project's label set, who may put them on items,
/// the two idempotent item-labelling endpoints, and the filter grammar built on top.
///
/// One organization-visible project, so an Admin/Member/Guest org role is also that
/// project's effective role - the same trick <see cref="Aictiq.IntegrationTests.Tenancy.ProjectsTests"/>
/// uses, which lets every role be exercised without a separate membership call per test.
/// </summary>
[Trait("Category", "WorkItems")]
[Collection("postgres")]
public sealed class LabelsTests(PostgresFixture postgres, GarageFixture garage) : IAsyncLifetime
{
    private const string Org = "labels-org";
    private ApiTestContext _context = null!;
    private Guid _orgId;
    private ProjectView _project = null!;
    private readonly Dictionary<OrgRole, HttpClient> _clients = [];
    private readonly Dictionary<OrgRole, string> _ids = [];
    private HttpClient _outsider = null!;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _context = await ApiTestContext.CreateAsync(postgres, garage, "labels");

        var owner = await _context.RegisterAsync("labels-owner@test.local", "Owner", "Owner");
        _clients[OrgRole.Owner] = _context.ClientFor(owner);
        _ids[OrgRole.Owner] = owner.User.Id;

        var org = await _clients[OrgRole.Owner].PostAsJsonAsync("/api/v1/orgs",
            new CreateOrganizationRequest("Labels Org", Org, null, null), ApiTestContext.Json, Ct);
        org.EnsureSuccessStatusCode();
        _orgId = (await org.Content.ReadFromJsonAsync<OrganizationView>(ApiTestContext.Json, Ct))!.Id;

        foreach (var (role, email) in new[] { (OrgRole.Admin, "labels-admin@test.local"), (OrgRole.Member, "labels-member@test.local"), (OrgRole.Guest, "labels-guest@test.local") })
        {
            var auth = await _context.RegisterAsync(email, "Test", "User");
            _clients[role] = _context.ClientFor(auth);
            _ids[role] = auth.User.Id;
            await AddMemberAsync(auth.User.Id, role);
        }
        _outsider = _context.ClientFor(await _context.RegisterAsync("labels-outsider@test.local", "Out", "Sider"));

        var project = await _clients[OrgRole.Owner].PostAsJsonAsync($"/api/v1/orgs/{Org}/projects",
            new CreateProjectRequest("Labels Project", "LBL", null, ProjectVisibility.Organization, null, null), ApiTestContext.Json, Ct);
        project.EnsureSuccessStatusCode();
        _project = (await project.Content.ReadFromJsonAsync<ProjectView>(ApiTestContext.Json, Ct))!;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients.Values) client.Dispose();
        _outsider.Dispose();
        await _context.DisposeAsync();
    }

    // -------------------------------------------------------------------------------- CRUD

    [Fact]
    public async Task a_member_can_create_and_patch_a_label()
    {
        var created = await CreateAsync(_clients[OrgRole.Member], "Frontend", "#3B82F6", "type");
        Assert.Equal("Frontend", created.Name);
        Assert.Equal("#3B82F6", created.Color);
        Assert.Equal("type", created.Group);
        Assert.Equal(0, created.ItemCount);

        var patch = await PatchAsync(_clients[OrgRole.Member], created.Id,
            new UpdateLabelRequest("Frontend Team", null, "UI and client code", null, created.Version));
        patch.EnsureSuccessStatusCode();
        var patched = (await patch.Content.ReadFromJsonAsync<LabelView>(ApiTestContext.Json, Ct))!;

        Assert.Equal("Frontend Team", patched.Name);
        Assert.Null(patched.Color);
        Assert.Equal("UI and client code", patched.Description);
    }

    [Fact]
    public async Task a_cleared_colour_arrives_as_an_empty_string_and_means_no_colour()
    {
        // A form clears a field to "", not to null. That is not a malformed hex value -
        // it is the absence of one, and the settings screen has no other way to say so.
        var created = await CreateAsync(_clients[OrgRole.Member], "Chore", "#3B82F6", null);

        var patch = await PatchAsync(_clients[OrgRole.Member], created.Id,
            new UpdateLabelRequest("Chore", "", "", "", created.Version));
        Assert.True(patch.IsSuccessStatusCode, await patch.Content.ReadAsStringAsync(Ct));
        var patched = (await patch.Content.ReadFromJsonAsync<LabelView>(ApiTestContext.Json, Ct))!;

        Assert.Null(patched.Color);
        Assert.Null(patched.Group);
        Assert.Null(patched.Description);

        var malformed = await PostRawAsync(_clients[OrgRole.Member], new CreateLabelRequest("Nope", "blue", null, null));
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
    }

    [Fact]
    public async Task a_member_cannot_delete_a_label_but_an_admin_can()
    {
        var label = await CreateAsync(_clients[OrgRole.Owner], "Bug", null, null);

        var deniedDelete = await _clients[OrgRole.Member].DeleteAsync(LabelPath(label.Id), Ct);
        // Visible, so 403 rather than 404: a member can already see the label exists.
        Assert.Equal(HttpStatusCode.Forbidden, deniedDelete.StatusCode);

        var allowedDelete = await _clients[OrgRole.Admin].DeleteAsync(LabelPath(label.Id), Ct);
        Assert.Equal(HttpStatusCode.NoContent, allowedDelete.StatusCode);
    }

    [Fact]
    public async Task a_non_member_gets_404_not_403()
    {
        var label = await CreateAsync(_clients[OrgRole.Owner], "Bug", null, null);

        var list = await _outsider.GetAsync($"/api/v1/orgs/{Org}/projects/{_project.Key}/labels", Ct);
        var patch = await _outsider.PatchAsJsonAsync(LabelPath(label.Id),
            new UpdateLabelRequest("Renamed", null, null, null, label.Version), ApiTestContext.Json, Ct);

        Assert.Equal(HttpStatusCode.NotFound, list.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, patch.StatusCode);
    }

    [Fact]
    public async Task a_duplicate_name_differing_only_in_case_is_a_conflict()
    {
        await CreateAsync(_clients[OrgRole.Owner], "Bug", null, null);

        var response = await PostRawAsync(_clients[OrgRole.Owner], new CreateLabelRequest("bug", null, null, null));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task the_database_not_the_endpoint_refuses_a_case_insensitive_duplicate_name()
    {
        await CreateAsync(_clients[OrgRole.Owner], "Bug", null, null);

        await using var connection = new NpgsqlConnection(_context.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO work.labels (id, organization_id, project_id, name)
            VALUES (gen_random_uuid(), @org, @project, 'BUG')
            """, connection);
        command.Parameters.AddWithValue("org", _orgId);
        command.Parameters.AddWithValue("project", _project.Id);

        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync(Ct));
        Assert.Equal("23505", error.SqlState);
    }

    [Fact]
    public async Task a_stale_version_loses_the_patch_race()
    {
        var label = await CreateAsync(_clients[OrgRole.Owner], "Bug", null, null);

        var first = await PatchAsync(_clients[OrgRole.Owner], label.Id,
            new UpdateLabelRequest("First", null, null, null, label.Version));
        var second = await PatchAsync(_clients[OrgRole.Admin], label.Id,
            new UpdateLabelRequest("Second", null, null, null, label.Version));

        first.EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    // ------------------------------------------------------------------- item labelling

    [Fact]
    public async Task deleting_a_label_cascades_to_item_labels_and_the_item_stops_reporting_it()
    {
        var label = await CreateAsync(_clients[OrgRole.Owner], "Bug", null, null);
        var item = await CreateItemAsync("Something");

        var put = await AddLabelAsync(item.Key, label.Id);
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        Assert.Single((await GetItemAsync(item.Key)).Labels);

        var deleted = await _clients[OrgRole.Admin].DeleteAsync(LabelPath(label.Id), Ct);
        deleted.EnsureSuccessStatusCode();

        Assert.Empty((await GetItemAsync(item.Key)).Labels);
    }

    [Fact]
    public async Task put_is_idempotent_and_a_label_from_another_project_is_404()
    {
        var label = await CreateAsync(_clients[OrgRole.Owner], "Bug", null, null);
        var item = await CreateItemAsync("Item");

        var first = await AddLabelAsync(item.Key, label.Id);
        var second = await AddLabelAsync(item.Key, label.Id);
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
        Assert.Single((await GetItemAsync(item.Key)).Labels);

        var otherProject = await CreateProjectAsync("Other Project", "OTH");
        var otherLabel = await CreateAsync(_clients[OrgRole.Owner], "Other", null, null, otherProject.Key);
        var crossProject = await AddLabelAsync(item.Key, otherLabel.Id);
        Assert.Equal(HttpStatusCode.NotFound, crossProject.StatusCode);
    }

    [Fact]
    public async Task delete_is_idempotent()
    {
        var label = await CreateAsync(_clients[OrgRole.Owner], "Bug", null, null);
        var item = await CreateItemAsync("Item");
        (await AddLabelAsync(item.Key, label.Id)).EnsureSuccessStatusCode();

        var first = await _clients[OrgRole.Member].DeleteAsync(ItemLabelPath(item.Key, label.Id), Ct);
        var second = await _clients[OrgRole.Member].DeleteAsync(ItemLabelPath(item.Key, label.Id), Ct);

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
        Assert.Empty((await GetItemAsync(item.Key)).Labels);
    }

    // ---------------------------------------------------------------------------- filters

    [Fact]
    public async Task filter_grammar_supports_any_all_and_none_and_ands_with_other_terms()
    {
        var frontend = await CreateAsync(_clients[OrgRole.Owner], "Frontend", null, null);
        var backend = await CreateAsync(_clients[OrgRole.Owner], "Backend", null, null);
        var urgent = await CreateAsync(_clients[OrgRole.Owner], "Urgent", null, null);

        var bugFrontend = await CreateItemAsync("Bug + frontend", WorkItemType.Bug);
        var bugBackend = await CreateItemAsync("Bug + backend", WorkItemType.Bug);
        var bugFrontendUrgent = await CreateItemAsync("Bug + frontend + urgent", WorkItemType.Bug);
        var epicFrontend = await CreateItemAsync("Epic + frontend", WorkItemType.Epic);
        var plain = await CreateItemAsync("No labels", WorkItemType.Bug);

        (await AddLabelAsync(bugFrontend.Key, frontend.Id)).EnsureSuccessStatusCode();
        (await AddLabelAsync(bugBackend.Key, backend.Id)).EnsureSuccessStatusCode();
        (await AddLabelAsync(bugFrontendUrgent.Key, frontend.Id)).EnsureSuccessStatusCode();
        (await AddLabelAsync(bugFrontendUrgent.Key, urgent.Id)).EnsureSuccessStatusCode();
        (await AddLabelAsync(epicFrontend.Key, frontend.Id)).EnsureSuccessStatusCode();

        var any = await ListItemsAsync("label:Frontend,Backend");
        Assert.Equal(
            new[] { bugFrontend.Key, bugBackend.Key, bugFrontendUrgent.Key, epicFrontend.Key }.OrderBy(x => x),
            any.Select(x => x.Key).OrderBy(x => x));

        var all = await ListItemsAsync("label:Frontend+Urgent");
        Assert.Equal([bugFrontendUrgent.Key], all.Select(x => x.Key));

        var none = await ListItemsAsync("-label:Frontend");
        Assert.Equal(
            new[] { bugBackend.Key, plain.Key }.OrderBy(x => x),
            none.Select(x => x.Key).OrderBy(x => x));

        // Several terms AND together: narrows the any-of-Frontend set down to Bugs only.
        var combined = await ListItemsAsync("label:Frontend type:bug");
        Assert.Equal(
            new[] { bugFrontend.Key, bugFrontendUrgent.Key }.OrderBy(x => x),
            combined.Select(x => x.Key).OrderBy(x => x));
    }

    [Theory]
    [InlineData("label:")]
    [InlineData("-label:")]
    [InlineData("label:doesnotexist")]
    public async Task an_empty_or_unknown_label_term_is_a_clean_validation_error(string filter)
    {
        var response = await ListItemsRawAsync(filter);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ------------------------------------------------------------------------- archiving

    [Fact]
    public async Task an_archived_project_refuses_every_label_write()
    {
        var label = await CreateAsync(_clients[OrgRole.Owner], "Bug", null, null);
        var item = await CreateItemAsync("Item");

        var archived = await _clients[OrgRole.Owner].PostAsync($"/api/v1/orgs/{Org}/projects/{_project.Key}/archive", null, Ct);
        archived.EnsureSuccessStatusCode();

        var createDenied = await PostRawAsync(_clients[OrgRole.Owner], new CreateLabelRequest("Should fail", null, null, null));
        var putDenied = await AddLabelAsync(item.Key, label.Id);
        var deleteDenied = await _clients[OrgRole.Member].DeleteAsync(ItemLabelPath(item.Key, label.Id), Ct);

        Assert.Equal(HttpStatusCode.Conflict, createDenied.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, putDenied.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, deleteDenied.StatusCode);

        var problem = await createDenied.Content.ReadFromJsonAsync<ProblemDetails>(Ct);
        Assert.Equal(ProblemTypes.ProjectArchived, problem!.Type);
    }

    // -------------------------------------------------------------------------- helpers

    private string LabelPath(Guid labelId, string? projectKey = null) =>
        $"/api/v1/orgs/{Org}/projects/{projectKey ?? _project.Key}/labels/{labelId}";

    private string ItemLabelPath(string itemKey, Guid labelId) =>
        $"/api/v1/orgs/{Org}/items/{itemKey}/labels/{labelId}";

    private Task<HttpResponseMessage> PostRawAsync(HttpClient client, CreateLabelRequest request, string? projectKey = null) =>
        client.PostAsJsonAsync($"/api/v1/orgs/{Org}/projects/{projectKey ?? _project.Key}/labels", request, ApiTestContext.Json, Ct);

    private Task<HttpResponseMessage> PatchAsync(HttpClient client, Guid labelId, UpdateLabelRequest request, string? projectKey = null) =>
        client.PatchAsJsonAsync(LabelPath(labelId, projectKey), request, ApiTestContext.Json, Ct);

    private async Task<LabelView> CreateAsync(HttpClient client, string name, string? color, string? group, string? projectKey = null)
    {
        var response = await PostRawAsync(client, new CreateLabelRequest(name, color, null, group), projectKey);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<LabelView>(ApiTestContext.Json, Ct))!;
    }

    private async Task<ProjectView> CreateProjectAsync(string name, string key)
    {
        var response = await _clients[OrgRole.Owner].PostAsJsonAsync($"/api/v1/orgs/{Org}/projects",
            new CreateProjectRequest(name, key, null, ProjectVisibility.Organization, null, null), ApiTestContext.Json, Ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProjectView>(ApiTestContext.Json, Ct))!;
    }

    private async Task<WorkItemView> CreateItemAsync(string title, WorkItemType type = WorkItemType.Bug, string? projectKey = null)
    {
        var response = await _clients[OrgRole.Member].PostAsJsonAsync($"/api/v1/orgs/{Org}/projects/{projectKey ?? _project.Key}/items/",
            new CreateWorkItemRequest(type, title, null, null, null, null, null, null, null, null, null, null, null, null),
            ApiTestContext.Json, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<WorkItemView>(ApiTestContext.Json, Ct))!;
    }

    private async Task<WorkItemView> GetItemAsync(string key) =>
        (await _clients[OrgRole.Member].GetFromJsonAsync<WorkItemView>($"/api/v1/orgs/{Org}/items/{key}", ApiTestContext.Json, Ct))!;

    private Task<HttpResponseMessage> AddLabelAsync(string itemKey, Guid labelId, HttpClient? client = null) =>
        (client ?? _clients[OrgRole.Member]).PutAsync(ItemLabelPath(itemKey, labelId), null, Ct);

    private Task<HttpResponseMessage> ListItemsRawAsync(string filter) =>
        _clients[OrgRole.Member].GetAsync($"/api/v1/orgs/{Org}/projects/{_project.Key}/items/?filter={Uri.EscapeDataString(filter)}", Ct);

    private async Task<List<WorkItemView>> ListItemsAsync(string filter)
    {
        var response = await ListItemsRawAsync(filter);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        var page = await response.Content.ReadFromJsonAsync<PagedResult<WorkItemView>>(ApiTestContext.Json, Ct);
        return page!.Items.ToList();
    }

    /// <summary>Straight to the table: how someone joined is not what these tests are about.</summary>
    private async Task AddMemberAsync(string userId, OrgRole role)
    {
        await using var connection = new NpgsqlConnection(_context.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO tenancy.organization_members (organization_id, user_id, role, joined_at, can_operate_factory)
            VALUES (@org, @user, @role, now(), @role <> 3)
            """, connection);
        command.Parameters.AddWithValue("org", _orgId);
        command.Parameters.AddWithValue("user", userId);
        command.Parameters.AddWithValue("role", (short)role);
        await command.ExecuteNonQueryAsync(Ct);
    }
}

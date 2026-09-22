using System.Net;
using System.Net.Http.Json;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.Automation;
using Aictiq.Modules.Automation.Domain;
using Aictiq.Modules.Automation.Endpoints;
using Aictiq.Modules.Automation.Events;
using Aictiq.Modules.Tenancy.Domain;
using Aictiq.Modules.Tenancy.Endpoints;
using Aictiq.Modules.Wiki.Endpoints;
using Aictiq.Modules.WorkItems.Endpoints;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Aictiq.IntegrationTests.Automation;

[Trait("Category", "Automation")]
[Collection("postgres")]
public sealed class PlaybookTests(PostgresFixture postgres, GarageFixture garage) : IAsyncLifetime
{
    private const string Slug = "factory-playbooks";
    private ApiTestContext _context = null!;
    private HttpClient _owner = null!;
    private HttpClient _member = null!;
    private HttpClient _stranger = null!;
    private OrganizationView _organization = null!;
    private ProjectView _project = null!;
    private ProjectView _otherProject = null!;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _context = await ApiTestContext.CreateAsync(postgres, garage, "playbooks");
        var owner = await _context.RegisterAsync($"playbook-owner-{Guid.NewGuid():N}@test.local");
        _owner = _context.ClientFor(owner);
        var organization = await _owner.PostAsJsonAsync("/api/v1/orgs",
            new CreateOrganizationRequest("Factory playbooks", Slug, null, null), ApiTestContext.Json, Ct);
        organization.EnsureSuccessStatusCode();
        _organization = (await organization.Content.ReadFromJsonAsync<OrganizationView>(ApiTestContext.Json, Ct))!;
        _project = await CreateProjectAsync("Factory project", "FAC");
        _otherProject = await CreateProjectAsync("Other project", "OTH");

        var member = await _context.RegisterAsync($"playbook-member-{Guid.NewGuid():N}@test.local");
        _member = _context.ClientFor(member);
        await AddOrganizationMemberAsync(member.User.Id, OrgRole.Member);
        var stranger = await _context.RegisterAsync($"playbook-stranger-{Guid.NewGuid():N}@test.local");
        _stranger = _context.ClientFor(stranger);
    }

    public async ValueTask DisposeAsync()
    {
        _owner.Dispose();
        _member.Dispose();
        _stranger.Dispose();
        await _context.DisposeAsync();
    }

    [Fact]
    public async Task starter_creates_a_default_playbook_backed_by_an_editable_wiki_page_and_is_idempotent()
    {
        var response = await _owner.PostAsync($"{Base(_project)}/playbooks/starter", null, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var playbook = (await response.Content.ReadFromJsonAsync<PlaybookView>(ApiTestContext.Json, Ct))!;
        Assert.True(playbook.IsDefault);
        Assert.Equal("claude", playbook.Harness);
        Assert.NotNull(playbook.WikiPageId);

        var page = await _owner.GetFromJsonAsync<WikiPageView>(
            $"/api/v1/orgs/{Slug}/wiki/pages/{playbook.WikiPageId}", ApiTestContext.Json, Ct);
        Assert.Equal("Implement", page!.Title);
        Assert.Contains("run the project's tests and verify", page.ContentMarkdown);
        Assert.Contains("Do not transition the item yourself", page.ContentMarkdown);
        var edited = await _owner.PatchAsJsonAsync($"/api/v1/orgs/{Slug}/wiki/pages/{page.Id}",
            new UpdateWikiPageRequest(null, page.ContentMarkdown + "\n\nProject convention.", page.Version, "Project convention"),
            ApiTestContext.Json, Ct);
        edited.EnsureSuccessStatusCode();
        var revisions = await _owner.GetFromJsonAsync<List<WikiRevisionView>>(
            $"/api/v1/orgs/{Slug}/wiki/pages/{page.Id}/revisions", ApiTestContext.Json, Ct);
        Assert.Equal(2, revisions!.Count);

        Assert.Equal(HttpStatusCode.Conflict,
            (await _owner.PostAsync($"{Base(_project)}/playbooks/starter", null, Ct)).StatusCode);
    }

    [Fact]
    public async Task page_and_workflow_state_must_belong_to_the_playbooks_project()
    {
        var page = await CreatePageAsync(_project, "Instructions");
        var otherPage = await CreatePageAsync(_otherProject, "Foreign instructions");
        var otherState = (await WorkflowsAsync(_otherProject)).Single().States.First().Id;

        var wrongPage = await _owner.PostAsJsonAsync($"{Base(_project)}/playbooks",
            new CreatePlaybookRequest("Wrong page", otherPage.Id, "codex", null, null, 60), ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.NotFound, wrongPage.StatusCode);

        var wrongState = await _owner.PostAsJsonAsync($"{Base(_project)}/playbooks",
            new CreatePlaybookRequest("Wrong state", page.Id, "codex", otherState, null, 60), ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, wrongState.StatusCode);
    }

    [Fact]
    public async Task promoting_a_playbook_demotes_the_incumbent_and_the_database_prevents_two_defaults()
    {
        var starterResponse = await _owner.PostAsync($"{Base(_project)}/playbooks/starter", null, Ct);
        starterResponse.EnsureSuccessStatusCode();
        var starter = (await starterResponse.Content.ReadFromJsonAsync<PlaybookView>(ApiTestContext.Json, Ct))!;
        var page = await CreatePageAsync(_project, "Review");
        var created = await _owner.PostAsJsonAsync($"{Base(_project)}/playbooks",
            new CreatePlaybookRequest("Review", page.Id, "opencode", null, null, 90), ApiTestContext.Json, Ct);
        created.EnsureSuccessStatusCode();
        var second = (await created.Content.ReadFromJsonAsync<PlaybookView>(ApiTestContext.Json, Ct))!;

        var promoted = await _owner.PutAsync($"{Base(_project)}/playbooks/{second.Id}/default", null, Ct);
        promoted.EnsureSuccessStatusCode();
        var listed = (await _owner.GetFromJsonAsync<List<PlaybookView>>(
            $"{Base(_project)}/playbooks", ApiTestContext.Json, Ct))!;
        Assert.Equal(second.Id, Assert.Single(listed, playbook => playbook.IsDefault).Id);
        Assert.False(listed.Single(playbook => playbook.Id == starter.Id).IsDefault);

        // Put both candidates back to non-default, then let two raw transactions race to
        // claim the partial unique index. One blocks on the other's speculative index row;
        // after the winner commits, the loser must fail rather than leave two defaults.
        await using (var reset = new NpgsqlConnection(_context.ConnectionString))
        {
            await reset.OpenAsync(Ct);
            await using var command = new NpgsqlCommand(
                "UPDATE automation.playbooks SET is_default = false WHERE project_id = @project", reset);
            command.Parameters.AddWithValue("project", _project.Id);
            await command.ExecuteNonQueryAsync(Ct);
        }

        await using var firstConnection = new NpgsqlConnection(_context.ConnectionString);
        await using var secondConnection = new NpgsqlConnection(_context.ConnectionString);
        await firstConnection.OpenAsync(Ct);
        await secondConnection.OpenAsync(Ct);
        await using var firstTransaction = await firstConnection.BeginTransactionAsync(Ct);
        await using var secondTransaction = await secondConnection.BeginTransactionAsync(Ct);
        await using var firstCommand = new NpgsqlCommand(
            "UPDATE automation.playbooks SET is_default = true WHERE id = @id", firstConnection, firstTransaction);
        await using var secondCommand = new NpgsqlCommand(
            "UPDATE automation.playbooks SET is_default = true WHERE id = @id", secondConnection, secondTransaction);
        firstCommand.Parameters.AddWithValue("id", starter.Id);
        secondCommand.Parameters.AddWithValue("id", second.Id);

        var firstRace = firstCommand.ExecuteNonQueryAsync(Ct);
        var secondRace = secondCommand.ExecuteNonQueryAsync(Ct);
        var winner = await Task.WhenAny(firstRace, secondRace);
        await winner;
        var loser = winner == firstRace ? secondRace : firstRace;
        if (winner == firstRace) await firstTransaction.CommitAsync(Ct);
        else await secondTransaction.CommitAsync(Ct);
        var exception = await Assert.ThrowsAsync<PostgresException>(async () => await loser);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, exception.SqlState);
        if (winner == firstRace) await secondTransaction.RollbackAsync(Ct);
        else await firstTransaction.RollbackAsync(Ct);

        await using var verify = new NpgsqlConnection(_context.ConnectionString);
        await verify.OpenAsync(Ct);
        await using var count = new NpgsqlCommand(
            "SELECT count(*) FROM automation.playbooks WHERE project_id = @project AND is_default", verify);
        count.Parameters.AddWithValue("project", _project.Id);
        Assert.Equal(1L, (long)(await count.ExecuteScalarAsync(Ct))!);
    }

    [Fact]
    public async Task deleting_a_wiki_page_nulls_the_pointer_without_deleting_the_playbook()
    {
        var page = await CreatePageAsync(_project, "Disposable instructions");
        var created = await _owner.PostAsJsonAsync($"{Base(_project)}/playbooks",
            new CreatePlaybookRequest("Disposable", page.Id, "claude", null, null, 60), ApiTestContext.Json, Ct);
        created.EnsureSuccessStatusCode();
        var playbook = (await created.Content.ReadFromJsonAsync<PlaybookView>(ApiTestContext.Json, Ct))!;

        await using var scope = _context.Factory.Services.CreateAsyncScope();
        var handler = new AutomationWikiPagesDeletedHandler(
            scope.ServiceProvider.GetRequiredService<AutomationDbContext>(),
            scope.ServiceProvider.GetRequiredService<AmbientCurrentTenant>());
        await handler.HandleAsync(new WikiPagesDeleted(_organization.Id, _project.Id, [page.Id]), Ct);

        var stillThere = await _owner.GetFromJsonAsync<PlaybookView>(
            $"{Base(_project)}/playbooks/{playbook.Id}", ApiTestContext.Json, Ct);
        Assert.Null(stillThere!.WikiPageId);
    }

    [Fact]
    public async Task members_read_admins_write_and_non_members_see_404()
    {
        var page = await CreatePageAsync(_project, "Permissions");
        Assert.Equal(HttpStatusCode.OK, (await _member.GetAsync($"{Base(_project)}/playbooks", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _member.PostAsJsonAsync($"{Base(_project)}/playbooks",
            new CreatePlaybookRequest("No", page.Id, "claude", null, null, 60), ApiTestContext.Json, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _stranger.GetAsync($"{Base(_project)}/playbooks", Ct)).StatusCode);
    }

    [Fact]
    public async Task factory_settings_round_trip_with_compare_and_swap()
    {
        var empty = await _owner.GetFromJsonAsync<FactorySettingsView>(
            $"{Base(_project)}/factory-settings", ApiTestContext.Json, Ct);
        Assert.Equal((short)ProjectRepositorySource.RunnerLocal, empty!.RepoSource);
        Assert.Equal((uint)0, empty.Version);

        var savedResponse = await _owner.PutAsJsonAsync($"{Base(_project)}/factory-settings",
            new UpdateFactorySettingsRequest((short)ProjectRepositorySource.GitHubBinding, "aictiq/aictiq", "main", null, null, 0),
            ApiTestContext.Json, Ct);
        savedResponse.EnsureSuccessStatusCode();
        var saved = (await savedResponse.Content.ReadFromJsonAsync<FactorySettingsView>(ApiTestContext.Json, Ct))!;
        Assert.Equal("aictiq/aictiq", saved.RepoFullName);
        Assert.True(saved.Version > 0);

        var stale = await _owner.PutAsJsonAsync($"{Base(_project)}/factory-settings",
            new UpdateFactorySettingsRequest((short)ProjectRepositorySource.RunnerLocal, null, "main", "/srv/aictiq", null, 0),
            ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }

    private string Base(ProjectView project) => $"/api/v1/orgs/{Slug}/projects/{project.Key}";

    private async Task<ProjectView> CreateProjectAsync(string name, string key)
    {
        var response = await _owner.PostAsJsonAsync($"/api/v1/orgs/{Slug}/projects",
            new CreateProjectRequest(name, key, null, ProjectVisibility.Organization, null, null), ApiTestContext.Json, Ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProjectView>(ApiTestContext.Json, Ct))!;
    }

    private async Task<WikiPageView> CreatePageAsync(ProjectView project, string title)
    {
        var response = await _owner.PostAsJsonAsync($"{Base(project)}/wiki/pages",
            new CreateWikiPageRequest(null, title, "Instructions"), ApiTestContext.Json, Ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<WikiPageView>(ApiTestContext.Json, Ct))!;
    }

    private async Task<List<WorkflowView>> WorkflowsAsync(ProjectView project) =>
        (await _owner.GetFromJsonAsync<List<WorkflowView>>(
            $"{Base(project)}/workflows", ApiTestContext.Json, Ct))!;

    private async Task AddOrganizationMemberAsync(string userId, OrgRole role)
    {
        await using var connection = new NpgsqlConnection(_context.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("""
            INSERT INTO tenancy.organization_members
                (organization_id, user_id, role, joined_at, can_operate_factory)
            VALUES (@organization_id, @user_id, @role, now(), false)
            """, connection);
        command.Parameters.AddWithValue("organization_id", _organization.Id);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("role", (int)role);
        await command.ExecuteNonQueryAsync(Ct);
    }
}

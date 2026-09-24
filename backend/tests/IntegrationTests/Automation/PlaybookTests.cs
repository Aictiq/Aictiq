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
            new CreatePlaybookRequest("Wrong state", null, "codex", otherState, null, 60, "Do it."), ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, wrongState.StatusCode);
    }

    [Fact]
    public async Task instructions_written_on_the_playbook_live_in_the_factory_section_and_nowhere_else()
    {
        // Home is made by a Workers handler this test host does not run; any page outside the
        // section is refused the same way.
        var home = await CreatePageAsync(_project, "Home");
        var onHome = await _owner.PostAsJsonAsync($"{Base(_project)}/playbooks",
            new CreatePlaybookRequest("On home", home.Id, "claude", null, null), ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, onHome.StatusCode);
        Assert.Contains("Factory section", await onHome.Content.ReadAsStringAsync(Ct));
        var empty = await _owner.PostAsJsonAsync($"{Base(_project)}/playbooks",
            new CreatePlaybookRequest("Empty", null, "claude", null, null), ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);

        var created = await _owner.PostAsJsonAsync($"{Base(_project)}/playbooks",
            new CreatePlaybookRequest("Triage", null, "codex", null, null, 30, "# Triage\n\nLabel the bug."), ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var playbook = (await created.Content.ReadFromJsonAsync<PlaybookView>(ApiTestContext.Json, Ct))!;
        var tree = await TreeAsync(_project);
        var section = tree.Single(x => x.Slug == "factory");
        var page = tree.Single(x => x.Id == playbook.WikiPageId);
        Assert.Equal("Triage", page.Title);
        Assert.Equal(section.Id, page.ParentId);

        // A second playbook joins the same section; a taken name writes no page at all.
        var taken = await _owner.PostAsJsonAsync($"{Base(_project)}/playbooks",
            new CreatePlaybookRequest("triage", null, "codex", null, null, 30, "Again."), ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        var another = await _owner.PostAsJsonAsync($"{Base(_project)}/playbooks",
            new CreatePlaybookRequest("Label", null, "codex", null, null, 30, "Label it."), ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.Created, another.StatusCode);
        tree = await TreeAsync(_project);
        Assert.Single(tree, x => x.Slug == "factory");
        Assert.Equal(2, tree.Count(x => x.ParentId == section.Id));

        var instructions = (await _owner.GetFromJsonAsync<PlaybookInstructionsView>(
            $"{Base(_project)}/playbooks/{playbook.Id}/instructions", ApiTestContext.Json, Ct))!;
        Assert.Equal("# Triage\n\nLabel the bug.", instructions.Markdown);
        Assert.True(instructions.InFactorySection);
        Assert.Equal(HttpStatusCode.Forbidden, (await _member.GetAsync($"{Base(_project)}/playbooks/{playbook.Id}/instructions", Ct)).StatusCode);

        // Editing them is a new revision of the same page.
        var updated = await _owner.PatchAsJsonAsync($"{Base(_project)}/playbooks/{playbook.Id}",
            new UpdatePlaybookRequest { InstructionsMarkdown = "# Triage\n\nLabel and prioritise the bug.", Version = playbook.Version }, ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal(playbook.WikiPageId, (await updated.Content.ReadFromJsonAsync<PlaybookView>(ApiTestContext.Json, Ct))!.WikiPageId);
        var revisions = (await _owner.GetFromJsonAsync<List<WikiRevisionView>>(
            $"/api/v1/orgs/{Slug}/wiki/pages/{playbook.WikiPageId}/revisions", ApiTestContext.Json, Ct))!;
        Assert.Equal(2, revisions.Count);

        // The page cannot leave the section from the wiki either.
        var wikiPage = (await _owner.GetFromJsonAsync<WikiPageView>($"/api/v1/orgs/{Slug}/wiki/pages/{playbook.WikiPageId}", ApiTestContext.Json, Ct))!;
        var moved = await _owner.PostAsJsonAsync($"/api/v1/orgs/{Slug}/wiki/pages/{wikiPage.Id}/move",
            new MoveWikiPageRequest(null, 0, wikiPage.Version), ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, moved.StatusCode);
    }

    [Fact]
    public async Task a_playbook_on_home_from_before_the_rule_moves_into_the_factory_section_when_its_instructions_are_saved()
    {
        var created = await _owner.PostAsJsonAsync($"{Base(_otherProject)}/playbooks",
            new CreatePlaybookRequest("Legacy", null, "claude", null, null, 60, "Placeholder."), ApiTestContext.Json, Ct);
        created.EnsureSuccessStatusCode();
        var playbook = (await created.Content.ReadFromJsonAsync<PlaybookView>(ApiTestContext.Json, Ct))!;
        var home = await CreatePageAsync(_otherProject, "Home");
        await using (var connection = new NpgsqlConnection(_context.ConnectionString))
        {
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand("UPDATE automation.playbooks SET wiki_page_id = @page WHERE id = @id", connection);
            command.Parameters.AddWithValue("page", home.Id);
            command.Parameters.AddWithValue("id", playbook.Id);
            await command.ExecuteNonQueryAsync(Ct);
        }
        playbook = (await _owner.GetFromJsonAsync<PlaybookView>($"{Base(_otherProject)}/playbooks/{playbook.Id}", ApiTestContext.Json, Ct))!;
        Assert.False((await _owner.GetFromJsonAsync<PlaybookInstructionsView>(
            $"{Base(_otherProject)}/playbooks/{playbook.Id}/instructions", ApiTestContext.Json, Ct))!.InFactorySection);

        // Other edits leave it be.
        var renamed = await _owner.PatchAsJsonAsync($"{Base(_otherProject)}/playbooks/{playbook.Id}",
            new UpdatePlaybookRequest { Name = "Legacy build", Version = playbook.Version }, ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        playbook = (await renamed.Content.ReadFromJsonAsync<PlaybookView>(ApiTestContext.Json, Ct))!;
        Assert.Equal(home.Id, playbook.WikiPageId);

        var saved = await _owner.PatchAsJsonAsync($"{Base(_otherProject)}/playbooks/{playbook.Id}",
            new UpdatePlaybookRequest { InstructionsMarkdown = "Build it.", Version = playbook.Version }, ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var moved = (await saved.Content.ReadFromJsonAsync<PlaybookView>(ApiTestContext.Json, Ct))!;
        Assert.NotEqual(home.Id, moved.WikiPageId);
        var instructions = (await _owner.GetFromJsonAsync<PlaybookInstructionsView>(
            $"{Base(_otherProject)}/playbooks/{playbook.Id}/instructions", ApiTestContext.Json, Ct))!;
        Assert.True(instructions.InFactorySection);
        Assert.Equal("Build it.", instructions.Markdown);
        Assert.Equal("Legacy build", instructions.PageTitle);
        // Home keeps what it had.
        Assert.Equal("Home", (await _owner.GetFromJsonAsync<WikiPageView>($"/api/v1/orgs/{Slug}/wiki/pages/{home.Id}", ApiTestContext.Json, Ct))!.Title);
    }

    [Fact]
    public async Task promoting_a_playbook_demotes_the_incumbent_and_the_database_prevents_two_defaults()
    {
        var starterResponse = await _owner.PostAsync($"{Base(_project)}/playbooks/starter", null, Ct);
        starterResponse.EnsureSuccessStatusCode();
        var starter = (await starterResponse.Content.ReadFromJsonAsync<PlaybookView>(ApiTestContext.Json, Ct))!;
        var created = await _owner.PostAsJsonAsync($"{Base(_project)}/playbooks",
            new CreatePlaybookRequest("Review", null, "opencode", null, null, 90, "Review the change."), ApiTestContext.Json, Ct);
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
        var created = await _owner.PostAsJsonAsync($"{Base(_project)}/playbooks",
            new CreatePlaybookRequest("Disposable", null, "claude", null, null, 60, "Disposable instructions"), ApiTestContext.Json, Ct);
        created.EnsureSuccessStatusCode();
        var playbook = (await created.Content.ReadFromJsonAsync<PlaybookView>(ApiTestContext.Json, Ct))!;
        var page = new { Id = playbook.WikiPageId!.Value };

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
        Assert.Equal(HttpStatusCode.OK, (await _member.GetAsync($"{Base(_project)}/playbooks", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _member.PostAsJsonAsync($"{Base(_project)}/playbooks",
            new CreatePlaybookRequest("No", null, "claude", null, null, 60, "No."), ApiTestContext.Json, Ct)).StatusCode);
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

    private async Task<List<WikiTreePageView>> TreeAsync(ProjectView project) =>
        (await _owner.GetFromJsonAsync<List<WikiTreePageView>>($"{Base(project)}/wiki/tree", ApiTestContext.Json, Ct))!;

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

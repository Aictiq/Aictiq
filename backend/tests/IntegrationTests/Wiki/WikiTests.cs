using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Npgsql;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.Tenancy.Contracts;
using Aictiq.Modules.Tenancy.Domain;
using Aictiq.Modules.Wiki.Domain;
using Aictiq.Modules.Tenancy.Endpoints;
using Aictiq.Modules.Wiki;
using Aictiq.Modules.Wiki.Endpoints;
using Aictiq.Modules.Wiki.Events;
using Aictiq.Modules.WorkItems.Domain;
using Aictiq.Modules.WorkItems;
using Aictiq.Modules.WorkItems.Endpoints;
using Aictiq.Modules.WorkItems.Events;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Storage;
using Aictiq.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;

namespace Aictiq.IntegrationTests.Wiki;

[Trait("Category", "Wiki")]
public sealed class WikiTests(PostgresFixture postgres, GarageFixture garage) : IAsyncLifetime
{
    private ApiTestContext _context = null!;
    private HttpClient _client = null!;
    private ProjectView _project = null!;
    private OrganizationView _organization = null!;
    private string _userId = null!;
    private CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _context = await ApiTestContext.CreateAsync(postgres, garage, "wiki");
        var auth = await _context.RegisterAsync($"wiki-{Guid.NewGuid():N}@test.local");
        _client = _context.ClientFor(auth);
        _userId = auth.User.Id;
        var organization = await _client.PostAsJsonAsync("/api/v1/orgs", new CreateOrganizationRequest("Wiki tests", "wiki-tests", null, null), ApiTestContext.Json, CancellationToken);
        organization.EnsureSuccessStatusCode();
        _organization = (await organization.Content.ReadFromJsonAsync<OrganizationView>(ApiTestContext.Json, CancellationToken))!;
        var project = await _client.PostAsJsonAsync("/api/v1/orgs/wiki-tests/projects", new CreateProjectRequest("Wiki project", "WIKI", null, ProjectVisibility.Organization, null, null), ApiTestContext.Json, CancellationToken);
        project.EnsureSuccessStatusCode();
        _project = (await project.Content.ReadFromJsonAsync<ProjectView>(ApiTestContext.Json, CancellationToken))!;
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _context.DisposeAsync();
    }

    [Fact]
    public async Task pages_deduplicate_slugs_render_safely_and_enforce_compare_and_swap()
    {
        var first = await CreateAsync("API reference", "# API\n\n<script>alert('xss')</script>\n\n- [x] done\n\n| A | B |\n|---|---|\n| 1 | 2 |");
        var second = await CreateAsync("API reference", "second");
        Assert.Equal("api-reference", first.Slug);
        Assert.Equal("api-reference-2", second.Slug);
        Assert.DoesNotContain("<script", first.ContentHtml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<table>", first.ContentHtml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("task-list", first.ContentHtml, StringComparison.OrdinalIgnoreCase);

        var update = await _client.PatchAsJsonAsync(PagePath(first.Id), new UpdateWikiPageRequest(null, "changed", first.Version, "First edit"), ApiTestContext.Json, CancellationToken);
        update.EnsureSuccessStatusCode();
        var revised = (await update.Content.ReadFromJsonAsync<WikiPageView>(ApiTestContext.Json, CancellationToken))!;
        Assert.Equal(2, revised.RevisionNumber);
        var stale = await _client.PatchAsJsonAsync(PagePath(first.Id), new UpdateWikiPageRequest(null, "stale", first.Version, null), ApiTestContext.Json, CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }

    [Fact]
    public async Task a_page_can_be_nested_under_one_created_moments_ago()
    {
        // Each Create below warms the per-person access cache before the next one runs, so
        // a cache that is not invalidated on create answers 404 for the newest parent.
        var root = await CreateAsync("Handbook", "root");
        var section = await CreateAsync("Onboarding", "section", root.Id);
        var leaf = await CreateAsync("First week", "leaf", section.Id);
        Assert.Equal(section.Id, leaf.ParentId);

        var read = await _client.GetAsync(PagePath(leaf.Id), CancellationToken);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
    }

    [Fact]
    public async Task deleting_a_page_removes_it_its_subpages_their_history_and_attachments_for_good()
    {
        var root = await CreateAsync("Architecture", "root");
        var child = await CreateAsync("Decisions", "child", root.Id);
        var edit = await _client.PatchAsJsonAsync(PagePath(child.Id), new UpdateWikiPageRequest(null, "child, edited", child.Version, null), ApiTestContext.Json, CancellationToken);
        edit.EnsureSuccessStatusCode();
        var attachment = await AttachToPageAsync(child.Id);

        var delete = await _client.DeleteAsync(PagePath(root.Id), CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync(PagePath(child.Id), CancellationToken)).StatusCode);
        Assert.Equal(0L, await CountAsync("SELECT count(*) FROM wiki.pages WHERE id = ANY(@ids)", root.Id, child.Id));
        Assert.Equal(0L, await CountAsync("SELECT count(*) FROM wiki.page_revisions WHERE page_id = ANY(@ids)", root.Id, child.Id));
        Assert.Equal(1L, await CountAsync("SELECT count(*) FROM shared.outbox_messages WHERE type = 'Aictiq.SharedKernel.Contracts.WikiPagesDeleted' AND payload::text LIKE '%' || @id || '%'", child.Id));

        // What the outbox delivers to WorkItems - twice, as at-least-once delivery may.
        for (var delivery = 0; delivery < 2; delivery++)
        {
            await using var scope = _context.Factory.Services.CreateAsyncScope();
            await new WikiPagesDeletedHandler(scope.ServiceProvider.GetRequiredService<WorkItemsDbContext>(),
                    scope.ServiceProvider.GetRequiredService<AmbientCurrentTenant>(), scope.ServiceProvider.GetRequiredService<IBlobStorage>())
                .HandleAsync(new WikiPagesDeleted(_organization.Id, _project.Id, [root.Id, child.Id]), CancellationToken);
        }
        Assert.Equal(0L, await CountAsync("SELECT count(*) FROM work.attachments WHERE id = ANY(@ids)", attachment));

        // The name is free again: nothing hidden still holds the slug.
        Assert.Equal("architecture", (await CreateAsync("Architecture", "again")).Slug);
    }

    [Fact]
    public async Task a_page_cannot_be_deleted_with_a_subpage_the_caller_may_not_write()
    {
        var colleague = await _context.RegisterAsync($"wiki-colleague-{Guid.NewGuid():N}@test.local");
        await using (var connection = new NpgsqlConnection(_context.ConnectionString))
        {
            await connection.OpenAsync(CancellationToken);
            await using var member = new NpgsqlCommand("INSERT INTO tenancy.organization_members (organization_id, user_id, role, joined_at, can_operate_factory) VALUES (@org, @user, @role, now(), @role <> 3)", connection);
            member.Parameters.AddWithValue("org", _organization.Id);
            member.Parameters.AddWithValue("user", colleague.User.Id);
            member.Parameters.AddWithValue("role", (int)OrgRole.Member);
            await member.ExecuteNonQueryAsync(CancellationToken);
        }
        using var colleagueClient = _context.ClientFor(colleague);
        var root = await CreateAsync("Team notes", "root");
        var restricted = await CreateAsync("Salaries", "private", root.Id);
        var rules = await _client.PutAsJsonAsync($"{PagePath(restricted.Id)}/permissions",
            new ReplaceWikiPagePermissionsRequest([new WikiPagePermissionView(WikiPermissionSubjectKind.User, _userId, WikiPermissionAccess.Write)]), ApiTestContext.Json, CancellationToken);
        rules.EnsureSuccessStatusCode();

        var delete = await colleagueClient.DeleteAsync(PagePath(root.Id), CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
        Assert.Equal(2L, await CountAsync("SELECT count(*) FROM wiki.pages WHERE id = ANY(@ids)", root.Id, restricted.Id));
    }

    [Fact]
    public async Task a_page_rule_cannot_lift_a_project_guest_above_read()
    {
        // An organization Member who is only a Guest on this private project: past every
        // route filter, so the page rule is the only thing deciding.
        var (guestId, guest) = await JoinAsync(OrgRole.Member);
        using var _ = guest;
        var created = await _client.PostAsJsonAsync("/api/v1/orgs/wiki-tests/projects", new CreateProjectRequest("Private wiki", "PWIKI", null, ProjectVisibility.Private, null, null), ApiTestContext.Json, CancellationToken);
        created.EnsureSuccessStatusCode();
        (await _client.PutAsJsonAsync($"/api/v1/orgs/wiki-tests/projects/PWIKI/members/{guestId}", new UpdateProjectMemberRequest(ProjectRole.Guest), ApiTestContext.Json, CancellationToken)).EnsureSuccessStatusCode();
        var response = await _client.PostAsJsonAsync("/api/v1/orgs/wiki-tests/projects/PWIKI/wiki/pages", new CreateWikiPageRequest(null, "Guest notes", "original"), ApiTestContext.Json, CancellationToken);
        response.EnsureSuccessStatusCode();
        var page = (await response.Content.ReadFromJsonAsync<WikiPageView>(ApiTestContext.Json, CancellationToken))!;
        await RestrictAsync(page.Id, new WikiPagePermissionView(WikiPermissionSubjectKind.User, guestId, WikiPermissionAccess.Write),
            new WikiPagePermissionView(WikiPermissionSubjectKind.User, _userId, WikiPermissionAccess.Write));

        // Guests read and comment: a rule naming one opens the page to them, but "write" on a
        // page is not a way past the ceiling of the role.
        Assert.Equal(HttpStatusCode.OK, (await guest.GetAsync(PagePath(page.Id), CancellationToken)).StatusCode);
        var edit = await guest.PatchAsJsonAsync(PagePath(page.Id), new UpdateWikiPageRequest(null, "defaced", page.Version, null), ApiTestContext.Json, CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, edit.StatusCode);
        var unchanged = await _client.GetFromJsonAsync<WikiPageView>(PagePath(page.Id), ApiTestContext.Json, CancellationToken);
        Assert.Equal("original", unchanged!.ContentMarkdown);
    }

    [Fact]
    public async Task demoting_an_admin_closes_a_restricted_page_at_once()
    {
        var (adminId, admin) = await JoinAsync(OrgRole.Admin);
        using var _ = admin;
        var page = await CreateAsync("Board minutes", "restricted");
        await RestrictAsync(page.Id, new WikiPagePermissionView(WikiPermissionSubjectKind.ProjectRole, "admin", WikiPermissionAccess.Write));
        // Warms the per-person access snapshot while they are still an Admin.
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(PagePath(page.Id), CancellationToken)).StatusCode);

        var demote = await _client.PutAsJsonAsync($"/api/v1/orgs/wiki-tests/members/{adminId}", new UpdateMemberRequest(OrgRole.Member), ApiTestContext.Json, CancellationToken);
        demote.EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync(PagePath(page.Id), CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task a_restricted_page_s_attachments_are_hidden_with_it()
    {
        var (_, colleague) = await JoinAsync(OrgRole.Member);
        using var __ = colleague;
        var page = await CreateAsync("Contracts", "see attachment");
        var attachment = await AttachToPageAsync(page.Id);
        var download = $"/api/v1/orgs/wiki-tests/attachments/{attachment}/download";
        Assert.Equal(HttpStatusCode.OK, (await colleague.GetAsync(download, CancellationToken)).StatusCode);

        await RestrictAsync(page.Id, new WikiPagePermissionView(WikiPermissionSubjectKind.User, _userId, WikiPermissionAccess.Write));

        Assert.Equal(HttpStatusCode.NotFound, (await colleague.GetAsync(download, CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync(download, CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task an_archived_project_s_pages_are_read_only_through_their_own_routes()
    {
        var page = await CreateAsync("Frozen", "before");
        var edited = await _client.PatchAsJsonAsync(PagePath(page.Id), new UpdateWikiPageRequest(null, "second", page.Version, null), ApiTestContext.Json, CancellationToken);
        edited.EnsureSuccessStatusCode();
        page = (await edited.Content.ReadFromJsonAsync<WikiPageView>(ApiTestContext.Json, CancellationToken))!;
        (await _client.PostAsync($"/api/v1/orgs/wiki-tests/projects/{_project.Key}/archive", null, CancellationToken)).EnsureSuccessStatusCode();

        foreach (var response in new[]
        {
            await _client.PatchAsJsonAsync(PagePath(page.Id), new UpdateWikiPageRequest(null, "after", page.Version, null), ApiTestContext.Json, CancellationToken),
            await _client.PostAsJsonAsync($"{PagePath(page.Id)}/move", new MoveWikiPageRequest(null, 0, page.Version), ApiTestContext.Json, CancellationToken),
            await _client.PostAsync($"{PagePath(page.Id)}/revisions/1/restore", null, CancellationToken),
            await _client.PutAsJsonAsync($"{PagePath(page.Id)}/permissions", new ReplaceWikiPagePermissionsRequest([]), ApiTestContext.Json, CancellationToken),
            await _client.DeleteAsync(PagePath(page.Id), CancellationToken),
        })
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Contains("project-archived", await response.Content.ReadAsStringAsync(CancellationToken));
        }

        var unchanged = await _client.GetFromJsonAsync<WikiPageView>(PagePath(page.Id), ApiTestContext.Json, CancellationToken);
        Assert.Equal("second", unchanged!.ContentMarkdown);
    }

    [Fact]
    public async Task a_new_projects_home_page_is_readable_and_a_replay_creates_nothing()
    {
        // Run the handler the outbox would: the Home page must come out with its revision
        // wired up, which a trailing UPDATE in the same statement silently failed to do.
        var created = new ProjectCreated(_organization.Id, _project.Id, _project.Key, _project.Name, "wiki-home-test");
        for (var delivery = 0; delivery < 2; delivery++)
        {
            await using var scope = _context.Factory.Services.CreateAsyncScope();
            var handler = new WikiProjectCreatedHandler(scope.ServiceProvider.GetRequiredService<WikiDbContext>(),
                scope.ServiceProvider.GetRequiredService<AmbientCurrentTenant>());
            await handler.HandleAsync(created, CancellationToken);
        }

        var tree = await _client.GetFromJsonAsync<List<WikiTreePageView>>($"/api/v1/orgs/wiki-tests/projects/{_project.Key}/wiki/tree", ApiTestContext.Json, CancellationToken);
        var home = Assert.Single(tree!, page => page.Slug == "home");
        var read = await _client.GetAsync(PagePath(home.Id), CancellationToken);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var revisions = await _client.GetFromJsonAsync<List<WikiRevisionView>>($"{PagePath(home.Id)}/revisions", ApiTestContext.Json, CancellationToken);
        Assert.True(Assert.Single(revisions!).IsCurrent);
    }

    [Fact]
    public async Task revision_rows_are_immutable_and_the_database_rejects_cycles()
    {
        var root = await CreateAsync("Root", "root");
        var child = await CreateAsync("Child", "child", root.Id);
        await using var connection = new NpgsqlConnection(_context.ConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var revision = new NpgsqlCommand("UPDATE wiki.page_revisions SET summary = 'nope' WHERE page_id = @pageId", connection);
        revision.Parameters.AddWithValue("pageId", root.Id);
        var immutable = await Assert.ThrowsAsync<PostgresException>(() => revision.ExecuteNonQueryAsync(CancellationToken));
        Assert.Equal("P0001", immutable.SqlState);
        await using var bareDelete = new NpgsqlCommand("DELETE FROM wiki.page_revisions WHERE page_id = @pageId", connection);
        bareDelete.Parameters.AddWithValue("pageId", root.Id);
        Assert.Equal("P0001", (await Assert.ThrowsAsync<PostgresException>(() => bareDelete.ExecuteNonQueryAsync(CancellationToken))).SqlState);

        await using var cycle = new NpgsqlCommand("UPDATE wiki.pages SET parent_id = @parentId WHERE id = @pageId", connection);
        cycle.Parameters.AddWithValue("parentId", child.Id);
        cycle.Parameters.AddWithValue("pageId", root.Id);
        var rejected = await Assert.ThrowsAsync<PostgresException>(() => cycle.ExecuteNonQueryAsync(CancellationToken));
        Assert.Equal("P0001", rejected.SqlState);
        Assert.Equal("wiki_cycle", rejected.MessageText);
    }

    [Fact]
    public async Task revision_history_diff_and_restore_are_append_only()
    {
        var page = await CreateAsync("Release notes", "line one\nline two");
        var update = await _client.PatchAsJsonAsync(PagePath(page.Id), new UpdateWikiPageRequest(null, "line one\nchanged line", page.Version, "Clarify"), ApiTestContext.Json, CancellationToken);
        update.EnsureSuccessStatusCode();
        var head = (await update.Content.ReadFromJsonAsync<WikiPageView>(ApiTestContext.Json, CancellationToken))!;

        var history = await _client.GetFromJsonAsync<List<WikiRevisionView>>($"{PagePath(page.Id)}/revisions", ApiTestContext.Json, CancellationToken);
        Assert.Collection(history!, latest => { Assert.Equal(2, latest.Number); Assert.Equal("Clarify", latest.Summary); Assert.True(latest.IsCurrent); Assert.Equal(latest.AuthorId, latest.Author.Id); Assert.NotEqual("Unknown user", latest.Author.DisplayName); },
            original => Assert.Equal(1, original.Number));
        var diffResponse = await _client.GetAsync($"{PagePath(page.Id)}/diff?from=1&to=2", CancellationToken);
        Assert.True(diffResponse.IsSuccessStatusCode, await diffResponse.Content.ReadAsStringAsync(CancellationToken));
        var diff = await diffResponse.Content.ReadFromJsonAsync<WikiDiffView>(ApiTestContext.Json, CancellationToken);
        Assert.Contains(diff!.Hunks.SelectMany(x => x.Lines), x => x.Kind == "deleted" && x.Text == "line two");
        Assert.Contains(diff.Hunks.SelectMany(x => x.Lines), x => x.Kind == "inserted" && x.Text == "changed line");

        var restore = await _client.PostAsync($"{PagePath(page.Id)}/revisions/1/restore", null, CancellationToken);
        restore.EnsureSuccessStatusCode();
        var restored = (await restore.Content.ReadFromJsonAsync<WikiPageView>(ApiTestContext.Json, CancellationToken))!;
        Assert.Equal(3, restored.RevisionNumber);
        Assert.Equal("line one\nline two", restored.ContentMarkdown);
        var repeat = await _client.PostAsync($"{PagePath(page.Id)}/revisions/3/restore", null, CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, repeat.StatusCode);
    }

    [Fact]
    public async Task markdown_over_one_megabyte_is_rejected_with_payload_too_large()
    {
        var response = await _client.PostAsJsonAsync($"/api/v1/orgs/wiki-tests/projects/{_project.Key}/wiki/pages",
            new CreateWikiPageRequest(null, "Huge", new string('x', WikiPageEndpoints.MaxMarkdownLength + 1)), ApiTestContext.Json, CancellationToken);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task project_search_returns_page_content_alongside_work_item_groups()
    {
        var page = await CreateAsync("Deployment runbook", "Run the deployment checklist before releasing.");
        var result = await _client.GetFromJsonAsync<SearchResponse>($"/api/v1/orgs/wiki-tests/projects/{_project.Key}/search?q=deployment", ApiTestContext.Json, CancellationToken);
        var found = Assert.Single(result!.Pages);
        Assert.Equal(page.Id, found.Id);
        Assert.Contains("<mark>deployment</mark>", found.Snippet, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task page_item_links_are_replaced_and_backlinks_are_exposed()
    {
        var item = await CreateItemAsync("Referenced item");
        var page = await CreateAsync("Spec", $"See {item.Key} for details.");
        var linksResponse = await _client.GetAsync($"/api/v1/orgs/wiki-tests/items/{item.Key}/wiki-backlinks", CancellationToken);
        Assert.True(linksResponse.IsSuccessStatusCode, await linksResponse.Content.ReadAsStringAsync(CancellationToken));
        var links = await linksResponse.Content.ReadFromJsonAsync<List<WikiItemBacklinkView>>(ApiTestContext.Json, CancellationToken);
        var backlink = Assert.Single(links!); Assert.Equal(page.Id, backlink.PageId);

        var update = await _client.PatchAsJsonAsync(PagePath(page.Id), new UpdateWikiPageRequest(null, "No references now.", page.Version, null), ApiTestContext.Json, CancellationToken);
        update.EnsureSuccessStatusCode();
        var removed = await _client.GetFromJsonAsync<List<WikiItemBacklinkView>>($"/api/v1/orgs/wiki-tests/items/{item.Key}/wiki-backlinks", ApiTestContext.Json, CancellationToken);
        Assert.Empty(removed!);

        var summary = await _client.GetFromJsonAsync<List<WorkItemSummaryView>>($"/api/v1/orgs/wiki-tests/projects/{_project.Key}/items/summary?keys={item.Key}", ApiTestContext.Json, CancellationToken);
        Assert.Collection(summary!, value => Assert.Equal(item.Key, value.Key));
    }

    private async Task<(string UserId, HttpClient Client)> JoinAsync(OrgRole role)
    {
        var person = await _context.RegisterAsync($"wiki-{role.ToString().ToLowerInvariant()}-{Guid.NewGuid():N}@test.local");
        await using var connection = new NpgsqlConnection(_context.ConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var member = new NpgsqlCommand("INSERT INTO tenancy.organization_members (organization_id, user_id, role, joined_at, can_operate_factory) VALUES (@org, @user, @role, now(), @role <> 3)", connection);
        member.Parameters.AddWithValue("org", _organization.Id);
        member.Parameters.AddWithValue("user", person.User.Id);
        member.Parameters.AddWithValue("role", (int)role);
        await member.ExecuteNonQueryAsync(CancellationToken);
        return (person.User.Id, _context.ClientFor(person));
    }

    private async Task RestrictAsync(Guid pageId, params WikiPagePermissionView[] rules)
    {
        var response = await _client.PutAsJsonAsync($"{PagePath(pageId)}/permissions", new ReplaceWikiPagePermissionsRequest(rules), ApiTestContext.Json, CancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task<WikiPageView> CreateAsync(string title, string content, Guid? parentId = null)
    {
        var response = await _client.PostAsJsonAsync($"/api/v1/orgs/wiki-tests/projects/{_project.Key}/wiki/pages",
            new CreateWikiPageRequest(parentId, title, content), ApiTestContext.Json, CancellationToken);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(CancellationToken));
        return (await response.Content.ReadFromJsonAsync<WikiPageView>(ApiTestContext.Json, CancellationToken))!;
    }

    private async Task<WorkItemView> CreateItemAsync(string title)
    {
        var response = await _client.PostAsJsonAsync($"/api/v1/orgs/wiki-tests/projects/{_project.Key}/items/",
            new CreateWorkItemRequest(WorkItemType.Bug, title, "", null, null, null, null, null, null, null, null, null, null, null), ApiTestContext.Json, CancellationToken);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(CancellationToken));
        return (await response.Content.ReadFromJsonAsync<WorkItemView>(ApiTestContext.Json, CancellationToken))!;
    }

    private async Task<Guid> AttachToPageAsync(Guid pageId)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent("notes"u8.ToArray());
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        form.Add(file, "file", "notes.txt");
        var upload = await _client.PostAsync($"/api/v1/orgs/wiki-tests/projects/{_project.Key}/attachments", form, CancellationToken);
        Assert.True(upload.IsSuccessStatusCode, await upload.Content.ReadAsStringAsync(CancellationToken));
        var attachment = (await upload.Content.ReadFromJsonAsync<AttachmentView>(ApiTestContext.Json, CancellationToken))!;
        var commit = await _client.PostAsJsonAsync($"/api/v1/orgs/wiki-tests/attachments/{attachment.Id}/commit", new CommitAttachmentRequest(null, null, pageId), ApiTestContext.Json, CancellationToken);
        Assert.True(commit.IsSuccessStatusCode, await commit.Content.ReadAsStringAsync(CancellationToken));
        return attachment.Id;
    }

    private async Task<long> CountAsync(string sql, params Guid[] ids)
    {
        await using var connection = new NpgsqlConnection(_context.ConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        if (sql.Contains("@ids")) command.Parameters.AddWithValue("ids", ids);
        else command.Parameters.AddWithValue("id", ids[0].ToString());
        return (long)(await command.ExecuteScalarAsync(CancellationToken))!;
    }

    private static string PagePath(Guid pageId) => $"/api/v1/orgs/wiki-tests/wiki/pages/{pageId}";
}

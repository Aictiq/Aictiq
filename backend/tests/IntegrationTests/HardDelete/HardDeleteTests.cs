using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Aictiq.IntegrationTests.Billing;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.Analytics;
using Aictiq.Modules.Automation;
using Aictiq.Modules.Automation.Endpoints;
using Aictiq.Modules.Billing;
using Aictiq.Modules.Billing.Payments;
using Aictiq.Modules.Identity;
using Aictiq.Modules.Integrations;
using Aictiq.Modules.Notifications;
using Aictiq.Modules.Tenancy;
using Aictiq.Modules.Tenancy.Domain;
using Aictiq.Modules.Tenancy.Endpoints;
using Aictiq.Modules.Wiki;
using Aictiq.Modules.Wiki.Endpoints;
using Aictiq.Modules.WorkItems;
using Aictiq.Modules.WorkItems.Domain;
using Aictiq.Modules.WorkItems.Endpoints;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Email;
using Aictiq.SharedKernel.Outbox;
using Aictiq.SharedKernel.Persistence;
using Aictiq.SharedKernel.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Aictiq.IntegrationTests.HardDelete;

/// <summary>
/// Items, projects and organizations are deleted for good, and "for good" is checked the
/// way an auditor would: after the API answers and the Workers' outbox has drained, every
/// table in every module schema is searched for the deleted ids, and the object store for the
/// deleted prefixes. A neighbour that was not deleted must still be whole.
/// </summary>
[Trait("Category", "HardDelete")]
public sealed class HardDeleteTests(PostgresFixture postgres, GarageFixture garage) : IAsyncLifetime
{
    private static readonly string[] Schemas = ["tenancy", "work", "wiki", "analytics", "notify", "integrations", "billing", "automation", "identity", "audit"];

    private ApiTestContext _context = null!;
    private ServiceProvider _workers = null!;
    private FakeStripeGateway _stripe = null!;
    private HttpClient _client = null!;
    private string _userId = null!;
    private OrganizationView _organization = null!;
    private CancellationToken Ct => TestContext.Current.CancellationToken;
    private string Slug => _organization.Slug;

    public async ValueTask InitializeAsync()
    {
        _context = await ApiTestContext.CreateAsync(postgres, garage, "harddelete");
        _stripe = new FakeStripeGateway();
        _workers = BuildWorkers(_context, garage, _stripe);
        var auth = await _context.RegisterAsync($"deleter-{Guid.NewGuid():N}@test.local");
        _client = _context.ClientFor(auth);
        _userId = auth.User.Id;
        var organization = await _client.PostAsJsonAsync("/api/v1/orgs", new CreateOrganizationRequest("Doomed", "doomed", null, null), ApiTestContext.Json, Ct);
        organization.EnsureSuccessStatusCode();
        _organization = (await organization.Content.ReadFromJsonAsync<OrganizationView>(ApiTestContext.Json, Ct))!;
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _workers.DisposeAsync();
        await _context.DisposeAsync();
    }

    [Fact]
    public async Task deleting_an_item_removes_it_and_everything_below_it_from_every_module_and_the_store()
    {
        var project = await SeedProjectAsync("Website", "WEB");
        var survivorKey = project.Survivor.Key;

        var preview = (await _client.GetFromJsonAsync<ItemDeletePreviewView>(ItemPath(project.Epic.Key, "delete-preview"), ApiTestContext.Json, Ct))!;
        Assert.Equal(project.Story.Key, Assert.Single(preview.Descendants).Key);
        Assert.Equal(1, preview.Comments);
        Assert.Equal(1, preview.Attachments);

        var delete = await _client.DeleteAsync(ItemPath(project.Epic.Key), Ct);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync(ItemPath(project.Story.Key), Ct)).StatusCode);
        await DrainOutboxAsync();

        Guid[] doomed = [project.Epic.Id, project.Story.Id];
        Assert.Empty(await ReferencesAsync(["item_id", "source_id", "target_id", "parent_id"], doomed));
        Assert.Empty(await ReferencesAsync(["id"], doomed));
        Assert.Empty(await ReferencesAsync(["comment_id"], [project.CommentId]));
        Assert.False(await Storage.ExistsAsync(project.ItemObjectKey, Ct));

        // One content-free line in the audit log says it happened; nothing it said survives.
        Assert.Equal(["(deleted)"], await AuditFieldsAsync("WorkItem", doomed));
        Assert.Equal(0L, await CountAsync("SELECT count(*) FROM audit.audit_log WHERE entity_id = @id::text", project.Story.Id));

        // The item it was related to is untouched, and no longer related to anything.
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync(ItemPath(survivorKey), Ct)).StatusCode);
        Assert.True(await Storage.ExistsAsync(project.PageObjectKey, Ct));
    }

    [Fact]
    public async Task deleting_a_project_removes_everything_in_it_from_every_module_and_the_store()
    {
        var doomed = await SeedProjectAsync("Website", "WEB");
        var kept = await SeedProjectAsync("Mobile", "MOB");
        var stray = $"org/{_organization.Id}/project/{doomed.ProjectId}/attachments/{Guid.NewGuid()}/stray.txt";
        await Storage.PutAsync(stray, new MemoryStream("no row knows me"u8.ToArray()), "text/plain", Ct);
        var invitation = await InviteAsync(doomed.ProjectId);

        var mistyped = await _client.SendAsync(DeleteRequest($"/api/v1/orgs/{Slug}/projects/WEB", "Websit"), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, mistyped.StatusCode);
        var delete = await _client.SendAsync(DeleteRequest($"/api/v1/orgs/{Slug}/projects/WEB", "website"), Ct);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/v1/orgs/{Slug}/projects/WEB", Ct)).StatusCode);
        await DrainOutboxAsync();

        Assert.Empty(await ReferencesAsync(["project_id"], [doomed.ProjectId]));
        Assert.Empty(await ReferencesAsync(["team_id"], doomed.TeamIds));
        Assert.Empty(await ReferencesAsync(["sprint_id"], [doomed.SprintId]));
        Assert.Empty(await ReferencesAsync(["item_id", "source_id", "target_id", "page_id", "subscription_id"],
            [doomed.Epic.Id, doomed.Story.Id, doomed.Survivor.Id, doomed.PageId, doomed.WebhookId]));
        Assert.Empty(await ReferencesAsync(["id"], [doomed.ProjectId, doomed.Epic.Id, doomed.PageId, doomed.WebhookId, doomed.SprintId, .. doomed.TeamIds]));
        Assert.Equal(0, await Storage.DeletePrefixAsync($"org/{_organization.Id}/project/{doomed.ProjectId}/", Ct));

        Assert.Equal(["(deleted)"], await AuditFieldsAsync("Project", [doomed.ProjectId]));
        Assert.Empty(await AuditFieldsAsync("WorkItem", [doomed.Epic.Id, doomed.Story.Id]));
        Assert.Empty(await AuditFieldsAsync("WikiPage", [doomed.PageId]));
        Assert.Empty(await AuditFieldsAsync("Team", doomed.TeamIds));

        // The invitation still invites to the organization; only the project part is gone.
        Assert.Equal(1L, await CountAsync("SELECT count(*) FROM tenancy.invitations WHERE id = @id AND project_id IS NULL AND project_role IS NULL", invitation));

        // The neighbour is whole, down to its stored objects.
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync(ItemPath(kept.Story.Key), Ct)).StatusCode);
        Assert.True(await Storage.ExistsAsync(kept.ItemObjectKey, Ct));
        Assert.True(await Storage.ExistsAsync(kept.PageObjectKey, Ct));
        Assert.NotEmpty(await ReferencesAsync(["project_id"], [kept.ProjectId]));

        // With everything gone, the key and the name are free for a new project.
        var again = await _client.PostAsJsonAsync($"/api/v1/orgs/{Slug}/projects", new CreateProjectRequest("Website", "WEB", null, ProjectVisibility.Organization, null, null), ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.Created, again.StatusCode);
    }

    [Fact]
    public async Task only_a_project_admin_may_delete_a_project()
    {
        await SeedProjectAsync("Website", "WEB", seedModules: false);
        var member = await _context.RegisterAsync($"member-{Guid.NewGuid():N}@test.local");
        await ExecuteAsync("INSERT INTO tenancy.organization_members (organization_id, user_id, role, joined_at) VALUES (@org, @user, 2, now())",
            ("org", _organization.Id), ("user", member.User.Id));
        using var memberClient = _context.ClientFor(member);

        var response = await memberClient.SendAsync(DeleteRequest($"/api/v1/orgs/{Slug}/projects/WEB", "Website"), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task deleting_an_organization_leaves_nothing_but_its_retired_slug()
    {
        var project = await SeedProjectAsync("Website", "WEB");
        var agent = await CreateAgentAsync();
        var runner = await _client.PostAsJsonAsync($"/api/v1/orgs/{Slug}/runners", new CreateRunnerRequest("vps-1"), ApiTestContext.Json, Ct);
        Assert.True(runner.IsSuccessStatusCode, await runner.Content.ReadAsStringAsync(Ct));
        await ExecuteAsync("""
            INSERT INTO billing.subscriptions (id, plan, status, stripe_customer_id, stripe_subscription_id, cancel_at_period_end, seats_human, seats_agent, created_at, updated_at, organization_id)
            VALUES (gen_random_uuid(), 'team', 3, 'cus_doomed', 'sub_doomed', false, 1, 0, now(), now(), @org);
            INSERT INTO billing.usage_snapshots (organization_id, day, humans, agents, projects, storage_bytes) VALUES (@org, current_date, 1, 1, 1, 10);
            INSERT INTO integrations.github_installations (id, installation_id, account_login, account_type, status, created_at, updated_at, organization_id)
            VALUES (gen_random_uuid(), 424242, 'doomed', 'Organization', 0, now(), now(), @org);
            """, ("org", _organization.Id));
        // Delivered before the deletion, as they would be long before anyone deletes an
        // organization. A message delivered in the same batch as the deletion is not yet marked
        // delivered when the purge runs, and waits for outbox retention instead.
        await DrainOutboxAsync();

        var delete = await _client.SendAsync(DeleteRequest($"/api/v1/orgs/{Slug}", "Doomed"), Ct);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/v1/orgs/{Slug}", Ct)).StatusCode);
        await DrainOutboxAsync();

        Assert.Empty(await ReferencesAsync(["organization_id"], [_organization.Id]));
        Assert.Empty(await ReferencesAsync(["id", "project_id"], [_organization.Id, project.ProjectId]));
        Assert.Equal(0L, await CountAsync("SELECT count(*) FROM audit.audit_log WHERE organization_id = @id OR entity_id = @id::text", _organization.Id));
        Assert.Equal(0, await Storage.DeletePrefixAsync($"org/{_organization.Id}/", Ct));
        // Delivered events said what the organization and its projects were called; only the
        // deletion's own message, delivered last, still names it.
        Assert.Equal(0L, await CountAsync("SELECT count(*) FROM shared.outbox_messages WHERE payload ->> 'OrganizationId' = @id::text AND type <> 'Aictiq.SharedKernel.Contracts.OrganizationDeleted'", _organization.Id));

        // The agent was the organization's; the person who deleted it keeps their account.
        Assert.Empty(await ReferencesAsync(["user_id", "id"], agent));
        Assert.Equal(1L, await CountAsync("SELECT count(*) FROM identity.\"AspNetUsers\" WHERE id = @id::text", Guid.Parse(_userId)));
        Assert.Equal(["sub_doomed"], _stripe.ImmediateCancellations.ToArray());

        // The slug is all that is left, and it stays taken - in the endpoint and in the database.
        Assert.Equal(1L, await CountAsync("SELECT count(*) FROM tenancy.retired_slugs WHERE slug = 'doomed' AND @unused IS NOT NULL", Guid.Empty));
        var reuse = await _client.PostAsJsonAsync("/api/v1/orgs", new CreateOrganizationRequest("Doomed again", "doomed", null, null), ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, reuse.StatusCode);
        var raw = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            "INSERT INTO tenancy.organizations (id, slug, name, plan, settings, created_by, created_at, updated_at) VALUES (gen_random_uuid(), 'doomed', 'Sneaky', 'self_hosted', '{}', 'x', now(), now())"));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, raw.SqlState);
        var derived = await _client.PostAsJsonAsync("/api/v1/orgs", new CreateOrganizationRequest("Doomed", null, null, null), ApiTestContext.Json, Ct);
        derived.EnsureSuccessStatusCode();
        Assert.NotEqual("doomed", (await derived.Content.ReadFromJsonAsync<OrganizationView>(ApiTestContext.Json, Ct))!.Slug);
    }

    [Fact]
    public async Task history_stays_append_only_outside_a_purge()
    {
        var project = await SeedProjectAsync("Website", "WEB", seedModules: false);

        foreach (var sql in new[]
        {
            "DELETE FROM work.item_history WHERE item_id = @id",
            "DELETE FROM work.comment_revisions WHERE comment_id IN (SELECT id FROM work.comments WHERE item_id = @id)",
            "DELETE FROM work.items WHERE id = @id",
            "DELETE FROM audit.audit_log WHERE entity_id = @id::text",
        })
        {
            var refused = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(sql, ("id", project.Story.Id)));
            Assert.Equal("P0001", refused.SqlState);
        }
    }

    // ── seeding ──────────────────────────────────────────────────────────────────────

    private sealed record SeededProject(
        Guid ProjectId, Guid[] TeamIds, WorkItemView Epic, WorkItemView Story, WorkItemView Survivor,
        Guid CommentId, string ItemObjectKey, Guid PageId, string PageObjectKey, Guid SprintId, Guid WebhookId);

    /// <summary>
    /// A project with something in every place a project keeps things: an epic with a story
    /// below it (comment and its revision, label, relation, watcher, attachment), a wiki page
    /// that links the story and has its own attachment, a sprint, and - written directly,
    /// because they are fed by background work - analytics, notifications and integrations rows.
    /// </summary>
    private async Task<SeededProject> SeedProjectAsync(string name, string key, bool seedModules = true)
    {
        var created = await _client.PostAsJsonAsync($"/api/v1/orgs/{Slug}/projects", new CreateProjectRequest(name, key, null, ProjectVisibility.Organization, null, null), ApiTestContext.Json, Ct);
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync(Ct));
        var project = (await created.Content.ReadFromJsonAsync<ProjectView>(ApiTestContext.Json, Ct))!;
        var teams = (await _client.GetFromJsonAsync<List<TeamView>>($"/api/v1/orgs/{Slug}/projects/{key}/teams", ApiTestContext.Json, Ct))!;

        var epic = await CreateItemAsync(key, WorkItemType.Epic, $"{name} epic", null);
        var story = await CreateItemAsync(key, WorkItemType.Story, $"{name} story", epic.Id);
        var survivor = await CreateItemAsync(key, WorkItemType.Bug, $"{name} bug", null);

        var comment = await _client.PostAsJsonAsync(ItemPath(story.Key, "comments"), new CreateCommentRequest("First thought"), ApiTestContext.Json, Ct);
        comment.EnsureSuccessStatusCode();
        var commentId = (await comment.Content.ReadFromJsonAsync<CommentView>(ApiTestContext.Json, Ct))!.Id;
        (await _client.PatchAsJsonAsync(ItemPath(story.Key, $"comments/{commentId}"), new UpdateCommentRequest("Second thought"), ApiTestContext.Json, Ct)).EnsureSuccessStatusCode();
        var label = await _client.PostAsJsonAsync($"/api/v1/orgs/{Slug}/projects/{key}/labels", new CreateLabelRequest("backend", "#336699", null, null), ApiTestContext.Json, Ct);
        label.EnsureSuccessStatusCode();
        var labelId = (await label.Content.ReadFromJsonAsync<LabelView>(ApiTestContext.Json, Ct))!.Id;
        (await _client.PutAsync(ItemPath(story.Key, $"labels/{labelId}"), null, Ct)).EnsureSuccessStatusCode();
        (await _client.PutAsJsonAsync(ItemPath(story.Key, "relations"), new PutItemRelationRequest(survivor.Key, ItemRelationKind.Related), ApiTestContext.Json, Ct)).EnsureSuccessStatusCode();
        (await _client.PutAsync(ItemPath(story.Key, "watch"), null, Ct)).EnsureSuccessStatusCode();
        var itemObject = await AttachAsync(key, new CommitAttachmentRequest(story.Id, null));

        var page = await _client.PostAsJsonAsync($"/api/v1/orgs/{Slug}/projects/{key}/wiki/pages", new CreateWikiPageRequest(null, "Notes", $"See {story.Key}."), ApiTestContext.Json, Ct);
        page.EnsureSuccessStatusCode();
        var pageId = (await page.Content.ReadFromJsonAsync<WikiPageView>(ApiTestContext.Json, Ct))!.Id;
        var pageObject = await AttachAsync(key, new CommitAttachmentRequest(null, null, pageId));

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var sprint = await _client.PostAsJsonAsync($"/api/v1/orgs/{Slug}/teams/{teams[0].Id}/sprints", new CreateSprintRequest("Sprint 1", null, today, today.AddDays(13)), ApiTestContext.Json, Ct);
        Assert.True(sprint.IsSuccessStatusCode, await sprint.Content.ReadAsStringAsync(Ct));
        var sprintId = (await sprint.Content.ReadFromJsonAsync<SprintView>(ApiTestContext.Json, Ct))!.Id;

        var webhookId = Guid.NewGuid();
        await DrainOutboxAsync();
        if (seedModules)
        {
            await ExecuteAsync("""
                INSERT INTO analytics.item_transitions (event_id, project_id, item_id, from_state_id, to_state_id, sprint_id, actor_id, at, organization_id)
                VALUES (gen_random_uuid(), @project, @story, gen_random_uuid(), gen_random_uuid(), @sprint, @user, now(), @org);
                INSERT INTO analytics.item_state_daily (organization_id, item_id, day, project_id, state_id, captured_at)
                VALUES (@org, @story, current_date, @project, gen_random_uuid(), now());
                INSERT INTO analytics.sprint_scope_log (event_id, sprint_id, item_id, added, at, organization_id)
                VALUES (gen_random_uuid(), @sprint, @survivor, true, now(), @org);
                INSERT INTO analytics.dashboards (id, project_id, owner_user_id, name, layout_json, is_default, created_at, updated_at, organization_id)
                VALUES (gen_random_uuid(), @project, @user, 'Mine', '{}', false, now(), now(), @org);
                INSERT INTO notify.notifications (id, user_id, event_id, kind, project_id, item_id, item_key, message, created_at, organization_id)
                VALUES (gen_random_uuid(), @user, gen_random_uuid(), 1, @project, @story, 'X-1', 'You were mentioned', now(), @org);
                INSERT INTO integrations.webhook_subscriptions (id, project_id, url, secret_hash, secret_protected, events, active, consecutive_failures, created_at, updated_at, organization_id)
                VALUES (@webhook, @project, 'https://example.com/hook', 'h', 'p', ARRAY['item.updated'], true, 0, now(), now(), @org);
                INSERT INTO integrations.webhook_deliveries (id, subscription_id, event_id, event_name, payload, attempt, status, created_at, next_attempt_at, organization_id)
                VALUES (gen_random_uuid(), @webhook, gen_random_uuid(), 'item.updated', '{"title":"secret"}', 1, 'delivered', now(), now(), @org);
                INSERT INTO integrations.repo_bindings (id, project_id, installation_id, repo_id, full_name, created_at, organization_id)
                VALUES (gen_random_uuid(), @project, 1, abs(hashtext(@project::text)), 'acme/web', now(), @org);
                """,
                ("project", project.Id), ("story", story.Id), ("survivor", survivor.Id), ("sprint", sprintId),
                ("user", _userId), ("org", _organization.Id), ("webhook", webhookId));
        }

        if (seedModules)
        {
            // The deletion checks below only mean something if there was something to delete.
            var present = await ReferencesAsync(["item_id"], [story.Id]);
            foreach (var table in new[] { "wiki.page_item_links", "work.item_history", "work.comments", "work.attachments",
                "work.item_labels", "work.watchers", "analytics.item_transitions", "notify.notifications" })
                Assert.Contains(present, row => row.StartsWith(table + ".", StringComparison.Ordinal));
            Assert.Contains(await ReferencesAsync(["comment_id"], [commentId]), row => row.StartsWith("work.comment_revisions.", StringComparison.Ordinal));
            Assert.NotEmpty(await AuditFieldsAsync("WorkItem", [story.Id]));
        }

        return new SeededProject(project.Id, [.. teams.Select(t => t.Id)], epic, story, survivor, commentId,
            itemObject, pageId, pageObject, sprintId, webhookId);
    }

    private async Task<WorkItemView> CreateItemAsync(string projectKey, WorkItemType type, string title, Guid? parentId)
    {
        var response = await _client.PostAsJsonAsync($"/api/v1/orgs/{Slug}/projects/{projectKey}/items/",
            new CreateWorkItemRequest(type, title, "Something only this item says", null, null, null, null, parentId, null, null, null, null, null, null),
            ApiTestContext.Json, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<WorkItemView>(ApiTestContext.Json, Ct))!;
    }

    /// <summary>Uploads and commits a file, and returns the object key it was stored under.</summary>
    private async Task<string> AttachAsync(string projectKey, CommitAttachmentRequest target)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent("notes"u8.ToArray());
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        form.Add(file, "file", "notes.txt");
        var upload = await _client.PostAsync($"/api/v1/orgs/{Slug}/projects/{projectKey}/attachments", form, Ct);
        Assert.True(upload.IsSuccessStatusCode, await upload.Content.ReadAsStringAsync(Ct));
        var attachment = (await upload.Content.ReadFromJsonAsync<AttachmentView>(ApiTestContext.Json, Ct))!;
        var commit = await _client.PostAsJsonAsync($"/api/v1/orgs/{Slug}/attachments/{attachment.Id}/commit", target, ApiTestContext.Json, Ct);
        Assert.True(commit.IsSuccessStatusCode, await commit.Content.ReadAsStringAsync(Ct));
        await using var connection = new NpgsqlConnection(_context.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT object_key FROM work.attachments WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", attachment.Id);
        var key = (string)(await command.ExecuteScalarAsync(Ct))!;
        Assert.True(await Storage.ExistsAsync(key, Ct));
        return key;
    }

    private async Task<Guid> InviteAsync(Guid projectId)
    {
        var id = Guid.NewGuid();
        await ExecuteAsync("""
            INSERT INTO tenancy.invitations (id, email, role, project_id, project_role, token_hash, invited_by, created_at, expires_at, organization_id)
            VALUES (@id, 'someone@test.local', 2, @project, 2, decode(md5(@id::text), 'hex'), @user, now(), now() + interval '7 days', @org)
            """, ("id", id), ("project", projectId), ("user", _userId), ("org", _organization.Id));
        return id;
    }

    private async Task<Guid[]> CreateAgentAsync()
    {
        var response = await _client.PostAsJsonAsync($"/api/v1/orgs/{Slug}/agents", new CreateAgentRequest("Robot", null), ApiTestContext.Json, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        var agent = (await response.Content.ReadFromJsonAsync<AgentView>(ApiTestContext.Json, Ct))!;
        var token = await _client.PostAsJsonAsync($"/api/v1/orgs/{Slug}/agents/{agent.UserId}/tokens", new CreateAgentTokenRequest("ci", ["read"], null), ApiTestContext.Json, Ct);
        Assert.True(token.IsSuccessStatusCode, await token.Content.ReadAsStringAsync(Ct));
        await Storage.PutAsync($"users/{agent.UserId}/avatar/{Guid.NewGuid()}", new MemoryStream([1, 2, 3]), "image/webp", Ct);
        return [Guid.Parse(agent.UserId)];
    }

    // ── checks ───────────────────────────────────────────────────────────────────────

    private IBlobStorage Storage => _context.Factory.Services.GetRequiredService<IBlobStorage>();

    /// <summary>Every table, in every module schema, that has one of the columns and a row naming one of the ids.</summary>
    private async Task<List<string>> ReferencesAsync(string[] columns, IEnumerable<Guid> ids)
    {
        var values = ids.Select(id => id.ToString()).ToArray();
        await using var connection = new NpgsqlConnection(_context.ConnectionString);
        await connection.OpenAsync(Ct);
        var tables = new List<(string Schema, string Table, string Column)>();
        await using (var find = new NpgsqlCommand("""
            SELECT table_schema, table_name, column_name FROM information_schema.columns
            WHERE table_schema = ANY(@schemas) AND column_name = ANY(@columns)
              AND table_name NOT LIKE '%ef_migrations%'
            """, connection))
        {
            find.Parameters.AddWithValue("schemas", Schemas);
            find.Parameters.AddWithValue("columns", columns);
            await using var reader = await find.ExecuteReaderAsync(Ct);
            while (await reader.ReadAsync(Ct)) tables.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        var found = new List<string>();
        foreach (var (schema, table, column) in tables)
        {
            await using var count = new NpgsqlCommand($"SELECT count(*) FROM \"{schema}\".\"{table}\" WHERE \"{column}\"::text = ANY(@values)", connection);
            count.Parameters.AddWithValue("values", values);
            var rows = (long)(await count.ExecuteScalarAsync(Ct))!;
            if (rows > 0) found.Add($"{schema}.{table}.{column}: {rows}");
        }
        return found;
    }

    private async Task<List<string>> AuditFieldsAsync(string entityType, IEnumerable<Guid> ids)
    {
        await using var connection = new NpgsqlConnection(_context.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("""
            SELECT field FROM audit.audit_log
            WHERE entity_type = @type AND split_part(entity_id, '/', 1) = ANY(@ids) ORDER BY at
            """, connection);
        command.Parameters.AddWithValue("type", entityType);
        command.Parameters.AddWithValue("ids", ids.Select(id => id.ToString()).ToArray());
        var fields = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct)) fields.Add(reader.GetString(0));
        return fields;
    }

    private async Task<long> CountAsync(string sql, Guid id)
    {
        await using var connection = new NpgsqlConnection(_context.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        if (sql.Contains("@unused")) command.Parameters.AddWithValue("unused", id);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    private async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(_context.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static HttpRequestMessage DeleteRequest(string path, string name) =>
        new(HttpMethod.Delete, path) { Content = JsonContent.Create(new { name }) };

    private string ItemPath(string key, string? suffix = null) =>
        $"/api/v1/orgs/{Slug}/items/{key}{(suffix is null ? "" : $"/{suffix}")}";

    /// <summary>Runs every pending outbox message through the Workers' handlers, and fails on any that failed.</summary>
    private async Task DrainOutboxAsync()
    {
        var processor = _workers.GetRequiredService<OutboxProcessor>();
        for (var round = 0; round < 20 && await processor.ProcessPendingAsync(Ct) > 0; round++)
        {
        }

        await using var connection = new NpgsqlConnection(_context.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var failed = new NpgsqlCommand("SELECT type || ': ' || last_error FROM shared.outbox_messages WHERE processed_at IS NULL", connection);
        var errors = new List<string>();
        await using (var reader = await failed.ExecuteReaderAsync(Ct))
            while (await reader.ReadAsync(Ct)) errors.Add(reader.GetString(0));
        Assert.True(errors.Count == 0, "Outbox handlers failed: " + string.Join(" | ", errors));
    }

    /// <summary>The Workers process's composition (Workers/Program.cs) against the API's database and store.</summary>
    private static ServiceProvider BuildWorkers(ApiTestContext context, GarageFixture garage, FakeStripeGateway stripe)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["S3:Endpoint"] = garage.S3Endpoint,
            ["S3:Bucket"] = GarageFixture.Bucket,
            ["S3:AccessKey"] = GarageFixture.AccessKey,
            ["S3:SecretKey"] = GarageFixture.SecretKey,
            ["S3:Region"] = "garage",
            ["S3:ForcePathStyle"] = "true",
        }).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton(NpgsqlDataSource.Create(context.ConnectionString));
        services.AddSharedKernel();
        services.AddBlobStorage(configuration);
        services.AddEmail(configuration);
        services.AddHybridCache();
        services.AddSingleton<ICurrentUser, SystemCurrentUser>();
        services.AddIdentityModule(configuration);
        services.AddIdentityWorkers(configuration);
        services.AddTenancyModule();
        services.AddBillingModule(configuration);
        services.AddBillingWorkers();
        services.AddNotificationsModule();
        services.AddNotificationsWorkers(configuration);
        services.AddWorkItemsModule();
        services.AddWorkItemsWorkers();
        services.AddWikiModule();
        services.AddWikiWorkers();
        services.AddIntegrationsModule(configuration);
        services.AddIntegrationsWorkers();
        services.AddAnalyticsModule(configuration);
        services.AddAnalyticsWorkers(configuration);
        services.AddAutomationModule(configuration);
        services.AddAutomationWorkers();
        services.AddOutboxProcessor(
            typeof(SendEmailRequested).Assembly,
            typeof(TenancyDbContext).Assembly,
            typeof(WorkItemsDbContext).Assembly,
            typeof(WikiDbContext).Assembly,
            typeof(IntegrationsDbContext).Assembly);
        services.RemoveAll<IStripeGateway>();
        services.AddSingleton<IStripeGateway>(stripe);
        return services.BuildServiceProvider();
    }
}

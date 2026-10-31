using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.Tenancy.Domain;
using Aictiq.Modules.Tenancy.Endpoints;
using Aictiq.Modules.WorkItems.Domain;
using Aictiq.Modules.WorkItems.Endpoints;
using Aictiq.Modules.WorkItems.Jira;
using Aictiq.Modules.WorkItems.Workers;
using Aictiq.SharedKernel.Authorization;

namespace Aictiq.IntegrationTests.WorkItems;

/// <summary>
/// The one-time Jira Cloud import end to end against a fake Jira: the connection check, the
/// preview's mapping, and a worker run that brings issues across with their parent links,
/// comments, attachments and sprints - then a second run that creates nothing twice. Jira is a
/// fake handler; everything between the endpoint and the HTTP call is the production code.
/// </summary>
[Trait("Category", "WorkItems")]
public sealed class JiraImportTests(PostgresFixture postgres, GarageFixture garage) : IAsyncLifetime
{
    private const string Slug = "jira-co";
    private const string Site = "https://acme.atlassian.net";
    private const string Token = "jira-test-token-0123456789";

    private readonly FakeJira _jira = new();
    private ApiTestContext _context = null!;
    private HttpClient _client = null!;
    private ProjectView _project = null!;
    private Guid _organizationId;
    private string _memberEmail = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private string Base => $"/api/v1/orgs/{Slug}/projects/{_project.Key}";

    public async ValueTask InitializeAsync()
    {
        _context = await ApiTestContext.CreateAsync(postgres, garage, "jira_import",
            configureServices: services => services.AddHttpClient(JiraClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _jira));
        var owner = await _context.RegisterAsync($"jira-owner-{Guid.NewGuid():N}@test.local", "Olga", "Owner");
        _client = _context.ClientFor(owner);
        var organization = await _client.PostAsJsonAsync("/api/v1/orgs", new CreateOrganizationRequest("Jira Co", Slug, null, null), ApiTestContext.Json, Ct);
        organization.EnsureSuccessStatusCode();
        var project = await _client.PostAsJsonAsync($"/api/v1/orgs/{Slug}/projects",
            new CreateProjectRequest("Imported", "IMP", null, ProjectVisibility.Organization, null, null), ApiTestContext.Json, Ct);
        project.EnsureSuccessStatusCode();
        _project = (await project.Content.ReadFromJsonAsync<ProjectView>(ApiTestContext.Json, Ct))!;
        _organizationId = await ScalarAsync<Guid>("SELECT id FROM tenancy.organizations WHERE slug = @p0", Slug);

        _memberEmail = $"marta-{Guid.NewGuid():N}@test.local";
        var member = await _context.RegisterAsync(_memberEmail, "Marta", "Member");
        await AddOrgMemberAsync(member.User.Id, OrgRole.Member);
        _jira.MemberEmail = _memberEmail;
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _context.DisposeAsync();
    }

    [Fact]
    public async Task a_refused_token_or_a_non_jira_site_is_a_clear_error_and_imports_nothing()
    {
        var refused = await _client.PostAsJsonAsync($"{Base}/import/jira/connect", new { siteUrl = Site, email = "me@acme.test", apiToken = "wrong" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("did not accept this email and API token", await refused.Content.ReadAsStringAsync(Ct));

        var elsewhere = await _client.PostAsJsonAsync($"{Base}/import/jira/connect", new { siteUrl = "https://169.254.169.254", email = "me@acme.test", apiToken = Token }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, elsewhere.StatusCode);
        Assert.Contains("Only Jira Cloud sites", await elsewhere.Content.ReadAsStringAsync(Ct));

        var start = await _client.PostAsJsonAsync($"{Base}/import/jira", new { siteUrl = Site, email = "me@acme.test", apiToken = "wrong", jiraProjectKey = "ACME" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, start.StatusCode);
        Assert.Equal(0L, await ScalarAsync<long>("SELECT count(*) FROM work.jira_import_jobs"));
    }

    [Fact]
    public async Task a_guest_cannot_use_the_import()
    {
        var guest = await _context.RegisterAsync($"jira-guest-{Guid.NewGuid():N}@test.local", "Gus", "Guest");
        await AddOrgMemberAsync(guest.User.Id, OrgRole.Guest);
        using var client = _context.ClientFor(guest);
        var response = await client.PostAsJsonAsync($"{Base}/import/jira/connect", new { siteUrl = Site, email = "me@acme.test", apiToken = Token }, Ct);
        Assert.True(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound, $"Expected a refusal, got {response.StatusCode}");
    }

    [Fact]
    public async Task an_import_brings_issues_comments_attachments_and_sprints_across_once()
    {
        var connect = await _client.PostAsJsonAsync($"{Base}/import/jira/connect", new { siteUrl = "acme.atlassian.net/jira/software/projects/ACME/boards/1", email = "me@acme.test", apiToken = Token }, Ct);
        connect.EnsureSuccessStatusCode();
        var connection = (await connect.Content.ReadFromJsonAsync<JiraConnectionView>(ApiTestContext.Json, Ct))!;
        Assert.Equal(Site, connection.SiteUrl);
        Assert.Equal("ACME", Assert.Single(connection.Projects).Key);

        var preview = (await (await _client.PostAsJsonAsync($"{Base}/import/jira/preview", new { siteUrl = Site, email = "me@acme.test", apiToken = Token, jiraProjectKey = "ACME" }, Ct))
            .Content.ReadFromJsonAsync<JiraImportPreview>(ApiTestContext.Json, Ct))!;
        Assert.Equal(3, preview.IssueCount);
        Assert.Equal(WorkItemType.Epic, preview.IssueTypes.Single(x => x.Name == "Epic").SuggestedType);
        Assert.Equal(WorkItemType.Task, preview.IssueTypes.Single(x => x.Name == "Sub-task").SuggestedType);
        Assert.Contains(preview.Statuses, s => s.Name == "Done" && preview.States.Single(x => x.Id == s.SuggestedStateId).Category == WorkflowStateCategory.Completed);
        Assert.NotNull(preview.Users.Single(u => u.DisplayName == "Marta Jira").MatchedUserId);
        Assert.Null(preview.Users.Single(u => u.DisplayName == "Ghost Writer").MatchedUserId);

        var body = new
        {
            siteUrl = Site, email = "me@acme.test", apiToken = Token, jiraProjectKey = "ACME",
            issueTypes = preview.IssueTypes.ToDictionary(x => x.Name, x => x.SuggestedType),
            statuses = preview.Statuses.ToDictionary(x => x.Name, x => x.SuggestedStateId),
        };
        var start = await _client.PostAsJsonAsync($"{Base}/import/jira", body, ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);
        var queued = (await start.Content.ReadFromJsonAsync<JiraImportJobView>(ApiTestContext.Json, Ct))!;
        Assert.Equal(JiraImportStatus.Pending, queued.Status);
        Assert.DoesNotContain(Token, await ScalarAsync<string>("SELECT protected_token FROM work.jira_import_jobs WHERE id = @p0", queued.Id));

        Assert.True(await Worker().RunOnceAsync(Ct));
        var job = (await _client.GetFromJsonAsync<JiraImportJobView>($"{Base}/imports/jira/{queued.Id}", ApiTestContext.Json, Ct))!;
        Assert.True(job.Status == JiraImportStatus.Completed && job.CreatedItems == 3, JsonSerializer.Serialize(job.Failures));
        Assert.Equal((3, 3, 0, 2, 1), (job.ProcessedIssues, job.CreatedItems, job.SkippedIssues, job.CreatedComments, job.CreatedAttachments));
        Assert.Equal(2, job.CreatedSprints);
        var tooBig = Assert.Single(job.Failures);
        Assert.Equal(("ACME-2", "attachment"), (tooBig.IssueKey, tooBig.Kind));
        Assert.Contains("huge.zip", tooBig.Reason);
        Assert.True(await ScalarAsync<bool>("SELECT protected_token IS NULL FROM work.jira_import_jobs WHERE id = @p0", queued.Id));

        // Fields, parent links and people.
        var epic = await ItemAsync("ACME-1");
        var story = await ItemAsync("ACME-2");
        var subtask = await ItemAsync("ACME-3");
        Assert.Equal(((short)WorkItemType.Epic, (short)WorkItemType.Story, (short)WorkItemType.Task), (epic.Type, story.Type, subtask.Type));
        Assert.Equal(epic.Id, story.ParentId);
        Assert.Equal(story.Id, subtask.ParentId);
        Assert.Equal("Checkout flow", story.Title);
        Assert.Equal(5m, story.Points);
        Assert.Equal(2m, subtask.Estimate);
        Assert.NotNull(story.AssigneeId);
        Assert.Contains("**Pay** with a card", story.Description);
        Assert.Contains("- Visa", story.Description);
        Assert.Contains("Reporter in Jira: Ghost Writer (not a member of this project)", story.Description);
        Assert.Contains($"[ACME-2]({Site}/browse/ACME-2)", story.Description);
        Assert.Equal(new DateTimeOffset(2024, 1, 15, 9, 20, 30, TimeSpan.Zero), story.CreatedAt);
        Assert.Equal("backend", await ScalarAsync<string>("SELECT l.name FROM work.item_labels il JOIN work.labels l ON l.id = il.label_id WHERE il.item_id = @p0", story.Id));
        Assert.Equal((short)WorkflowStateCategory.Completed, await ScalarAsync<short>("SELECT s.category FROM work.workflow_states s WHERE s.id = @p0", epic.StateId));

        // Comments in order, with the outsider's under the importer and their name.
        var comments = await RowsAsync("SELECT author_id, body_markdown FROM work.comments WHERE item_id = @p0 ORDER BY created_at", story.Id);
        Assert.Equal(2, comments.Count);
        Assert.Equal(story.AssigneeId, comments[0].Item1);
        Assert.StartsWith("**Ghost Writer** wrote in Jira:", comments[1].Item2);

        // The attachment is the item's, its bytes stored, linked from the description; the credentials never left the site.
        var attachmentId = await ScalarAsync<Guid>("SELECT id FROM work.attachments WHERE item_id = @p0 AND status = 1", story.Id);
        Assert.Contains($"/api/v1/orgs/{Slug}/attachments/{attachmentId}/download", story.Description);
        Assert.Equal("release notes", await _client.GetStringAsync($"/api/v1/orgs/{Slug}/attachments/{attachmentId}/download", Ct));
        Assert.All(_jira.Requests.Where(r => r.Host != "acme.atlassian.net"), r => Assert.False(r.Authorized));
        Assert.Contains(_jira.Requests, r => r.Host == "api.media.atlassian.com");

        // Sprints keep name, dates and state; the story is in the active one.
        var sprint = await RowsAsync("SELECT name, state::text FROM work.sprints WHERE id = @p0", story.SprintId!.Value);
        Assert.Equal(("Sprint 2", ((short)SprintState.Active).ToString()), sprint.Single());
        Assert.Equal(new DateOnly(2024, 2, 1), await ScalarAsync<DateOnly>("SELECT starts_on FROM work.sprints WHERE id = @p0", story.SprintId!.Value));
        Assert.Equal(((short)SprintState.Completed).ToString(), (await RowsAsync("SELECT name, state::text FROM work.sprints WHERE name = @p0", "Sprint 1")).Single().Item2);

        // Again: the same issues are skipped, nothing is duplicated.
        var again = await _client.PostAsJsonAsync($"{Base}/import/jira", body, ApiTestContext.Json, Ct);
        var second = (await again.Content.ReadFromJsonAsync<JiraImportJobView>(ApiTestContext.Json, Ct))!;
        Assert.True(await Worker().RunOnceAsync(Ct));
        second = (await _client.GetFromJsonAsync<JiraImportJobView>($"{Base}/imports/jira/{second.Id}", ApiTestContext.Json, Ct))!;
        Assert.Equal((JiraImportStatus.Completed, 0, 3, 0), (second.Status, second.CreatedItems, second.SkippedIssues, second.CreatedSprints));
        Assert.Equal(3L, await ScalarAsync<long>("SELECT count(*) FROM work.items WHERE project_id = @p0", _project.Id));
        Assert.Equal(2L, await ScalarAsync<long>("SELECT count(*) FROM work.sprints WHERE external_ref IS NOT NULL"));
        Assert.Equal(2, (await _client.GetFromJsonAsync<List<JiraImportJobView>>($"{Base}/imports/jira", ApiTestContext.Json, Ct))!.Count);
    }

    private JiraImportWorker Worker() => new(_context.Factory.Services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System,
        Options.Create(new JiraImportOptions()), NullLogger<JiraImportWorker>.Instance);

    private sealed record ItemRow(Guid Id, short Type, string Title, string Description, Guid? ParentId, Guid? SprintId, string? AssigneeId, decimal? Points, decimal? Estimate, Guid StateId, DateTimeOffset CreatedAt);

    private async Task<ItemRow> ItemAsync(string externalRef)
    {
        await using var connection = new NpgsqlConnection(_context.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT id, type, title, description_markdown, parent_id, sprint_id, assignee_id, points, estimate_hours, state_id, created_at FROM work.items WHERE external_ref = @p0", connection);
        command.Parameters.AddWithValue("p0", externalRef);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        Assert.True(await reader.ReadAsync(Ct), $"{externalRef} was not imported");
        T? N<T>(int i) => reader.IsDBNull(i) ? default : reader.GetFieldValue<T>(i);
        return new ItemRow(reader.GetGuid(0), reader.GetInt16(1), reader.GetString(2), reader.GetString(3), N<Guid?>(4), N<Guid?>(5), N<string>(6), N<decimal?>(7), N<decimal?>(8), reader.GetGuid(9), reader.GetFieldValue<DateTimeOffset>(10));
    }

    private async Task<T> ScalarAsync<T>(string sql, params object[] values)
    {
        await using var connection = new NpgsqlConnection(_context.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        for (var i = 0; i < values.Length; i++) command.Parameters.AddWithValue($"p{i}", values[i]);
        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }

    private async Task<List<(string, string)>> RowsAsync(string sql, object value)
    {
        await using var connection = new NpgsqlConnection(_context.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("p0", value);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<(string, string)>();
        while (await reader.ReadAsync(Ct)) rows.Add((reader.GetString(0), reader.GetString(1)));
        return rows;
    }

    private async Task AddOrgMemberAsync(string userId, OrgRole role)
    {
        await using var connection = new NpgsqlConnection(_context.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "INSERT INTO tenancy.organization_members (organization_id, user_id, role, joined_at, can_operate_factory) VALUES (@org, @user, @role, now(), false)", connection);
        command.Parameters.AddWithValue("org", _organizationId);
        command.Parameters.AddWithValue("user", userId);
        command.Parameters.AddWithValue("role", (short)role);
        await command.ExecuteNonQueryAsync(Ct);
    }

    /// <summary>A Jira Cloud site with one project of three issues, and its media host.</summary>
    private sealed class FakeJira : HttpMessageHandler
    {
        public string MemberEmail { get; set; } = "";
        public ConcurrentQueue<(string Host, string Path, bool Authorized)> Requests { get; } = new();

        private static readonly string Expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"me@acme.test:{Token}"));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!;
            var authorized = request.Headers.Authorization?.ToString() == Expected;
            Requests.Enqueue((url.Host, url.AbsolutePath, request.Headers.Authorization is not null));
            if (url.Host == "api.media.atlassian.com") return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("release notes", Encoding.UTF8, "text/plain") };
            if (url.Host != "acme.atlassian.net") return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (!authorized) return Json(HttpStatusCode.Unauthorized, new { errorMessages = new[] { "Client must be authenticated" } });
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            return url.AbsolutePath switch
            {
                "/rest/api/3/myself" => Json(new { accountId = "me", displayName = "Importer" }),
                "/rest/api/3/project/search" => Json(new { values = new[] { new { id = "10000", key = "ACME", name = "Acme" } }, isLast = true }),
                "/rest/api/3/project/ACME" => Json(new { id = "10000", key = "ACME", name = "Acme" }),
                "/rest/api/3/project/ACME/statuses" => Json(new object[]
                {
                    new { name = "Epic", subtask = false, statuses = Statuses },
                    new { name = "Story", subtask = false, statuses = Statuses },
                    new { name = "Sub-task", subtask = true, statuses = Statuses },
                }),
                "/rest/api/3/search/approximate-count" => Json(new { count = 3 }),
                "/rest/api/3/field" => Json(new object[]
                {
                    new { id = "summary", name = "Summary", schema = new { type = "string" } },
                    new { id = "customfield_10016", name = "Story point estimate", schema = new { type = "number", custom = "com.atlassian.jira.plugin.system.customfieldtypes:jsw-story-points" } },
                    new { id = "customfield_10020", name = "Sprint", schema = new { type = "array", custom = "com.pyxis.greenhopper.jira:gh-sprint" } },
                }),
                "/rest/api/3/user/assignable/search" => Json(new object[] { Marta, Ghost }),
                "/rest/api/3/search/jql" => Json(body.Contains("\"p2\"", StringComparison.Ordinal)
                    // The sub-task comes before its parent story, so it has to wait for it.
                    ? new { issues = new[] { Story }, isLast = true }
                    : (object)new { issues = new[] { Subtask, Epic }, nextPageToken = "p2", isLast = false }),
                "/rest/api/3/issue/ACME-2/comment" => Json(new
                {
                    total = 2, startAt = 0, maxResults = 100,
                    comments = new object[]
                    {
                        new { id = "1", author = Marta, created = "2024-01-16T10:00:00.000+0000", body = Doc(Paragraph(Text("Looks good"))) },
                        new { id = "2", author = Ghost, created = "2024-01-17T10:00:00.000+0000", body = Doc(Paragraph(Text("Ship it"))) },
                    },
                }),
                "/rest/api/3/issue/ACME-1/comment" or "/rest/api/3/issue/ACME-3/comment" => Json(new { total = 0, startAt = 0, maxResults = 100, comments = Array.Empty<object>() }),
                "/rest/api/3/attachment/content/501" => new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://api.media.atlassian.com/file/501/binary?token=signed") } },
                _ => Json(HttpStatusCode.NotFound, new { errorMessages = new[] { $"No fake for {url.AbsolutePath}" } }),
            };
        }

        private object Marta => new { accountId = "acc-marta", displayName = "Marta Jira", emailAddress = MemberEmail };
        private static object Ghost => new { accountId = "acc-ghost", displayName = "Ghost Writer" };
        private static readonly object[] Statuses =
        [
            new { name = "To Do", statusCategory = new { key = "new" } },
            new { name = "In Progress", statusCategory = new { key = "indeterminate" } },
            new { name = "Done", statusCategory = new { key = "done" } },
        ];

        private static object Status(string name, string category) => new { name, statusCategory = new { key = category } };
        private static object Sprint(int id, string name, string state, string start, string end) => new { id, name, state, startDate = start, endDate = end, completeDate = state == "closed" ? end : null };

        private static object Epic => new
        {
            id = "1", key = "ACME-1",
            fields = new { summary = "Payments", issuetype = new { name = "Epic", subtask = false }, status = Status("Done", "done"), created = "2024-01-10T09:00:00.000+0000", resolutiondate = "2024-03-01T09:00:00.000+0000", labels = Array.Empty<string>(), attachment = Array.Empty<object>() },
        };

        private object Story => new Dictionary<string, object?>
        {
            ["id"] = "2", ["key"] = "ACME-2",
            ["fields"] = new Dictionary<string, object?>
            {
                ["summary"] = "Checkout flow",
                ["description"] = Doc(Paragraph(Text("Pay", "strong"), Text(" with a card")),
                    new { type = "bulletList", content = new[] { new { type = "listItem", content = new[] { Paragraph(Text("Visa")) } } } }),
                ["issuetype"] = new { name = "Story", subtask = false },
                ["status"] = Status("In Progress", "indeterminate"),
                ["assignee"] = Marta, ["reporter"] = Ghost,
                ["labels"] = new[] { "backend" },
                ["parent"] = new { key = "ACME-1" },
                ["created"] = "2024-01-15T10:20:30.000+0100",
                ["customfield_10016"] = 5,
                ["customfield_10020"] = new[] { Sprint(9, "Sprint 1", "closed", "2024-01-15T00:00:00.000Z", "2024-01-29T00:00:00.000Z"), Sprint(10, "Sprint 2", "active", "2024-02-01T00:00:00.000Z", "2024-02-15T00:00:00.000Z") },
                ["attachment"] = new object[]
                {
                    new { id = "501", filename = "notes.txt", mimeType = "text/plain", size = 13, content = $"{Site}/rest/api/3/attachment/content/501" },
                    new { id = "502", filename = "huge.zip", mimeType = "application/zip", size = 100L * 1024 * 1024, content = $"{Site}/rest/api/3/attachment/content/502" },
                },
            },
        };

        private static object Subtask => new Dictionary<string, object?>
        {
            ["id"] = "3", ["key"] = "ACME-3",
            ["fields"] = new Dictionary<string, object?>
            {
                ["summary"] = "Card form", ["issuetype"] = new { name = "Sub-task", subtask = true }, ["status"] = Status("To Do", "new"),
                ["parent"] = new { key = "ACME-2" }, ["created"] = "2024-01-20T10:00:00.000+0000", ["timeoriginalestimate"] = 7200,
                ["customfield_10020"] = new[] { Sprint(9, "Sprint 1", "closed", "2024-01-15T00:00:00.000Z", "2024-01-29T00:00:00.000Z") },
            },
        };

        private static object Doc(params object[] content) => new { type = "doc", version = 1, content };
        private static object Paragraph(params object[] content) => new { type = "paragraph", content };
        private static object Text(string text, string? mark = null) => mark is null ? new { type = "text", text } : new { type = "text", text, marks = new[] { new { type = mark } } };

        private static HttpResponseMessage Json(object body) => Json(HttpStatusCode.OK, body);
        private static HttpResponseMessage Json(HttpStatusCode status, object body) =>
            new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    }
}

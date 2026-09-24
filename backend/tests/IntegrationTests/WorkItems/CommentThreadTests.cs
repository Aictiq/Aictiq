using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.Notifications.Delivery;
using Aictiq.Modules.Notifications.Domain;
using Aictiq.Modules.Notifications.Events;
using Aictiq.Modules.Notifications.Templates;
using Aictiq.Modules.Tenancy.Contracts;
using Aictiq.Modules.Tenancy.Domain;
using Aictiq.Modules.Tenancy.Endpoints;
using Aictiq.Modules.WorkItems.Contracts;
using Aictiq.Modules.WorkItems.Domain;
using Aictiq.Modules.WorkItems.Endpoints;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Domain;
using Aictiq.SharedKernel.Paging;
using Microsoft.Extensions.DependencyInjection;

namespace Aictiq.IntegrationTests.WorkItems;

/// <summary>
/// Replies and who hears about them. The outbox is not running here: each test lifts the
/// event the API wrote and hands it to the Notifications handler, which is what the
/// Workers process would do, and then reads what was queued for the mail relay.
/// </summary>
[Trait("Category", "WorkItems")]
public sealed class CommentThreadTests(PostgresFixture postgres, GarageFixture garage) : IAsyncLifetime
{
    private const string Slug = "threads";
    private ApiTestContext _context = null!;
    private HttpClient _owner = null!;
    private string _ownerId = null!;
    private OrganizationView _organization = null!;
    private ProjectView _project = null!;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _context = await ApiTestContext.CreateAsync(postgres, garage, "comment_threads",
            configure: settings => settings["Email:BaseUrl"] = "https://aictiq.test",
            // The Workers half of Notifications, minus its hosted services: nothing here is
            // sent, only queued, and the tests read the queue.
            configureServices: services =>
            {
                services.AddOptions<NotificationEmailOptions>();
                services.AddSingleton<EmailTemplateRenderer>();
                services.AddScoped<NotificationEmailService>();
            });
        var owner = await _context.RegisterAsync($"threads-owner-{Guid.NewGuid():N}@test.local", "Olga", "Owner");
        _owner = _context.ClientFor(owner);
        _ownerId = owner.User.Id;
        var organization = await _owner.PostAsJsonAsync("/api/v1/orgs", new CreateOrganizationRequest("Thread tests", Slug, null, null), ApiTestContext.Json, Ct);
        organization.EnsureSuccessStatusCode();
        _organization = (await organization.Content.ReadFromJsonAsync<OrganizationView>(ApiTestContext.Json, Ct))!;
        var project = await _owner.PostAsJsonAsync($"/api/v1/orgs/{Slug}/projects", new CreateProjectRequest("Threads", "THR", null, ProjectVisibility.Organization, null, null), ApiTestContext.Json, Ct);
        project.EnsureSuccessStatusCode();
        _project = (await project.Content.ReadFromJsonAsync<ProjectView>(ApiTestContext.Json, Ct))!;
    }

    public async ValueTask DisposeAsync()
    {
        _owner.Dispose();
        await _context.DisposeAsync();
    }

    [Fact]
    public async Task a_reply_to_a_reply_joins_the_thread_under_its_first_comment()
    {
        var (_, ana) = await JoinAsync("Ana", "Kovač");
        var item = await CreateItemAsync("Threaded discussion");
        var other = await CreateItemAsync("Somewhere else");

        var root = await CommentAsync(ana, item.Key, "Which database?");
        var reply = await CommentAsync(_owner, item.Key, "Postgres.", root.Id);
        var nested = await CommentAsync(ana, item.Key, "Thanks.", reply.Id);
        Assert.Null(root.ParentCommentId);
        Assert.Equal(root.Id, reply.ParentCommentId);
        Assert.Equal(root.Id, nested.ParentCommentId);

        var elsewhere = await _owner.PostAsJsonAsync(ItemPath(other.Key, "comments"), new CreateCommentRequest("Wrong item", root.Id), ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, elsewhere.StatusCode);

        var deleted = await ana.DeleteAsync(ItemPath(item.Key, $"comments/{root.Id}"), Ct);
        deleted.EnsureSuccessStatusCode();
        var late = await _owner.PostAsJsonAsync(ItemPath(item.Key, "comments"), new CreateCommentRequest("Too late", reply.Id), ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);

        var page = await _owner.GetFromJsonAsync<PagedResult<CommentView>>(ItemPath(item.Key, "comments"), ApiTestContext.Json, Ct);
        Assert.Equal([null, root.Id, root.Id], page!.Items.Select(x => x.ParentCommentId));
    }

    [Fact]
    public async Task only_the_thread_author_and_the_people_tagged_are_emailed()
    {
        var (anaId, ana) = await JoinAsync("Ana", "Kovač");
        var (ivoId, ivo) = await JoinAsync("Ivo", "Horvat");
        var (miaId, _) = await JoinAsync("Mia", "Šarić");
        // The owner created the item, so the owner is watching it.
        var item = await CreateItemAsync("Release checklist");

        var root = await CommentAsync(ana, item.Key, "Is the changelog done?");
        await HandleAddedAsync(root.Id);
        var rootNotifications = await NotificationsAsync(root.Id);
        Assert.Equal([(_ownerId, NotificationKind.Commented)], rootNotifications);
        Assert.Empty(await EmailsAsync());

        var reply = await CommentAsync(ivo, item.Key, "Almost - @MiaŠarić can you check the **wording**?", root.Id);
        Assert.Equal([miaId], reply.Mentions);
        await HandleAddedAsync(reply.Id);

        var kinds = (await NotificationsAsync(reply.Id)).ToDictionary(x => x.UserId, x => x.Kind);
        Assert.Equal(NotificationKind.Commented, kinds[_ownerId]);
        Assert.Equal(NotificationKind.Replied, kinds[anaId]);
        Assert.Equal(NotificationKind.Mentioned, kinds[miaId]);
        Assert.False(kinds.ContainsKey(ivoId));

        var emails = await EmailsAsync();
        Assert.Equal(2, emails.Count);
        var toAna = Assert.Single(emails, x => x.Template == "comment-reply");
        Assert.StartsWith("threads-ana-", toAna.To);
        Assert.Equal($"Ivo Horvat replied to your comment on {item.Key}", toAna.Subject);
        // The excerpt is the comment read as text, not its Markdown source.
        Assert.Contains("can you check the wording?", toAna.Text);
        Assert.DoesNotContain("**", toAna.Text);
        Assert.Contains($"https://aictiq.test/o/{Slug}/p/THR/items/{item.Key}#comment-{reply.Id}", toAna.Text);
        var toMia = Assert.Single(emails, x => x.Template == "mention");
        Assert.StartsWith("threads-mia-", toMia.To);
        Assert.Contains("Release checklist", toMia.Text);

        // Tagging someone in an edit reaches them, and only them.
        var edit = await ivo.PatchAsJsonAsync(ItemPath(item.Key, $"comments/{reply.Id}"),
            new UpdateCommentRequest("Almost - @MiaŠarić can you check the **wording**? @Olga too."), ApiTestContext.Json, Ct);
        edit.EnsureSuccessStatusCode();
        await HandleMentionsAddedAsync(reply.Id);
        emails = await EmailsAsync();
        Assert.Equal(3, emails.Count);
        Assert.Single(emails, x => x.To.StartsWith("threads-owner-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task an_agents_comment_is_not_news_to_a_stakeholder_watching_the_item()
    {
        // JoinAsync's members may not operate the factory: they are stakeholders.
        var (anaId, ana) = await JoinAsync("Ana", "Kovač");
        var item = await CreateItemAsync("Checkout times out");
        await CommentAsync(ana, item.Key, "Happens every Friday.");

        var created = await _owner.PostAsJsonAsync($"/api/v1/orgs/{Slug}/agents",
            new CreateAgentRequest("builder", [_project.Id]), ApiTestContext.Json, Ct);
        created.EnsureSuccessStatusCode();
        var agentId = (await created.Content.ReadFromJsonAsync<AgentView>(ApiTestContext.Json, Ct))!.UserId;
        var token = await _owner.PostAsJsonAsync($"/api/v1/orgs/{Slug}/agents/{agentId}/tokens",
            new CreateAgentTokenRequest("CI", [], null), ApiTestContext.Json, Ct);
        token.EnsureSuccessStatusCode();
        using var agent = _context.Factory.CreateClient();
        agent.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer",
            (await token.Content.ReadFromJsonAsync<AgentTokenIssued>(ApiTestContext.Json, Ct))!.Secret);

        var progress = await CommentAsync(agent, item.Key, "Found it: the Friday batch job holds the lock. @Ana");
        await HandleAddedAsync(progress.Id);
        Assert.Equal([_ownerId], (await NotificationsAsync(progress.Id)).Select(x => x.UserId));

        // Nor does a person's reply in the agent's thread reach them.
        var reply = await CommentAsync(_owner, item.Key, "Good catch.", progress.Id);
        await HandleAddedAsync(reply.Id);
        Assert.DoesNotContain(anaId, (await NotificationsAsync(reply.Id)).Select(x => x.UserId));
        Assert.Empty(await EmailsAsync());
    }

    private async Task HandleAddedAsync(Guid commentId)
    {
        var e = JsonSerializer.Deserialize<CommentAdded>(await OutboxPayloadAsync(nameof(CommentAdded), commentId))!;
        await using var scope = _context.Factory.Services.CreateAsyncScope();
        await ActivatorUtilities.CreateInstance<CommentNotificationHandler>(scope.ServiceProvider).HandleAsync(e, Ct);
    }

    private async Task HandleMentionsAddedAsync(Guid commentId)
    {
        var e = JsonSerializer.Deserialize<CommentMentionsAdded>(await OutboxPayloadAsync(nameof(CommentMentionsAdded), commentId))!;
        await using var scope = _context.Factory.Services.CreateAsyncScope();
        await ActivatorUtilities.CreateInstance<CommentMentionsNotificationHandler>(scope.ServiceProvider).HandleAsync(e, Ct);
    }

    private async Task<string> OutboxPayloadAsync(string eventName, Guid commentId)
    {
        await using var connection = new NpgsqlConnection(_context.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT type, payload::text FROM shared.outbox_messages WHERE payload::text LIKE @comment", connection);
        command.Parameters.AddWithValue("comment", $"%{commentId}%");
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            var type = reader.GetString(0);
            if (type == eventName || type.EndsWith($".{eventName}", StringComparison.Ordinal) || type.Contains($".{eventName},", StringComparison.Ordinal))
                return reader.GetString(1);
        }
        throw new InvalidOperationException($"No {eventName} was written for comment {commentId}.");
    }

    private async Task<List<(string UserId, NotificationKind Kind)>> NotificationsAsync(Guid commentId)
    {
        var e = JsonSerializer.Deserialize<CommentAdded>(await OutboxPayloadAsync(nameof(CommentAdded), commentId))!;
        await using var connection = new NpgsqlConnection(_context.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT user_id, kind FROM notify.notifications WHERE event_id = @event ORDER BY user_id", connection);
        command.Parameters.AddWithValue("event", e.EventId);
        var rows = new List<(string, NotificationKind)>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct)) rows.Add((reader.GetString(0), (NotificationKind)reader.GetInt16(1)));
        return rows;
    }

    private async Task<List<(string To, string Template, string Subject, string Text)>> EmailsAsync()
    {
        await using var connection = new NpgsqlConnection(_context.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT to_address, template, subject, body_text FROM notify.email_outbox ORDER BY created_at", connection);
        var rows = new List<(string, string, string, string)>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct)) rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        return rows;
    }

    private async Task<(string UserId, HttpClient Client)> JoinAsync(string first, string last)
    {
        var person = await _context.RegisterAsync($"threads-{first.ToLowerInvariant()}-{Guid.NewGuid():N}@test.local", first, last);
        await using var connection = new NpgsqlConnection(_context.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var member = new NpgsqlCommand("INSERT INTO tenancy.organization_members (organization_id, user_id, role, joined_at, can_operate_factory) VALUES (@org, @user, @role, now(), false)", connection);
        member.Parameters.AddWithValue("org", _organization.Id);
        member.Parameters.AddWithValue("user", person.User.Id);
        member.Parameters.AddWithValue("role", (int)OrgRole.Member);
        await member.ExecuteNonQueryAsync(Ct);
        return (person.User.Id, _context.ClientFor(person));
    }

    private async Task<WorkItemView> CreateItemAsync(string title)
    {
        var response = await _owner.PostAsJsonAsync($"/api/v1/orgs/{Slug}/projects/{_project.Key}/items/",
            new CreateWorkItemRequest(WorkItemType.Bug, title, null, null, null, null, null, null, null, null, null, null, null, null), ApiTestContext.Json, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<WorkItemView>(ApiTestContext.Json, Ct))!;
    }

    private async Task<CommentView> CommentAsync(HttpClient client, string itemKey, string body, Guid? parentId = null)
    {
        var response = await client.PostAsJsonAsync(ItemPath(itemKey, "comments"), new CreateCommentRequest(body, parentId), ApiTestContext.Json, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<CommentView>(ApiTestContext.Json, Ct))!;
    }

    private static string ItemPath(string key, string? suffix = null) =>
        $"/api/v1/orgs/{Slug}/items/{key}{(suffix is null ? "" : $"/{suffix}")}";
}

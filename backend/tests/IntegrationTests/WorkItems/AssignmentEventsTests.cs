using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.Identity.Endpoints;
using Aictiq.Modules.Tenancy.Domain;
using Aictiq.Modules.WorkItems.Contracts;
using Aictiq.Modules.WorkItems.Domain;
using Aictiq.Modules.WorkItems.Endpoints;
using Aictiq.SharedKernel.Authorization;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Npgsql;

namespace Aictiq.IntegrationTests.WorkItems;

[Trait("Category", "WorkItems")]
public sealed class AssignmentEventsTests(PostgresFixture postgres, GarageFixture garage) : WorkItemsTestBase(postgres, garage)
{
    protected override bool AppRole => true;

    [Fact]
    public async Task creating_an_assigned_item_emits_one_event_with_the_saved_item_snapshot()
    {
        var recipient = await AddColleagueAsync();
        var dueDate = new DateOnly(2030, 2, 15);
        var response = await Client.PostAsJsonAsync(ItemsPath(),
            new CreateWorkItemRequest(WorkItemType.Bug, "Assigned on creation", null, null, WorkItemPriority.High,
                recipient, null, null, null, null, null, null, dueDate, null), ApiTestContext.Json, CancellationToken);
        response.EnsureSuccessStatusCode();
        var item = (await response.Content.ReadFromJsonAsync<WorkItemView>(ApiTestContext.Json, CancellationToken))!;

        var assigned = Assert.Single(await EventsAsync());
        Assert.Equal(Organization.Id, assigned.OrganizationId);
        Assert.Equal(Project.Id, assigned.ProjectId);
        Assert.Equal(item.Id, assigned.ItemId);
        Assert.Equal(item.Key, assigned.Key);
        Assert.Equal(recipient, assigned.AssigneeId);
        Assert.Equal(UserId, assigned.ActorId);
        Assert.Equal(item.Title, assigned.ItemTitle);
        Assert.Equal(item.StateId, assigned.StateId);
        Assert.Equal(WorkItemPriority.High.ToString(), assigned.ItemPriority);
        Assert.Equal(dueDate, assigned.DueDate);

        await CreateAsync(WorkItemType.Bug, "Unassigned on creation");
        var self = await Client.PostAsJsonAsync(ItemsPath(),
            new CreateWorkItemRequest(WorkItemType.Bug, "Self assigned", null, null, null,
                UserId, null, null, null, null, null, null, null, null), ApiTestContext.Json, CancellationToken);
        self.EnsureSuccessStatusCode();
        Assert.Single(await EventsAsync());
    }

    [Fact]
    public async Task updates_emit_only_for_new_other_assignees_and_never_for_rejected_or_unrelated_changes()
    {
        var firstRecipient = await AddColleagueAsync();
        var secondRecipient = await AddColleagueAsync();
        var item = await CreateAsync(WorkItemType.Bug, "Assignment lifecycle");

        item = await UpdateAsync(item, firstRecipient);
        var first = Assert.Single(await EventsAsync());
        Assert.Equal(firstRecipient, first.AssigneeId);

        item = await UpdateAsync(item, firstRecipient);
        item = await UpdateAsync(item, firstRecipient, "Only the title changes");
        Assert.Single(await EventsAsync());

        var stale = item;
        item = await UpdateAsync(item, secondRecipient);
        var events = await EventsAsync();
        Assert.Equal(2, events.Count);
        var reassigned = Assert.Single(events, e => e.AssigneeId == secondRecipient);
        Assert.Equal(item.Id, reassigned.ItemId);
        Assert.Equal("Only the title changes", reassigned.ItemTitle);
        Assert.Single(events, e => e.AssigneeId == firstRecipient);

        var rejected = await Client.PatchAsJsonAsync(ItemPath(item.Key), Request(stale, firstRecipient), ApiTestContext.Json, CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        Assert.Equal(2, (await EventsAsync()).Count);

        item = await UpdateAsync(item, null);
        Assert.Null(item.AssigneeId);
        item = await UpdateAsync(item, UserId);
        Assert.Equal(UserId, item.AssigneeId);
        Assert.Equal(2, (await EventsAsync()).Count);
    }

    [Fact]
    public async Task bulk_assignment_emits_for_successes_only_when_another_item_has_a_stale_version()
    {
        var recipient = await AddColleagueAsync();
        var first = await CreateAsync(WorkItemType.Bug, "Bulk success");
        var stale = await CreateAsync(WorkItemType.Bug, "Bulk conflict");
        await UpdateAsync(stale, null, "Changed before the bulk request");
        var response = await Client.PostAsJsonAsync($"{ItemsPath()}bulk",
            new BulkUpdateItemsRequest([first.Key, stale.Key],
                new BulkItemSet(null, recipient, WorkItemPriority.High, null, null, null, null),
                new Dictionary<string, uint> { [first.Key] = first.Version, [stale.Key] = stale.Version }), ApiTestContext.Json, CancellationToken);
        response.EnsureSuccessStatusCode();
        var result = (await response.Content.ReadFromJsonAsync<BulkUpdateItemsResponse>(ApiTestContext.Json, CancellationToken))!;
        Assert.Equal((int)HttpStatusCode.OK, result.Results.Single(x => x.Key == first.Key).Status);
        Assert.Equal((int)HttpStatusCode.Conflict, result.Results.Single(x => x.Key == stale.Key).Status);

        var assigned = Assert.Single(await EventsAsync());
        Assert.Equal(first.Id, assigned.ItemId);
        Assert.Equal(recipient, assigned.AssigneeId);
        Assert.Equal(UserId, assigned.ActorId);
        Assert.Equal(WorkItemPriority.High.ToString(), assigned.ItemPriority);

        var saved = result.Results.Single(x => x.Key == first.Key).Item!;
        var unchanged = await Client.PostAsJsonAsync($"{ItemsPath()}bulk",
            new BulkUpdateItemsRequest([saved.Key], new BulkItemSet(null, recipient, null, null, null, null, null),
                new Dictionary<string, uint> { [saved.Key] = saved.Version }), ApiTestContext.Json, CancellationToken);
        unchanged.EnsureSuccessStatusCode();
        Assert.Single(await EventsAsync());
    }

    [Fact]
    public async Task mcp_bulk_assignment_deduplicates_keys_and_ignores_unchanged_and_unassignment()
    {
        var recipient = await AddColleagueAsync();
        var first = await CreateAsync(WorkItemType.Bug, "MCP first");
        var second = await CreateAsync(WorkItemType.Bug, "MCP second");
        var response = await Client.PostAsJsonAsync("/api/v1/me/tokens",
            new CreateAccessTokenRequest("Assignment test", [Scopes.Mcp, Scopes.Read, Scopes.Write], Organization.Id, null),
            ApiTestContext.Json, CancellationToken);
        response.EnsureSuccessStatusCode();
        var token = (await response.Content.ReadFromJsonAsync<AccessTokenCreated>(ApiTestContext.Json, CancellationToken))!;
        var http = Context.Factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Secret);
        await using var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(http.BaseAddress!, "/mcp")
        }, http, loggerFactory: null, ownsHttpClient: true);
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: CancellationToken);

        async Task AssignAsync(string? assignee)
        {
            var result = await client.CallToolAsync("bulk_update", new Dictionary<string, object?>
            {
                ["project"] = Project.Key, ["keys"] = new[] { first.Key, second.Key, first.Key }, ["assigneeId"] = assignee
            }, cancellationToken: CancellationToken);
            Assert.NotEqual(true, result.IsError);
            Assert.Equal("2", Assert.Single(result.Content.OfType<TextContentBlock>()).Text);
        }

        await AssignAsync(recipient);
        var events = await EventsAsync();
        Assert.Equal(2, events.Count);
        Assert.Equal(new[] { first.Id, second.Id }.Order(), events.Select(e => e.ItemId).Order());
        Assert.All(events, e => { Assert.Equal(recipient, e.AssigneeId); Assert.Equal(UserId, e.ActorId); });
        await AssignAsync(recipient);
        await AssignAsync(null);
        Assert.Equal(2, (await EventsAsync()).Count);
        var unassigned = await Client.GetFromJsonAsync<WorkItemView>(ItemPath(first.Key), ApiTestContext.Json, CancellationToken);
        Assert.Null(unassigned!.AssigneeId);
    }

    private string ItemsPath() => $"/api/v1/orgs/work-items/projects/{Project.Key}/items/";

    private static UpdateWorkItemRequest Request(WorkItemView item, string? assignee, string? title = null) =>
        new(null, title, null, null, item.Priority, assignee, item.TeamId, item.ParentId, item.Points,
            item.EstimateHours, item.RemainingHours, item.CompletedHours, item.DueDate, null, item.Version);

    private async Task<WorkItemView> UpdateAsync(WorkItemView item, string? assignee, string? title = null)
    {
        var response = await Client.PatchAsJsonAsync(ItemPath(item.Key), Request(item, assignee, title), ApiTestContext.Json, CancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<WorkItemView>(ApiTestContext.Json, CancellationToken))!;
    }

    private async Task<string> AddColleagueAsync()
    {
        var colleague = await Context.RegisterAsync($"assignment-{Guid.NewGuid():N}@test.local", "Assigned", "Colleague");
        await using var connection = new NpgsqlConnection(Context.ConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO tenancy.organization_members (organization_id, user_id, role, joined_at, can_operate_factory)
            VALUES (@org, @user, @role, now(), true)
            """, connection);
        command.Parameters.AddWithValue("org", Organization.Id);
        command.Parameters.AddWithValue("user", colleague.User.Id);
        command.Parameters.AddWithValue("role", (int)OrgRole.Member);
        await command.ExecuteNonQueryAsync(CancellationToken);
        return colleague.User.Id;
    }

    private async Task<List<WorkItemAssigned>> EventsAsync()
    {
        await using var connection = new NpgsqlConnection(Context.ConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand("SELECT payload::text FROM shared.outbox_messages WHERE type = @type ORDER BY occurred_at, id", connection);
        command.Parameters.AddWithValue("type", typeof(WorkItemAssigned).FullName!);
        await using var reader = await command.ExecuteReaderAsync(CancellationToken);
        var events = new List<WorkItemAssigned>();
        while (await reader.ReadAsync(CancellationToken))
            events.Add(JsonSerializer.Deserialize<WorkItemAssigned>(reader.GetString(0))!);
        return events;
    }
}

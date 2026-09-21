using System.Net;
using System.Net.Http.Json;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.Tenancy.Endpoints;
using Aictiq.Modules.WorkItems.Domain;
using Aictiq.Modules.WorkItems.Endpoints;
using Aictiq.SharedKernel.Paging;

namespace Aictiq.IntegrationTests.WorkItems;

/// <summary>
/// Search, filter and sort on every item surface — list, board, team backlog — served as the
/// RLS-bound app role. Under the superuser the tests used to run as, search was green while
/// production answered every query with nothing: its raw SQL never told Postgres the tenant.
/// </summary>
[Trait("Category", "Search")]
[Collection("postgres")]
public sealed class ItemQueryTests(PostgresFixture postgres, GarageFixture garage) : WorkItemsTestBase(postgres, garage)
{
    protected override bool AppRole => true;

    private string ItemsPath => $"/api/v1/orgs/work-items/projects/{Project.Key}/items/";

    [Fact]
    public async Task list_and_search_endpoints_find_items_under_row_level_security()
    {
        var timeout = await CreateAsync(WorkItemType.Bug, "Timeout when uploading a file");
        await CreateAsync(WorkItemType.Bug, "Broken layout");

        Assert.Equal([timeout.Key], (await ListAsync("q=timeout")).Items.Select(x => x.Key));
        // A word still being typed is a piece of a title, not yet a word full text knows.
        Assert.Equal([timeout.Key], (await ListAsync("q=tim")).Items.Select(x => x.Key));
        Assert.Empty((await ListAsync("q=100%25")).Items);

        var project = await Client.GetFromJsonAsync<SearchResponse>($"/api/v1/orgs/work-items/projects/{Project.Key}/search?q=timeout", ApiTestContext.Json, CancellationToken);
        Assert.Equal(timeout.Key, Assert.Single(project!.Items).Key);
        var organization = await Client.GetFromJsonAsync<SearchResponse>("/api/v1/orgs/work-items/search?q=timeout", ApiTestContext.Json, CancellationToken);
        Assert.Equal(timeout.Key, Assert.Single(organization!.Items).Key);
    }

    [Fact]
    public async Task a_number_or_key_finds_its_item_first_although_full_text_cannot_see_it()
    {
        var first = await CreateAsync(WorkItemType.Bug, "Alpha");
        await CreateAsync(WorkItemType.Bug, "Beta");
        var mention = await CreateAsync(WorkItemType.Bug, "Follow-up to 1");
        var number = first.Key[(first.Key.LastIndexOf('-') + 1)..];

        Assert.Equal([first.Key, mention.Key], (await ListAsync($"q={number}")).Items.Select(x => x.Key));
        Assert.Equal([first.Key, mention.Key], (await ListAsync($"q=%23{number}")).Items.Select(x => x.Key));
        Assert.Equal([first.Key], (await ListAsync($"q={first.Key.ToLowerInvariant()}")).Items.Select(x => x.Key));
        Assert.Equal(3, (await ListAsync($"q={Project.Key}-")).TotalCount);

        var palette = await Client.GetFromJsonAsync<SearchResponse>($"/api/v1/orgs/work-items/search?q={number}", ApiTestContext.Json, CancellationToken);
        Assert.Equal(first.Key, palette!.Items[0].Key);
    }

    [Fact]
    public async Task list_sort_orders_every_page_and_rejects_an_unknown_field()
    {
        var low = await CreateAsync(WorkItemType.Bug, "Low", priority: WorkItemPriority.Low);
        var urgent = await CreateAsync(WorkItemType.Bug, "Urgent", priority: WorkItemPriority.Urgent);
        var medium = await CreateAsync(WorkItemType.Bug, "Medium", priority: WorkItemPriority.Medium);
        var alsoUrgent = await CreateAsync(WorkItemType.Bug, "Also urgent", priority: WorkItemPriority.Urgent);

        // A bare field keeps its historical direction; ties fall back to the item number.
        Assert.Equal([urgent.Key, alsoUrgent.Key, medium.Key, low.Key], (await ListAsync("sort=priority")).Items.Select(x => x.Key));
        Assert.Equal([low.Key, medium.Key, urgent.Key, alsoUrgent.Key], (await ListAsync("sort=priority:asc")).Items.Select(x => x.Key));
        Assert.Equal(["Urgent", "Medium", "Low", "Also urgent"], (await ListAsync("sort=title:desc")).Items.Select(x => x.Title));

        // One row per page: without a unique tiebreaker equal priorities could repeat or vanish.
        var paged = new List<string>();
        for (var page = 1; page <= 4; page++) paged.AddRange((await ListAsync($"sort=priority&pageSize=1&page={page}")).Items.Select(x => x.Key));
        Assert.Equal([urgent.Key, alsoUrgent.Key, medium.Key, low.Key], paged);

        var workflow = Assert.Single(await WorkflowsAsync());
        var closed = workflow.States.Single(x => x.Category == WorkflowStateCategory.Completed);
        var moved = await Client.PostAsJsonAsync(ItemPath(low.Key, "transition"), new TransitionRequest(closed.Id, low.Version), ApiTestContext.Json, CancellationToken);
        moved.EnsureSuccessStatusCode();
        Assert.Equal(low.Key, (await ListAsync("sort=state:desc")).Items[0].Key);

        var unknown = await Client.GetAsync($"{ItemsPath}?sort=colour", CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
    }

    [Fact]
    public async Task board_applies_every_filter_term_and_the_search_box()
    {
        var team = await DefaultTeamAsync();
        var mine = await CreateAsync(WorkItemType.Bug, "Mine to fix", assigneeId: UserId, teamId: team.Id);
        var other = await CreateAsync(WorkItemType.Story, "Nobody's story", teamId: team.Id);

        Assert.Equal([mine.Key], await BoardKeysAsync(team.Id, "filter=assignee:@me"));
        Assert.Equal([other.Key], await BoardKeysAsync(team.Id, "filter=assignee:none"));
        Assert.Equal([other.Key], await BoardKeysAsync(team.Id, "q=story"));
        Assert.Empty(await BoardKeysAsync(team.Id, "filter=state:completed"));

        var bad = await Client.GetAsync($"/api/v1/orgs/work-items/teams/{team.Id}/board?filter=label:nope", CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task team_backlog_applies_filter_and_search_to_its_sections_and_counts()
    {
        var team = await DefaultTeamAsync();
        var mine = await CreateAsync(WorkItemType.Bug, "Crash on save", assigneeId: UserId, teamId: team.Id);
        await CreateAsync(WorkItemType.Bug, "Slow start", teamId: team.Id);

        var filtered = await BacklogAsync(team.Id, "filter=assignee:@me");
        Assert.Equal([mine.Key], filtered.Backlog.Select(x => x.Key));
        Assert.Equal(1, filtered.BacklogCount);
        var searched = await BacklogAsync(team.Id, "q=crash");
        Assert.Equal([mine.Key], searched.Backlog.Select(x => x.Key));

        var bad = await Client.GetAsync($"/api/v1/orgs/work-items/teams/{team.Id}/backlog?filter=blocked:maybe", CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    private async Task<PagedResult<WorkItemView>> ListAsync(string query)
    {
        var response = await Client.GetAsync($"{ItemsPath}?{query}", CancellationToken);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(CancellationToken));
        return (await response.Content.ReadFromJsonAsync<PagedResult<WorkItemView>>(ApiTestContext.Json, CancellationToken))!;
    }

    private async Task<List<string>> BoardKeysAsync(Guid teamId, string query)
    {
        var response = await Client.GetAsync($"/api/v1/orgs/work-items/teams/{teamId}/board?{query}", CancellationToken);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(CancellationToken));
        var board = (await response.Content.ReadFromJsonAsync<BoardView>(ApiTestContext.Json, CancellationToken))!;
        Assert.Equal(board.Columns.Sum(x => x.Cards.Count), board.Columns.Sum(x => x.Count));
        return board.Columns.SelectMany(x => x.Cards).Select(x => x.Key).ToList();
    }

    private async Task<TeamBacklogView> BacklogAsync(Guid teamId, string query)
    {
        var response = await Client.GetAsync($"/api/v1/orgs/work-items/teams/{teamId}/backlog?{query}", CancellationToken);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(CancellationToken));
        return (await response.Content.ReadFromJsonAsync<TeamBacklogView>(ApiTestContext.Json, CancellationToken))!;
    }

    private async Task<TeamView> DefaultTeamAsync() =>
        (await Client.GetFromJsonAsync<List<TeamView>>($"/api/v1/orgs/work-items/projects/{Project.Key}/teams", ApiTestContext.Json, CancellationToken))!.Single(x => x.IsDefault);

    private async Task<WorkItemView> CreateAsync(WorkItemType type, string title, WorkItemPriority? priority = null,
        string? assigneeId = null, Guid? teamId = null)
    {
        var response = await Client.PostAsJsonAsync(ItemsPath,
            new CreateWorkItemRequest(type, title, null, null, priority, assigneeId, teamId, null, null, null, null, null, null, null),
            ApiTestContext.Json, CancellationToken);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(CancellationToken));
        return (await response.Content.ReadFromJsonAsync<WorkItemView>(ApiTestContext.Json, CancellationToken))!;
    }
}

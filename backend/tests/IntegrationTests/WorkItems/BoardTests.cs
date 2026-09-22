using System.Net;
using System.Net.Http.Json;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.Tenancy.Endpoints;
using Aictiq.Modules.WorkItems.Domain;
using Aictiq.Modules.WorkItems.Endpoints;

namespace Aictiq.IntegrationTests.WorkItems;

[Trait("Category", "Planning")]
public sealed class BoardTests(PostgresFixture postgres, GarageFixture garage) : WorkItemsTestBase(postgres, garage)
{
    [Fact]
    public async Task board_columns_with_the_same_state_keep_cards_in_their_dropped_column()
    {
        var team = (await Client.GetFromJsonAsync<List<TeamView>>($"/api/v1/orgs/work-items/projects/{Project.Key}/teams/", ApiTestContext.Json, CancellationToken))![0];
        var item = await CreateAsync(WorkItemType.Bug, "Card with a distinct board placement");
        await AssignAsync(team.Id, item); item = await GetAsync(item.Key);
        var initialResponse = await Client.GetAsync($"/api/v1/orgs/work-items/teams/{team.Id}/board", CancellationToken); Assert.True(initialResponse.IsSuccessStatusCode, await initialResponse.Content.ReadAsStringAsync(CancellationToken)); var initial = await initialResponse.Content.ReadFromJsonAsync<BoardView>(ApiTestContext.Json, CancellationToken);
        var workflow = (await WorkflowsAsync()).Single(); var proposed = workflow.States.Single(x => x.Category == WorkflowStateCategory.Proposed); var active = workflow.States.First(x => x.Category == WorkflowStateCategory.Active);
        var config = new UpdateBoardRequest([new BoardColumnConfig("To do", [proposed.Id], null, Guid.Empty), new BoardColumnConfig("In review", [active.Id], null, Guid.Empty), new BoardColumnConfig("Testing", [active.Id], null, Guid.Empty)], null, null, null, initial!.Version);

        var saved = await Client.PutAsJsonAsync($"/api/v1/orgs/work-items/teams/{team.Id}/board", config, ApiTestContext.Json, CancellationToken);

        saved.EnsureSuccessStatusCode();
        var board = (await saved.Content.ReadFromJsonAsync<BoardView>(ApiTestContext.Json, CancellationToken))!;
        var inReview = board.Columns.Single(x => x.Name == "In review"); var testing = board.Columns.Single(x => x.Name == "Testing");
        Assert.Equal(active.Id, Assert.Single(inReview.StateIds)); Assert.Equal(active.Id, Assert.Single(testing.StateIds)); Assert.NotEqual(inReview.Id, testing.Id);
        var moved = await Client.PostAsJsonAsync(ItemPath(item.Key, "board-move"), new BoardMoveRequest(active.Id, null, item.Version, ToColumnId: testing.Id), ApiTestContext.Json, CancellationToken);
        moved.EnsureSuccessStatusCode();
        var rendered = await Client.GetFromJsonAsync<BoardView>($"/api/v1/orgs/work-items/teams/{team.Id}/board", ApiTestContext.Json, CancellationToken);
        Assert.Empty(rendered!.Columns.Single(x => x.Name == "In review").Cards);
        Assert.Equal(item.Key, Assert.Single(rendered.Columns.Single(x => x.Name == "Testing").Cards).Key);
    }

    [Fact]
    public async Task merged_columns_show_cards_and_wip_blocks_a_second_move()
    {
        var team = (await Client.GetFromJsonAsync<List<TeamView>>($"/api/v1/orgs/work-items/projects/{Project.Key}/teams/", ApiTestContext.Json, CancellationToken))![0];
        var first = await CreateAsync(WorkItemType.Bug, "First");
        var second = await CreateAsync(WorkItemType.Bug, "Second");
        await AssignAsync(team.Id, first, second);
        first = await GetAsync(first.Key); second = await GetAsync(second.Key);
        var initialResponse = await Client.GetAsync($"/api/v1/orgs/work-items/teams/{team.Id}/board", CancellationToken); Assert.True(initialResponse.IsSuccessStatusCode, await initialResponse.Content.ReadAsStringAsync(CancellationToken)); var initial = await initialResponse.Content.ReadFromJsonAsync<BoardView>(ApiTestContext.Json, CancellationToken);
        var active = (await WorkflowsAsync()).Single().States.Where(x => x.Category == WorkflowStateCategory.Active).ToList();
        var proposed = (await WorkflowsAsync()).Single().States.Single(x => x.Category == WorkflowStateCategory.Proposed);
        var config = new UpdateBoardRequest([new BoardColumnConfig("To do", [proposed.Id], null, Guid.Empty), new BoardColumnConfig("Doing", active.Select(x => x.Id).ToList(), 1, Guid.Empty)], "assignee", null, null, initial!.Version);
        var saved = await Client.PutAsJsonAsync($"/api/v1/orgs/work-items/teams/{team.Id}/board", config, ApiTestContext.Json, CancellationToken);
        saved.EnsureSuccessStatusCode();
        var board = (await saved.Content.ReadFromJsonAsync<BoardView>(ApiTestContext.Json, CancellationToken))!;
        var move = await Client.PostAsJsonAsync(ItemPath(first.Key, "board-move"), new BoardMoveRequest(active[0].Id, null, first.Version), ApiTestContext.Json, CancellationToken);
        move.EnsureSuccessStatusCode();
        var moved = (await move.Content.ReadFromJsonAsync<WorkItemView>(ApiTestContext.Json, CancellationToken))!;
        var blocked = await Client.PostAsJsonAsync(ItemPath(second.Key, "board-move"), new BoardMoveRequest(active[0].Id, moved.Key, second.Version), ApiTestContext.Json, CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        var rendered = await Client.GetFromJsonAsync<BoardView>($"/api/v1/orgs/work-items/teams/{team.Id}/board", ApiTestContext.Json, CancellationToken);
        Assert.True(rendered!.Columns.Single(x => x.Name == "Doing").WipExceeded == false);
        Assert.Equal("assignee", rendered.Swimlane);
    }

    [Fact]
    public async Task board_columns_page_cards_in_rank_order_but_count_the_whole_column()
    {
        var team = (await Client.GetFromJsonAsync<List<TeamView>>($"/api/v1/orgs/work-items/projects/{Project.Key}/teams/", ApiTestContext.Json, CancellationToken))![0];
        var first = await CreateAsync(WorkItemType.Bug, "First"); var second = await CreateAsync(WorkItemType.Bug, "Second"); var third = await CreateAsync(WorkItemType.Bug, "Third");
        await AssignAsync(team.Id, first, second, third);
        var mine = await GetAsync(third.Key);
        var assigned = await Client.PostAsJsonAsync($"/api/v1/orgs/work-items/projects/{Project.Key}/items/bulk", new BulkUpdateItemsRequest([mine.Key], new BulkItemSet(null, UserId, null, null, null, null, null), new Dictionary<string, uint> { [mine.Key] = mine.Version }), ApiTestContext.Json, CancellationToken); assigned.EnsureSuccessStatusCode();
        var boardPath = $"/api/v1/orgs/work-items/teams/{team.Id}/board";

        var paged = (await Client.GetFromJsonAsync<BoardView>($"{boardPath}?take=2", ApiTestContext.Json, CancellationToken))!;
        var column = paged.Columns.Single(x => x.Count > 0);
        Assert.Equal(3, column.Count);
        Assert.Equal([first.Key, second.Key], column.Cards.Select(x => x.Key));

        var expanded = (await Client.GetFromJsonAsync<BoardView>($"{boardPath}?take=2&expand={column.Id}:3", ApiTestContext.Json, CancellationToken))!;
        Assert.Equal([first.Key, second.Key, third.Key], expanded.Columns.Single(x => x.Id == column.Id).Cards.Select(x => x.Key));

        var configOnly = (await Client.GetFromJsonAsync<BoardView>($"{boardPath}?take=0", ApiTestContext.Json, CancellationToken))!;
        Assert.Empty(configOnly.Columns.SelectMany(x => x.Cards)); Assert.Equal(3, configOnly.Columns.Single(x => x.Id == column.Id).Count);

        var filtered = (await Client.GetFromJsonAsync<BoardView>($"{boardPath}?assigneeIds={UserId}", ApiTestContext.Json, CancellationToken))!;
        var filteredColumn = filtered.Columns.Single(x => x.Id == column.Id);
        Assert.Equal(1, filteredColumn.Count); Assert.Equal(third.Key, Assert.Single(filteredColumn.Cards).Key);

        Assert.Equal(HttpStatusCode.BadRequest, (await Client.GetAsync($"{boardPath}?take=501", CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client.GetAsync($"{boardPath}?expand=not-a-column", CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task taskboard_aggregates_task_hours_under_its_story_and_keeps_unparented_tasks()
    {
        var team = (await Client.GetFromJsonAsync<List<TeamView>>($"/api/v1/orgs/work-items/projects/{Project.Key}/teams/", ApiTestContext.Json, CancellationToken))![0];
        var sprint = await CreateSprintAsync(team.Id);
        var epic = await CreateAsync(WorkItemType.Epic, "Epic"); var feature = await CreateAsync(WorkItemType.Feature, "Feature", epic.Id);
        var story = await CreateAsync(WorkItemType.Story, "Story", feature.Id); var task = await CreateAsync(WorkItemType.Task, "Task", story.Id, estimateHours: 3);
        var offSprintStory = await CreateAsync(WorkItemType.Story, "Off sprint parent", feature.Id); var loose = await CreateAsync(WorkItemType.Task, "Loose", offSprintStory.Id, estimateHours: 2);
        await AssignAsync(team.Id, story, task, loose);
        var scoped = await Client.PostAsJsonAsync($"/api/v1/orgs/work-items/projects/{Project.Key}/items/bulk", new BulkUpdateItemsRequest([story.Key, task.Key, loose.Key], new BulkItemSet(null, null, null, null, sprint.Id, null, null), new Dictionary<string, uint> { [story.Key] = (await GetAsync(story.Key)).Version, [task.Key] = (await GetAsync(task.Key)).Version, [loose.Key] = (await GetAsync(loose.Key)).Version }), ApiTestContext.Json, CancellationToken); scoped.EnsureSuccessStatusCode();
        var board = await Client.GetFromJsonAsync<TaskboardView>($"/api/v1/orgs/work-items/sprints/{sprint.Id}/taskboard", ApiTestContext.Json, CancellationToken);
        Assert.Equal(3, Assert.Single(board!.Rows).RemainingHours);
        Assert.Equal(2, board.UnparentedTasks.Sum(x => x.RemainingHours));
    }

    private async Task AssignAsync(Guid teamId, params WorkItemView[] items)
    {
        var versions = items.ToDictionary(x => x.Key, x => x.Version);
        var response = await Client.PostAsJsonAsync($"/api/v1/orgs/work-items/projects/{Project.Key}/items/bulk", new BulkUpdateItemsRequest(items.Select(x => x.Key).ToList(), new BulkItemSet(null, null, null, teamId, null, null, null), versions), ApiTestContext.Json, CancellationToken); response.EnsureSuccessStatusCode();
    }
    private async Task<SprintView> CreateSprintAsync(Guid teamId) { var response = await Client.PostAsJsonAsync($"/api/v1/orgs/work-items/teams/{teamId}/sprints", new CreateSprintRequest("Sprint", null, new DateOnly(2030, 1, 1), new DateOnly(2030, 1, 8)), ApiTestContext.Json, CancellationToken); response.EnsureSuccessStatusCode(); return (await response.Content.ReadFromJsonAsync<SprintView>(ApiTestContext.Json, CancellationToken))!; }
    private async Task<WorkItemView> GetAsync(string key) => (await Client.GetFromJsonAsync<WorkItemView>(ItemPath(key), ApiTestContext.Json, CancellationToken))!;
}

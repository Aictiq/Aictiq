using System.Net;
using System.Net.Http.Json;
using Aictiq.Modules.Tenancy.Endpoints;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.WorkItems.Domain;
using Aictiq.Modules.WorkItems.Endpoints;
using Aictiq.SharedKernel.Paging;

namespace Aictiq.IntegrationTests.WorkItems;

[Trait("Category", "Planning")]
[Collection("postgres")]
public sealed class BacklogRankingTests(PostgresFixture postgres, GarageFixture garage) : WorkItemsTestBase(postgres, garage)
{
    [Fact]
    public async Task move_persists_an_order_that_every_client_reads_the_same_way()
    {
        var first = await CreateAsync(WorkItemType.Bug, "First");
        var middle = await CreateAsync(WorkItemType.Bug, "Middle");
        var last = await CreateAsync(WorkItemType.Bug, "Last");

        var response = await Client.PostAsJsonAsync(ItemPath(last.Key, "move"),
            new MoveItemRequest(null, first.Key, null, null, null, last.Version), ApiTestContext.Json, CancellationToken);
        response.EnsureSuccessStatusCode();

        var list = await Client.GetFromJsonAsync<PagedResult<WorkItemView>>($"/api/v1/orgs/work-items/projects/{Project.Key}/items/", ApiTestContext.Json, CancellationToken);
        Assert.Equal([last.Key, first.Key, middle.Key], list!.Items.Take(3).Select(x => x.Key));
        var tree = await Client.GetFromJsonAsync<List<BacklogNode>>($"/api/v1/orgs/work-items/projects/{Project.Key}/backlog?level=epic", ApiTestContext.Json, CancellationToken);
        Assert.Equal(last.Key, tree![0].Item.Key);
    }

    [Fact]
    public async Task team_backlog_lists_an_unplanned_item_once_when_the_team_has_no_active_or_next_sprint()
    {
        var team = (await Client.GetFromJsonAsync<List<TeamView>>($"/api/v1/orgs/work-items/projects/{Project.Key}/teams", ApiTestContext.Json, CancellationToken))!.Single(x => x.IsDefault);
        var created = await Client.PostAsJsonAsync($"/api/v1/orgs/work-items/projects/{Project.Key}/items/",
            new CreateWorkItemRequest(WorkItemType.Epic, "Unplanned epic", null, null, null, null, team.Id, null, null, null, null, null, null, null),
            ApiTestContext.Json, CancellationToken);
        created.EnsureSuccessStatusCode();

        var backlog = await Client.GetFromJsonAsync<TeamBacklogView>($"/api/v1/orgs/work-items/teams/{team.Id}/backlog", ApiTestContext.Json, CancellationToken);

        Assert.Empty(backlog!.CurrentSprint);
        Assert.Empty(backlog.NextSprint);
        Assert.Single(backlog.Backlog);
    }
    [Fact]
    public async Task team_backlog_pages_a_section_as_a_tree_prefix_but_counts_the_whole_section()
    {
        var team = (await Client.GetFromJsonAsync<List<TeamView>>($"/api/v1/orgs/work-items/projects/{Project.Key}/teams", ApiTestContext.Json, CancellationToken))!.Single(x => x.IsDefault);
        var epic = await CreateInTeamAsync(WorkItemType.Epic, "Epic", team.Id, null);
        var story = await CreateInTeamAsync(WorkItemType.Story, "Standalone story", team.Id, null);
        var child = await CreateInTeamAsync(WorkItemType.Story, "Story under the epic", team.Id, epic.Id);
        var path = $"/api/v1/orgs/work-items/teams/{team.Id}/backlog";

        // The child ranks after the standalone story, but it is drawn under its epic, so the
        // first page of two is the epic and its child - never a child without its parent.
        var paged = (await Client.GetFromJsonAsync<TeamBacklogView>($"{path}?take=2", ApiTestContext.Json, CancellationToken))!;
        Assert.Equal(3, paged.BacklogCount);
        Assert.Equal([epic.Key, child.Key], paged.Backlog.Select(x => x.Key));

        var expanded = (await Client.GetFromJsonAsync<TeamBacklogView>($"{path}?take=2&expand=backlog:3", ApiTestContext.Json, CancellationToken))!;
        Assert.Equal([epic.Key, story.Key, child.Key], expanded.Backlog.Select(x => x.Key));

        var countsOnly = (await Client.GetFromJsonAsync<TeamBacklogView>($"{path}?take=0", ApiTestContext.Json, CancellationToken))!;
        Assert.Empty(countsOnly.Backlog); Assert.Equal(3, countsOnly.BacklogCount); Assert.Equal(0, countsOnly.CurrentSprintCount);

        Assert.Equal(HttpStatusCode.BadRequest, (await Client.GetAsync($"{path}?take=2001", CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client.GetAsync($"{path}?expand=sideways:5", CancellationToken)).StatusCode);
    }

    private async Task<WorkItemView> CreateInTeamAsync(WorkItemType type, string title, Guid teamId, Guid? parentId)
    {
        var response = await Client.PostAsJsonAsync($"/api/v1/orgs/work-items/projects/{Project.Key}/items/",
            new CreateWorkItemRequest(type, title, null, null, null, null, teamId, parentId, null, null, null, null, null, null), ApiTestContext.Json, CancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<WorkItemView>(ApiTestContext.Json, CancellationToken))!;
    }
}

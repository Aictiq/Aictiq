using System.Net;
using System.Net.Http.Json;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.Tenancy.Domain;
using Aictiq.Modules.Tenancy.Endpoints;
using Aictiq.Modules.WorkItems.Domain;
using Aictiq.Modules.WorkItems.Endpoints;

namespace Aictiq.IntegrationTests.WorkItems;

[Trait("Category", "Search")]
public sealed class ItemSuggestionTests(PostgresFixture postgres, GarageFixture garage) : WorkItemsTestBase(postgres, garage)
{
    protected override bool AppRole => true;

    [Fact]
    public async Task empty_item_query_returns_recent_items_with_a_limit_and_project_isolation()
    {
        await CreateAsync(WorkItemType.Bug, "Earlier item");
        var recent = await CreateAsync(WorkItemType.Bug, "Latest local item");
        var separate = await CreateSeparateProjectAsync();
        var foreign = await Client.PostAsJsonAsync($"/api/v1/orgs/work-items/projects/{separate.Key}/items/",
            new CreateWorkItemRequest(WorkItemType.Bug, "Latest foreign item", null, null, null, null, null, null, null, null, null, null, null, null),
            ApiTestContext.Json, CancellationToken);
        foreign.EnsureSuccessStatusCode();

        var response = await SearchAsync("", 1);

        Assert.Equal(recent.Key, Assert.Single(response.Items).Key);
        Assert.Empty(response.Comments);
        Assert.Empty(response.Pages);
    }

    [Fact]
    public async Task key_and_number_prefixes_include_exact_match_first_and_remaining_matches()
    {
        var items = new List<WorkItemView>();
        for (var i = 0; i < 10; i++) items.Add(await CreateAsync(WorkItemType.Bug, "Ordinary work"));

        foreach (var query in new[] { "1", "#1", $"{Project.Key.ToLowerInvariant()}-1" })
        {
            var response = await SearchAsync(query);
            Assert.Equal([items[0].Key, items[9].Key], response.Items.Select(x => x.Key));
        }

        var prefix = await SearchAsync($"{Project.Key.ToLowerInvariant()}-");
        Assert.Equal(10, prefix.Items.Count);
        Assert.Empty((await SearchAsync("OTHER-1")).Items);
    }

    [Fact]
    public async Task title_prefix_still_finds_an_item_without_a_completed_word()
    {
        var item = await CreateAsync(WorkItemType.Bug, "Timeout when uploading");
        var descriptionMatch = await Client.PostAsJsonAsync($"/api/v1/orgs/work-items/projects/{Project.Key}/items/",
            new CreateWorkItemRequest(WorkItemType.Bug, "Another item", "Track time spent.", null, null, null, null, null, null, null, null, null, null, null),
            ApiTestContext.Json, CancellationToken);
        descriptionMatch.EnsureSuccessStatusCode();

        Assert.Equal(item.Key, (await SearchAsync("time")).Items[0].Key);
        Assert.Empty((await SearchAsync("100%")).Items);
    }

    [Fact]
    public async Task empty_queries_still_require_items_only_and_project_scope()
    {
        foreach (var path in new[]
        {
            $"/api/v1/orgs/work-items/projects/{Project.Key}/search?q=",
            $"/api/v1/orgs/work-items/projects/{Project.Key}/search?q=&types=comments",
            "/api/v1/orgs/work-items/search?q=&types=items"
        })
        {
            var response = await Client.GetAsync(path, CancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    private async Task<SearchResponse> SearchAsync(string query, int limit = 20) =>
        (await Client.GetFromJsonAsync<SearchResponse>($"/api/v1/orgs/work-items/projects/{Project.Key}/search?q={Uri.EscapeDataString(query)}&types=items&limit={limit}", ApiTestContext.Json, CancellationToken))!;

    private async Task<ProjectView> CreateSeparateProjectAsync()
    {
        var response = await Client.PostAsJsonAsync("/api/v1/orgs/work-items/projects",
            new CreateProjectRequest("Separate project", "SEP", null, ProjectVisibility.Organization, null, null),
            ApiTestContext.Json, CancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProjectView>(ApiTestContext.Json, CancellationToken))!;
    }
}

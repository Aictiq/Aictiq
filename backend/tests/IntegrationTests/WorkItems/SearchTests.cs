using System.Net.Http.Json;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.Tenancy.Domain;
using Aictiq.Modules.Tenancy.Endpoints;
using Aictiq.Modules.WorkItems.Domain;
using Aictiq.Modules.WorkItems.Endpoints;
using Aictiq.SharedKernel.Paging;

namespace Aictiq.IntegrationTests.WorkItems;

[Trait("Category", "Search")]
public sealed class SearchTests(PostgresFixture postgres, GarageFixture garage) : WorkItemsTestBase(postgres, garage)
{
    [Fact]
    public async Task title_matches_rank_above_description_matches_and_list_q_uses_the_same_document()
    {
        var title = await CreateWithDescriptionAsync("Login flow", "A routine change.");
        var description = await CreateWithDescriptionAsync("Authentication cleanup", "Document the login flow here.");

        var response = await SearchAsync(Project.Key, "login");

        Assert.Equal([title.Key, description.Key], response.Items.Take(2).Select(item => item.Key));
        var listResponse = await Client.GetAsync($"/api/v1/orgs/work-items/projects/{Project.Key}/items/?q=login", CancellationToken);
        Assert.True(listResponse.IsSuccessStatusCode, await listResponse.Content.ReadAsStringAsync(CancellationToken));
        var list = await listResponse.Content.ReadFromJsonAsync<PagedResult<WorkItemView>>(ApiTestContext.Json, CancellationToken);
        Assert.Equal(2, list!.TotalCount);
    }

    [Fact]
    public async Task project_search_does_not_leak_results_from_another_project()
    {
        var local = await CreateWithDescriptionAsync("Login in the current project", "");
        var projectResponse = await Client.PostAsJsonAsync("/api/v1/orgs/work-items/projects",
            new CreateProjectRequest("Separate project", "SEP", null, ProjectVisibility.Organization, null, null),
            ApiTestContext.Json, CancellationToken);
        projectResponse.EnsureSuccessStatusCode();
        var separate = (await projectResponse.Content.ReadFromJsonAsync<ProjectView>(ApiTestContext.Json, CancellationToken))!;
        await CreateWithDescriptionAsync("Login in another project", "", separate.Key);

        var response = await SearchAsync(Project.Key, "login");

        Assert.Equal(local.Key, Assert.Single(response.Items).Key);
    }

    [Fact]
    public async Task comment_headlines_escape_markdown_that_looks_like_html()
    {
        var item = await CreateWithDescriptionAsync("A safe title", "");
        var comment = await Client.PostAsJsonAsync(ItemPath(item.Key, "comments"),
            new CreateCommentRequest("<b>login</b> details"), ApiTestContext.Json, CancellationToken);
        comment.EnsureSuccessStatusCode();

        var response = await SearchAsync(Project.Key, "login", "comments");

        var match = Assert.Single(response.Comments);
        Assert.Contains("&lt;/b&gt;", match.Snippet, StringComparison.Ordinal);
        Assert.Contains("<mark>login</mark>", match.Snippet, StringComparison.Ordinal);
    }

    private async Task<SearchResponse> SearchAsync(string projectKey, string query, string? types = null) =>
        (await Client.GetFromJsonAsync<SearchResponse>($"/api/v1/orgs/work-items/projects/{projectKey}/search?q={Uri.EscapeDataString(query)}{(types is null ? "" : $"&types={types}")}", ApiTestContext.Json, CancellationToken))!;

    private async Task<WorkItemView> CreateWithDescriptionAsync(string title, string description, string? projectKey = null)
    {
        var key = projectKey ?? Project.Key;
        var response = await Client.PostAsJsonAsync($"/api/v1/orgs/work-items/projects/{key}/items/",
            new CreateWorkItemRequest(WorkItemType.Bug, title, description, null, null, null, null, null, null, null, null, null, null, null),
            ApiTestContext.Json, CancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<WorkItemView>(ApiTestContext.Json, CancellationToken))!;
    }
}

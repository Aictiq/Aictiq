using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.Identity.Endpoints;
using Aictiq.Modules.Tenancy.Domain;
using Aictiq.Modules.Tenancy.Endpoints;
using Aictiq.Modules.WorkItems.Domain;
using Aictiq.Modules.WorkItems.Endpoints;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Paging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Aictiq.IntegrationTests.Perf;

/// <summary>
/// The N+1 audit: every hot read path must issue a fixed, small number of SQL queries
/// however much data the request returns. Counting rides a DbCommandInterceptor on the
/// module contexts, so these bounds lock in the batched per-page helpers in
/// WorkItemEndpoints - a per-item query added anywhere in ViewsAsync or StatesAsync fails
/// the class.
/// </summary>
[Trait("Category", "WorkItems")]
public sealed class QueryCountTests(PostgresFixture postgres, GarageFixture garage) : IAsyncLifetime
{
    private const string OrgSlug = "query-count";
    private const int SeededItems = 40;

    private ApiTestContext _context = null!;
    private HttpClient _client = null!;
    private ProjectView _project = null!;
    private string _itemsPath = null!;
    private readonly List<WorkItemView> _items = [];
    private Guid _organizationId;

    public async ValueTask InitializeAsync()
    {
        _context = await ApiTestContext.CreateAsync(postgres, garage, "query_count", countQueries: true);
        var auth = await _context.RegisterAsync($"query-count-{Guid.NewGuid():N}@test.local");
        _client = _context.ClientFor(auth);

        var organization = await _client.PostAsJsonAsync("/api/v1/orgs",
            new CreateOrganizationRequest("Query count tests", OrgSlug, null, null), ApiTestContext.Json, CancellationToken);
        organization.EnsureSuccessStatusCode();
        _organizationId = (await organization.Content.ReadFromJsonAsync<OrganizationView>(ApiTestContext.Json, CancellationToken))!.Id;

        var project = await _client.PostAsJsonAsync($"/api/v1/orgs/{OrgSlug}/projects",
            new CreateProjectRequest("Query count project", "QCT", null, ProjectVisibility.Organization, null, null),
            ApiTestContext.Json, CancellationToken);
        project.EnsureSuccessStatusCode();
        _project = (await project.Content.ReadFromJsonAsync<ProjectView>(ApiTestContext.Json, CancellationToken))!;
        _itemsPath = $"/api/v1/orgs/{OrgSlug}/projects/{_project.Key}/items/";

        for (var i = 1; i <= SeededItems; i++)
            _items.Add(await CreateItemAsync(i == SeededItems ? "Zephyr rollout plan" : $"Ready work {i}"));

        var labels = new List<Guid>();
        foreach (var name in new[] { "frontend", "backend", "urgent" })
        {
            var created = await _client.PostAsJsonAsync($"/api/v1/orgs/{OrgSlug}/projects/{_project.Key}/labels/",
                new CreateLabelRequest(name, "#3B82F6", null, null), ApiTestContext.Json, CancellationToken);
            created.EnsureSuccessStatusCode();
            labels.Add((await created.Content.ReadFromJsonAsync<LabelView>(ApiTestContext.Json, CancellationToken))!.Id);
        }
        for (var i = 0; i < _items.Count; i++)
        {
            var added = await _client.PutAsync($"/api/v1/orgs/{OrgSlug}/items/{_items[i].Key}/labels/{labels[i % labels.Count]}", null, CancellationToken);
            Assert.True(added.IsSuccessStatusCode, await added.Content.ReadAsStringAsync(CancellationToken));
        }
        foreach (var item in _items.Where((_, index) => index % 4 == 0))
        {
            var commented = await _client.PostAsJsonAsync($"/api/v1/orgs/{OrgSlug}/items/{item.Key}/comments/",
                new CreateCommentRequest("Noise for the thread."), ApiTestContext.Json, CancellationToken);
            commented.EnsureSuccessStatusCode();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _context.DisposeAsync();
    }

    [Fact]
    public async Task listing_a_page_costs_the_same_queries_whatever_the_page_size()
    {
        await _client.GetFromJsonAsync<PagedResult<WorkItemView>>($"{_itemsPath}?pageSize=10", ApiTestContext.Json, CancellationToken);
        var queries = _context.StartQueryCount();

        await _client.GetFromJsonAsync<PagedResult<WorkItemView>>($"{_itemsPath}?pageSize=10", ApiTestContext.Json, CancellationToken);
        var smallPage = queries.Count;

        queries.Reset();
        await _client.GetFromJsonAsync<PagedResult<WorkItemView>>($"{_itemsPath}?pageSize=100", ApiTestContext.Json, CancellationToken);

        queries.AssertSameAs(smallPage, "whether the page holds 10 or 100 items");
        queries.AssertAtMost(12, "the list's rollups, labels, blockers and watchers must stay batched per page");
    }

    [Fact]
    public async Task an_item_detail_stays_a_fixed_handful_of_queries_whatever_its_thread()
    {
        var item = _items[0];
        await _client.GetFromJsonAsync<WorkItemView>(ItemPath(item.Key), ApiTestContext.Json, CancellationToken);
        var queries = _context.StartQueryCount();

        await _client.GetFromJsonAsync<WorkItemView>(ItemPath(item.Key), ApiTestContext.Json, CancellationToken);

        queries.AssertAtMost(10, "labels, watchers and the child rollup must arrive in batched queries, not per relation");
    }

    [Fact]
    public async Task the_board_costs_the_same_queries_whatever_the_number_of_cards()
    {
        var teamId = await DefaultTeamIdAsync();
        await BoardAsync(teamId);

        var queries = _context.StartQueryCount();
        await BoardAsync(teamId);
        var smallBoard = queries.Count;

        for (var i = 1; i <= 10; i++) await CreateItemAsync($"Board filler {i}");

        queries.Reset();
        await BoardAsync(teamId);

        queries.AssertSameAs(smallBoard, $"whether the board holds {SeededItems} or {SeededItems + 10} cards");
        queries.AssertAtMost(12, "board cards must come from the batched per-page helpers");
    }

    [Fact]
    public async Task project_search_matches_through_raw_sql_so_its_ef_side_stays_a_few_queries()
    {
        var searchPath = $"/api/v1/orgs/{OrgSlug}/projects/{_project.Key}/search?q=zephyr&types=items";
        await _client.GetFromJsonAsync<SearchResponse>(searchPath, ApiTestContext.Json, CancellationToken);
        var queries = _context.StartQueryCount();

        var response = await _client.GetFromJsonAsync<SearchResponse>(searchPath, ApiTestContext.Json, CancellationToken);

        queries.AssertAtMost(4, "the full-text match itself is one raw NpgsqlCommand the EF counter cannot see - only the request's authorization path counts here");
        Assert.Contains(response!.Items, result => result.Title.Contains("Zephyr", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task the_agent_ready_list_polls_with_a_few_queries()
    {
        var token = await CreateTokenAsync();
        await using var mcp = await ConnectAsync(token);
        var arguments = new Dictionary<string, object?> { ["project"] = _project.Key };

        var first = await mcp.CallToolAsync("list_ready_work", arguments, cancellationToken: CancellationToken);
        var queries = _context.StartQueryCount();
        var second = await mcp.CallToolAsync("list_ready_work", arguments, cancellationToken: CancellationToken);

        Assert.NotEqual(true, first.IsError);
        Assert.NotEqual(true, second.IsError);
        Assert.NotEmpty(Json(second).EnumerateArray());
        queries.AssertAtMost(8, "an agent polling list_ready_work in a loop must stay a handful of queries");
    }

    private CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    private async Task<WorkItemView> CreateItemAsync(string title)
    {
        var response = await _client.PostAsJsonAsync(_itemsPath,
            new CreateWorkItemRequest(WorkItemType.Bug, title, null, null, null, null, null, null, null, null, null, null, null, null),
            ApiTestContext.Json, CancellationToken);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(CancellationToken));
        return (await response.Content.ReadFromJsonAsync<WorkItemView>(ApiTestContext.Json, CancellationToken))!;
    }

    private async Task<Guid> DefaultTeamIdAsync() =>
        (await _client.GetFromJsonAsync<List<TeamView>>(
            $"/api/v1/orgs/{OrgSlug}/projects/{_project.Key}/teams/", ApiTestContext.Json, CancellationToken))![0].Id;

    private async Task<BoardView> BoardAsync(Guid teamId) =>
        (await _client.GetFromJsonAsync<BoardView>($"/api/v1/orgs/{OrgSlug}/teams/{teamId}/board", ApiTestContext.Json, CancellationToken))!;

    private static string ItemPath(string key, string? suffix = null) =>
        $"/api/v1/orgs/{OrgSlug}/items/{key}{(suffix is null ? "" : $"/{suffix}")}";

    private async Task<string> CreateTokenAsync()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/me/tokens",
            new CreateAccessTokenRequest("query-count", [Scopes.Mcp, Scopes.Read], _organizationId, null),
            ApiTestContext.Json, CancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AccessTokenCreated>(ApiTestContext.Json, CancellationToken))!.Secret;
    }

    private async Task<McpClient> ConnectAsync(string token)
    {
        var http = _context.Factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, "/mcp") },
            http, loggerFactory: null, ownsHttpClient: true);
        return await McpClient.CreateAsync(transport, cancellationToken: CancellationToken);
    }

    private static string Text(CallToolResult result) =>
        result.Content.OfType<TextContentBlock>().Single().Text;

    private static JsonElement Json(CallToolResult result) =>
        JsonDocument.Parse(Text(result)).RootElement.Clone();
}

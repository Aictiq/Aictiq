using System.Net.Http.Json;
using System.Diagnostics;
using System.Net.Http.Headers;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.Identity.Endpoints;
using Aictiq.Modules.Tenancy.Domain;
using Aictiq.Modules.Tenancy.Endpoints;
using Aictiq.Modules.WorkItems.Domain;
using Aictiq.Modules.WorkItems.Endpoints;
using Aictiq.SharedKernel.Authorization;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Aictiq.IntegrationTests.Mcp;

/// <summary>
/// The one collection that runs on its own. Everything here asserts a latency budget, and
/// a budget measured while three other test classes are driving the same Postgres is
/// measuring the runner, not the product. Keep it to tests that genuinely need the
/// machine to themselves: the suite is as slow as this collection is long.
/// </summary>
[CollectionDefinition("latency", DisableParallelization = true)]
public sealed class LatencyCollection;

/// <summary>
/// Separate from <see cref="McpTests"/> so that the other twenty-odd MCP tests stay in
/// the parallel pool; only the load test pays for running alone.
/// </summary>
[Trait("Category", "Mcp")]
[Collection("latency")]
public sealed class McpLatencyTests(PostgresFixture postgres, GarageFixture garage) : IAsyncLifetime
{
    private ApiTestContext _context = null!;
    private HttpClient _owner = null!;
    private Guid _organizationId;

    public async ValueTask InitializeAsync()
    {
        _context = await ApiTestContext.CreateAsync(postgres, garage, "mcp_latency");
        var auth = await _context.RegisterAsync("ada@mcp.test", "Ada", "Lovelace");
        _owner = _context.ClientFor(auth);
        var response = await _owner.PostAsJsonAsync("/api/v1/orgs",
            new CreateOrganizationRequest("MCP", "mcp-org", null, null), TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        _organizationId = (await response.Content.ReadFromJsonAsync<OrganizationView>(
            ApiTestContext.Json, TestContext.Current.CancellationToken))!.Id;
    }

    public async ValueTask DisposeAsync()
    {
        _owner.Dispose();
        await _context.DisposeAsync();
    }

    [Fact]
    public async Task fifty_concurrent_agents_polling_ready_work_meet_the_p95_target()
    {
        var project = await CreateProjectAsync("LOAD");
        await CreateItemAsync(project, "Ready for polling");
        var tokens = await Task.WhenAll(Enumerable.Range(0, 50)
            .Select(_ => CreateTokenAsync([Scopes.Mcp, Scopes.Read])));
        var clients = await Task.WhenAll(tokens.Select(ConnectAsync));
        var samples = new List<double>(capacity: 150);

        try
        {
            for (var poll = 0; poll < 3; poll++)
            {
                var elapsed = await Task.WhenAll(clients.Select(async client =>
                {
                    var stopwatch = Stopwatch.StartNew();
                    var result = await CallAsync(client, "list_ready_work", new() { ["project"] = project.Key });
                    Assert.NotEqual(true, result.IsError);
                    return stopwatch.Elapsed.TotalMilliseconds;
                }));
                samples.AddRange(elapsed);
                if (poll < 2) await Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            }
        }
        finally
        {
            foreach (var client in clients) await client.DisposeAsync();
        }

        var p95 = samples.Order().ElementAt((int)Math.Ceiling(samples.Count * .95) - 1);
        Console.WriteLine($"MCP list_ready_work 50-client polling p95: {p95:F1} ms");
        Assert.True(p95 < 300, $"Expected MCP list_ready_work p95 < 300 ms; observed {p95:F1} ms.");
    }

    private async Task<McpClient> ConnectAsync(string token)
    {
        var http = _context.Factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, "/mcp") },
            http, loggerFactory: null, ownsHttpClient: true);
        return await McpClient.CreateAsync(transport, cancellationToken: TestContext.Current.CancellationToken);
    }

    private static ValueTask<CallToolResult> CallAsync(McpClient client, string tool, Dictionary<string, object?> arguments) =>
        client.CallToolAsync(tool, arguments, cancellationToken: TestContext.Current.CancellationToken);

    private async Task<ProjectView> CreateProjectAsync(string key)
    {
        var response = await _owner.PostAsJsonAsync("/api/v1/orgs/mcp-org/projects/",
            new CreateProjectRequest($"Project {key}", key, null, ProjectVisibility.Organization, null, null),
            ApiTestContext.Json, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProjectView>(ApiTestContext.Json, TestContext.Current.CancellationToken))!;
    }

    private async Task<WorkItemView> CreateItemAsync(ProjectView project, string title)
    {
        var response = await _owner.PostAsJsonAsync($"/api/v1/orgs/mcp-org/projects/{project.Key}/items/",
            new CreateWorkItemRequest(WorkItemType.Bug, title, null, null, null, null, null, null, null, null, null, null, null, null),
            ApiTestContext.Json, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<WorkItemView>(ApiTestContext.Json, TestContext.Current.CancellationToken))!;
    }

    private async Task<string> CreateTokenAsync(IReadOnlyList<string> scopes)
    {
        var response = await _owner.PostAsJsonAsync("/api/v1/me/tokens",
            new CreateAccessTokenRequest("mcp", scopes, _organizationId, null),
            ApiTestContext.Json, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AccessTokenCreated>(
            ApiTestContext.Json, TestContext.Current.CancellationToken))!.Secret;
    }
}

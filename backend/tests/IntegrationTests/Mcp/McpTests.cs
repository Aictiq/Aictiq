using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Diagnostics;
using System.Net.Http.Headers;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.Automation.Domain;
using Aictiq.Modules.Automation.Endpoints;
using Aictiq.Modules.Identity.Endpoints;
using Aictiq.Modules.Tenancy.Domain;
using Aictiq.Modules.Tenancy.Endpoints;
using Aictiq.Modules.WorkItems.Domain;
using Aictiq.Modules.WorkItems.Endpoints;
using Aictiq.Modules.Wiki.Endpoints;
using Aictiq.SharedKernel.Authorization;
using ModelContextProtocol.Client;
using Npgsql;
using ModelContextProtocol.Protocol;
using SkiaSharp;

namespace Aictiq.IntegrationTests.Mcp;

[Trait("Category", "Mcp")]
[Collection("postgres")]
public sealed class McpTests(PostgresFixture postgres, GarageFixture garage) : IAsyncLifetime
{
    private ApiTestContext _context = null!;
    private HttpClient _owner = null!;
    private HttpClient _colleague = null!;
    private string _colleagueId = null!;
    private string _ownerId = null!;
    private Guid _organizationId;

    public async ValueTask InitializeAsync()
    {
        _context = await ApiTestContext.CreateAsync(postgres, garage, "mcp");
        var auth = await _context.RegisterAsync("ada@mcp.test", "Ada", "Lovelace");
        _ownerId = auth.User.Id;
        _owner = _context.ClientFor(auth);
        var response = await _owner.PostAsJsonAsync("/api/v1/orgs",
            new CreateOrganizationRequest("MCP", "mcp-org", null, null), TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        _organizationId = (await response.Content.ReadFromJsonAsync<OrganizationView>(
            ApiTestContext.Json, TestContext.Current.CancellationToken))!.Id;

        // A second person in the organization: the agent's token authenticates as Ada, so
        // "someone else's work" and "someone else's comment" need an actual someone else.
        var colleague = await _context.RegisterAsync("grace@mcp.test", "Grace", "Hopper");
        _colleague = _context.ClientFor(colleague);
        _colleagueId = colleague.User.Id;
        await AddMemberAsync(_colleagueId);
    }

    /// <param name="canOperateFactory">The factory-operator flag; null means what the role implies (everyone but a Guest).</param>
    private async Task AddMemberAsync(string userId, OrgRole role = OrgRole.Member, bool? canOperateFactory = null)
    {
        await using var connection = new NpgsqlConnection(_context.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO tenancy.organization_members (organization_id, user_id, role, joined_at, can_operate_factory)
            VALUES (@org, @user, @role, now(), @operator)
            """, connection);
        command.Parameters.AddWithValue("org", _organizationId);
        command.Parameters.AddWithValue("user", userId);
        command.Parameters.AddWithValue("role", (int)role);
        command.Parameters.AddWithValue("operator", canOperateFactory ?? role != OrgRole.Guest);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _owner.Dispose();
        _colleague.Dispose();
        await _context.DisposeAsync();
    }

    [Fact]
    public async Task a_bound_mcp_pat_can_list_tools_and_call_whoami()
    {
        var token = await CreateTokenAsync([Scopes.Mcp, Scopes.Read]);
        using var authenticated = _context.Factory.CreateClient();
        authenticated.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        await using var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(authenticated.BaseAddress!, "/mcp")
        }, authenticated, loggerFactory: null, ownsHttpClient: false);
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: TestContext.Current.CancellationToken);

        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(tools, tool => tool.Name == "whoami");
        var whoami = await client.CallToolAsync("whoami", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotEqual(true, whoami.IsError);
        // An agent reads this before trying to delegate a run.
        Assert.Contains(whoami.Content, block => block is TextContentBlock text
            && text.Text.Contains("\"canOperateFactory\":", StringComparison.Ordinal));
        var projects = await client.CallToolAsync("list_projects", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotEqual(true, projects.IsError);
        var templates = await client.ListResourceTemplatesAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(templates, template => template.UriTemplate == "aictiq://wiki/{project}/{slug}");
        Assert.Contains(templates, template => template.UriTemplate == "aictiq://project/{key}/workflow");
        var prompts = await client.ListPromptsAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(prompts, prompt => prompt.Name == "triage-bug");
    }

    [Fact]
    public async Task create_item_takes_its_number_from_the_project_sequence()
    {
        var project = await CreateProjectAsync("SEQ");
        var first = await CreateItemAsync(project, "Through the API");
        await using var client = await ConnectAsync(await CreateTokenAsync([Scopes.Mcp, Scopes.Read, Scopes.Write]));

        var created = Json(await CallAsync(client, "create_item", new() { ["project"] = project.Key, ["title"] = "Through MCP" }));
        Assert.Equal($"{project.Key}-2", created.GetProperty("key").GetString());
        // The regression: an MCP create that took max + 1 left the sequence behind, so the
        // next create through the API collided on the number and answered 409.
        var third = await CreateItemAsync(project, "Through the API again");
        Assert.Equal($"{project.Key}-3", third.Key);
        Assert.Equal($"{project.Key}-1", first.Key);
    }

    [Fact]
    public async Task every_tool_obeys_the_read_write_admin_and_non_member_matrix()
    {
        var project = await CreateProjectAsync("MAT");
        var item = await CreateItemAsync(project, "Scope matrix item");
        var readToken = await CreateTokenAsync([Scopes.Mcp, Scopes.Read]);
        var writeToken = await CreateTokenAsync([Scopes.Mcp, Scopes.Read, Scopes.Write]);
        var adminToken = await CreateTokenAsync([Scopes.Mcp, Scopes.Admin]);

        await using var read = await ConnectAsync(readToken);
        await using var write = await ConnectAsync(writeToken);
        await using var admin = await ConnectAsync(adminToken);
        var tools = await admin.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEmpty(tools);
        foreach (var tool in tools)
        {
            var arguments = ArgumentsFor(tool.Name, project.Key, item.Key, item.Version);
            var readResult = await CallAsync(read, tool.Name, arguments);
            Assert.NotEmpty(readResult.Content);
            var readOnly = tool.ProtocolTool.Annotations?.ReadOnlyHint == true || tool.Name == "whoami";
            Assert.Equal(!readOnly, readResult.IsError == true && Text(readResult).Contains("requires the 'write'", StringComparison.Ordinal));

            // The same calls may correctly fail validation or conflict after another tool
            // changes the seed item. What must never happen is a scope denial for a token
            // whose grants cover the tool, so this exercises every registered tool without
            // relying on one brittle happy-path fixture per operation.
            foreach (var client in new[] { write, admin })
            {
                var result = await CallAsync(client, tool.Name, arguments);
                Assert.NotEmpty(result.Content);
                Assert.DoesNotContain("requires the", Text(result), StringComparison.Ordinal);
            }
        }

        var outsider = await _context.RegisterAsync("outsider@mcp.test", "Out", "Sider");
        var outsiderHttp = _context.ClientFor(outsider);
        await AddMemberAsync(outsider.User.Id);
        var outsiderToken = await CreateTokenAsync(outsiderHttp, [Scopes.Mcp, Scopes.Read, Scopes.Write]);
        var removal = await _owner.DeleteAsync($"/api/v1/orgs/mcp-org/members/{outsider.User.Id}", TestContext.Current.CancellationToken);
        removal.EnsureSuccessStatusCode();
        await using var nonMember = await ConnectAsync(outsiderToken);

        foreach (var tool in tools.Where(tool => tool.Name != "whoami"))
        {
            var result = await CallAsync(nonMember, tool.Name, ArgumentsFor(tool.Name, project.Key, item.Key, item.Version));
            Assert.True(result.IsError);
            Assert.NotEmpty(result.Content);
            Assert.Contains("not a member", Text(result), StringComparison.Ordinal);
        }
        outsiderHttp.Dispose();
    }

    /// <summary>
    /// Every "nothing happened" a tool can mean — the thing is not
    /// there (or the caller may not see it, which answers identically), or it lost a
    /// compare-and-swap — arrives as text an agent can act on, on a successful result.
    /// The two answers use the same words the REST surface uses for 404 and 409.
    /// </summary>
    [Fact]
    public async Task tools_that_find_nothing_answer_in_text_and_never_in_silence()
    {
        var project = await CreateProjectAsync("NUL");
        var item = await CreateItemAsync(project, "Nothing to find");
        await using var client = await ConnectAsync();

        var missingPage = await CallAsync(client, "get_page", new() { ["project"] = project.Key, ["slugOrId"] = "no-such-page" });
        Assert.NotEqual(true, missingPage.IsError);
        Assert.Contains("not found or no access", Text(missingPage), StringComparison.Ordinal);

        var missingItem = await CallAsync(client, "get_item", new() { ["key"] = $"{project.Key}-999" });
        Assert.NotEqual(true, missingItem.IsError);
        Assert.Contains("not found or no access", Text(missingItem), StringComparison.Ordinal);

        var missingSprint = await CallAsync(client, "get_sprint", new() { ["id"] = Guid.NewGuid() });
        Assert.NotEqual(true, missingSprint.IsError);
        Assert.Contains("not found or no access", Text(missingSprint), StringComparison.Ordinal);

        var missingProject = await CallAsync(client, "get_project", new() { ["projectKey"] = "NOPE" });
        Assert.NotEqual(true, missingProject.IsError);
        Assert.Contains("not found or no access", Text(missingProject), StringComparison.Ordinal);

        var staleTransition = await CallAsync(client, "transition_item",
            new() { ["key"] = item.Key, ["version"] = item.Version + 1, ["toState"] = "Active" });
        Assert.NotEqual(true, staleTransition.IsError);
        Assert.Contains("conflict: version changed", Text(staleTransition), StringComparison.Ordinal);

        var page = Json(await CallAsync(client, "create_page",
            new() { ["project"] = project.Key, ["title"] = "Answer in words", ["markdown"] = "words" }));
        var stalePage = await CallAsync(client, "update_page",
            new() { ["id"] = page.GetProperty("id").GetString(), ["version"] = 99u, ["markdown"] = "words" });
        Assert.NotEqual(true, stalePage.IsError);
        Assert.Contains("conflict: version changed", Text(stalePage), StringComparison.Ordinal);
    }

    [Fact]
    public async Task search_items_filters_and_searches_before_the_limit_and_reports_a_bad_filter()
    {
        var project = await CreateProjectAsync("SRCH");
        for (var i = 0; i < 3; i++) await CreateItemAsync(project, $"Filler {i}");
        var needle = await CreateItemAsync(project, "Timeout on upload");
        await using var client = await ConnectAsync();

        // The match ranks below the first `limit` items; filtering that page would miss it.
        var found = await CallAsync(client, "search_items", new() { ["project"] = project.Key, ["q"] = "timeout", ["limit"] = 1 });
        Assert.NotEqual(true, found.IsError);
        Assert.Contains(needle.Key, Text(found), StringComparison.Ordinal);

        var none = await CallAsync(client, "search_items", new() { ["project"] = project.Key, ["filter"] = "assignee:none state:completed" });
        Assert.Equal("[]", Text(none));

        var bad = await CallAsync(client, "search_items", new() { ["project"] = project.Key, ["filter"] = "state:bogus" });
        Assert.True(bad.IsError);
        Assert.Contains("Unknown state category", Text(bad), StringComparison.Ordinal);
    }

    [Fact]
    public async Task mcp_limits_reject_oversized_requests_results_and_comment_bodies()
    {
        var project = await CreateProjectAsync("LIM");
        var item = await CreateItemAsync(project, "Limits item");
        var token = await CreateTokenAsync([Scopes.Mcp, Scopes.Read, Scopes.Write]);
        await using var client = await ConnectAsync(token);

        var tooMany = await CallAsync(client, "search_items", new() { ["project"] = project.Key, ["limit"] = 201 });
        Assert.True(tooMany.IsError);
        Assert.Contains("between 1 and 200", Text(tooMany), StringComparison.Ordinal);

        var tooLong = await CallAsync(client, "add_comment", new() { ["key"] = item.Key, ["bodyMarkdown"] = new string('x', 20_001) });
        Assert.True(tooLong.IsError);
        Assert.Contains("20,000", Text(tooLong), StringComparison.Ordinal);

        using var raw = _context.Factory.CreateClient();
        raw.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var oversized = new StringContent($"{{\"jsonrpc\":\"2.0\",\"method\":\"tools/list\",\"id\":1,\"padding\":\"{new string('x', 1_048_576)}\"}}", System.Text.Encoding.UTF8, "application/json");
        var response = await raw.PostAsync("/mcp", oversized, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task rate_limited_tool_returns_retry_after_to_the_agent()
    {
        await using var limited = await ApiTestContext.CreateAsync(postgres, garage, "mcp-rate", settings =>
            settings["RateLimiting:McpPermitLimitPerMinute"] = "1");
        var owner = await limited.RegisterAsync("rate@mcp.test", "Rate", "Limited");
        using var ownerHttp = limited.ClientFor(owner);
        var organization = await ownerHttp.PostAsJsonAsync("/api/v1/orgs",
            new CreateOrganizationRequest("Rate", "mcp-rate", null, null), TestContext.Current.CancellationToken);
        organization.EnsureSuccessStatusCode();
        var organizationId = (await organization.Content.ReadFromJsonAsync<OrganizationView>(ApiTestContext.Json, TestContext.Current.CancellationToken))!.Id;
        var issued = await ownerHttp.PostAsJsonAsync("/api/v1/me/tokens",
            new CreateAccessTokenRequest("rate", [Scopes.Mcp, Scopes.Read], organizationId, null), TestContext.Current.CancellationToken);
        issued.EnsureSuccessStatusCode();
        var token = (await issued.Content.ReadFromJsonAsync<AccessTokenCreated>(ApiTestContext.Json, TestContext.Current.CancellationToken))!.Secret;

        await using var client = await ConnectAsync(limited, token);
        Assert.NotEqual(true, (await client.CallToolAsync("whoami", cancellationToken: TestContext.Current.CancellationToken)).IsError);
        var blocked = await client.CallToolAsync("whoami", cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(blocked.IsError);
        Assert.Contains("Retry-After:", Text(blocked), StringComparison.Ordinal);
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

    [Fact]
    public async Task mcp_requires_authentication_and_the_mcp_scope()
    {
        var anonymous = await _context.Anonymous().PostAsync("/mcp", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var noMcpScope = await CreateTokenAsync([Scopes.Read]);
        using var client = _context.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", noMcpScope);
        var forbidden = await client.PostAsync("/mcp", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    /// <summary>
    /// The loop `docs/agents.md` tells an agent to run: find ready work, then keep one
    /// progress comment up to date instead of adding one per step. Both are assertions
    /// about behaviour a person never sees — an agent that gets an empty ready list, or
    /// that cannot edit its own comment, quietly falls back to spamming the thread.
    /// </summary>
    [Fact]
    public async Task ready_work_is_unassigned_or_mine_and_never_someone_else_s()
    {
        var project = await CreateProjectAsync("RDY");
        var free = await CreateItemAsync(project, "Nobody has this");
        var mine = await CreateItemAsync(project, "Already mine");
        var theirs = await CreateItemAsync(project, "Someone else has this");

        await using var client = await ConnectAsync();
        // Claiming assigns it to the token's identity, which is what makes it "mine".
        await CallAsync(client, "claim_item", new() { ["key"] = mine.Key, ["version"] = mine.Version });
        await AssignToColleagueAsync(theirs);

        var ready = Json(await CallAsync(client, "list_ready_work", new() { ["project"] = "RDY" }));
        var keys = ready.EnumerateArray().Select(x => x.GetProperty("key").GetString()).ToList();

        Assert.Contains(free.Key, keys);
        Assert.Contains(mine.Key, keys);
        Assert.DoesNotContain(theirs.Key, keys);
    }

    [Fact]
    public async Task an_agent_can_rewrite_its_own_progress_comment_but_not_another_s()
    {
        var project = await CreateProjectAsync("PRG");
        var item = await CreateItemAsync(project, "Progress reporting");

        await using var client = await ConnectAsync();
        var added = Json(await CallAsync(client, "add_comment",
            new() { ["key"] = item.Key, ["bodyMarkdown"] = "<!-- aictiq:progress -->\nstep one" }));
        var commentId = added.GetProperty("id").GetString()!;

        var edited = Json(await CallAsync(client, "update_comment",
            new() { ["key"] = item.Key, ["commentId"] = commentId, ["bodyMarkdown"] = "<!-- aictiq:progress -->\nstep two" }));
        Assert.False(edited.GetProperty("unchanged").GetBoolean());

        // Still one comment: the point of editing is that a retried loop does not grow the
        // thread, and the previous body survives as a revision.
        var comments = Json(await CallAsync(client, "list_comments", new() { ["key"] = item.Key }));
        var progress = comments.EnumerateArray()
            .Where(x => x.GetProperty("bodyMarkdown").GetString()!.Contains("aictiq:progress")).ToList();
        Assert.Single(progress);
        Assert.Contains("step two", progress[0].GetProperty("bodyMarkdown").GetString());

        // Someone else's comment is not the agent's to rewrite.
        var mine = await _colleague.PostAsJsonAsync($"/api/v1/orgs/mcp-org/items/{item.Key}/comments/",
            new CreateCommentRequest("A person wrote this."), ApiTestContext.Json, TestContext.Current.CancellationToken);
        mine.EnsureSuccessStatusCode();
        var theirs = (await mine.Content.ReadFromJsonAsync<CommentView>(ApiTestContext.Json, TestContext.Current.CancellationToken))!;

        await CallAsync(client, "update_comment",
            new() { ["key"] = item.Key, ["commentId"] = theirs.Id.ToString(), ["bodyMarkdown"] = "rewritten" });

        var afterRefusal = Json(await CallAsync(client, "list_comments", new() { ["key"] = item.Key }));
        var untouched = afterRefusal.EnumerateArray()
            .Single(x => x.GetProperty("id").GetString() == theirs.Id.ToString());
        Assert.Equal("A person wrote this.", untouched.GetProperty("bodyMarkdown").GetString());
    }

    [Fact]
    public async Task wiki_tools_create_agent_authored_revisions_and_bound_page_reads()
    {
        var project = await CreateProjectAsync("WIK");
        await using var client = await ConnectAsync();
        var created = Json(await CallAsync(client, "create_page", new() { ["project"] = project.Key, ["title"] = "Runbook", ["markdown"] = new string('d', 20_010) }));
        Assert.True(created.GetProperty("truncated").GetBoolean());
        var pageId = created.GetProperty("id").GetString()!;
        var nextRange = created.GetProperty("range").GetString()!;
        var continued = Json(await CallAsync(client, "get_page", new() { ["project"] = project.Key, ["slugOrId"] = pageId, ["range"] = nextRange }));
        Assert.False(continued.GetProperty("truncated").GetBoolean());

        var page = await _owner.GetFromJsonAsync<WikiPageView>($"/api/v1/orgs/mcp-org/wiki/pages/{pageId}", ApiTestContext.Json, TestContext.Current.CancellationToken);
        Assert.NotNull(page);
        var changed = Json(await CallAsync(client, "update_page", new() { ["id"] = pageId, ["version"] = page!.Version, ["markdown"] = "## Implementation notes\nAgent update" }));
        Assert.Equal(2, changed.GetProperty("revisionNumber").GetInt32());
        var revisions = await _owner.GetFromJsonAsync<List<WikiRevisionView>>($"/api/v1/orgs/mcp-org/wiki/pages/{pageId}/revisions", ApiTestContext.Json, TestContext.Current.CancellationToken);
        Assert.Equal(_ownerId, revisions!.Single(x => x.Number == 2).AuthorId);
    }

    // ------------------------------------------------------------ object-level authorization

    [Fact]
    public async Task a_member_of_the_organization_cannot_reach_a_private_project_through_mcp()
    {
        var project = await CreateProjectAsync("PRV", ProjectVisibility.Private);
        var item = await CreateItemAsync(project, "Private item");
        await using var colleague = await ConnectAsync(await CreateTokenAsync(_colleague, [Scopes.Mcp, Scopes.Read, Scopes.Write]));

        foreach (var (tool, arguments) in new (string, Dictionary<string, object?>)[]
        {
            ("search_items", new() { ["project"] = project.Key }),
            ("list_ready_work", new() { ["project"] = project.Key }),
            ("get_item", new() { ["key"] = item.Key }),
            ("claim_item", new() { ["key"] = item.Key, ["version"] = item.Version }),
            ("create_item", new() { ["project"] = project.Key, ["title"] = "Smuggled in" }),
            ("create_subtask", new() { ["parentKey"] = item.Key, ["title"] = "Smuggled under" }),
            ("bulk_update", new() { ["project"] = project.Key, ["keys"] = new[] { item.Key }, ["assigneeId"] = _colleagueId }),
        })
        {
            var result = await CallAsync(colleague, tool, arguments);
            var text = Text(result);
            // The list tools answer an empty list; everything else says so in words. Neither
            // leaks the item, and neither distinguishes "private" from "does not exist".
            Assert.DoesNotContain("Private item", text);
            Assert.True(text == "[]" || text.Contains("not found or no access", StringComparison.Ordinal), $"{tool}: {text}");
        }

        var items = await _owner.GetFromJsonAsync<Aictiq.SharedKernel.Paging.PagedResult<WorkItemView>>(
            $"/api/v1/orgs/mcp-org/projects/{project.Key}/items/", ApiTestContext.Json, TestContext.Current.CancellationToken);
        Assert.Single(items!.Items);
        Assert.Null(items.Items[0].AssigneeId);
    }

    [Fact]
    public async Task an_organization_guest_reads_and_comments_but_cannot_write_through_mcp()
    {
        var guest = await _context.RegisterAsync("guest@mcp.test", "Guest", "Reader");
        await AddMemberAsync(guest.User.Id, OrgRole.Guest);
        var project = await CreateProjectAsync("GST");
        var item = await CreateItemAsync(project, "Guest target");
        await using var client = await ConnectAsync(await CreateTokenAsync(_context.ClientFor(guest), [Scopes.Mcp, Scopes.Read, Scopes.Write]));

        Assert.Contains("Guest target", Text(await CallAsync(client, "get_item", new() { ["key"] = item.Key })));
        Assert.Contains("not found or no access", Text(await CallAsync(client, "transition_item",
            new() { ["key"] = item.Key, ["version"] = item.Version, ["toState"] = "Active" })));
        Assert.Contains("not found or no access", Text(await CallAsync(client, "create_item",
            new() { ["project"] = project.Key, ["title"] = "By a guest" })));
        Assert.Equal("false", Text(await CallAsync(client, "update_item",
            new() { ["key"] = item.Key, ["version"] = item.Version, ["title"] = "Renamed by a guest" })));
        Assert.Equal("false", Text(await CallAsync(client, "set_remaining_hours",
            new() { ["key"] = item.Key, ["version"] = item.Version, ["hours"] = 3m })));
        // Guests read and comment: the comment is the one write the role allows.
        Assert.Contains("createdAt", Text(await CallAsync(client, "add_comment",
            new() { ["key"] = item.Key, ["bodyMarkdown"] = "A guest's remark" })));

        var unchanged = await _owner.GetFromJsonAsync<WorkItemView>($"/api/v1/orgs/mcp-org/items/{item.Key}", ApiTestContext.Json, TestContext.Current.CancellationToken);
        Assert.Equal("Guest target", unchanged!.Title);
        Assert.Equal(item.StateId, unchanged.StateId);
    }

    [Fact]
    public async Task resources_pass_the_same_scope_and_membership_gate_as_the_read_tools()
    {
        var project = await CreateProjectAsync("RES");
        var item = await CreateItemAsync(project, "Resource secret");
        var uri = $"aictiq://item/{item.Key}";

        // `mcp` alone is refused by every read tool; a resource is the same data through
        // another door and must be refused too, not answered.
        await using (var mcpOnly = await ConnectAsync(await CreateTokenAsync([Scopes.Mcp])))
        {
            var refused = await Assert.ThrowsAnyAsync<ModelContextProtocol.McpException>(async () =>
                await mcpOnly.ReadResourceAsync(uri, cancellationToken: TestContext.Current.CancellationToken));
            Assert.Contains("requires the 'read'", refused.Message, StringComparison.Ordinal);
        }

        await using (var reader = await ConnectAsync(await CreateTokenAsync([Scopes.Mcp, Scopes.Read])))
        {
            var read = await reader.ReadResourceAsync(uri, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Contains("Resource secret", Assert.IsType<TextResourceContents>(Assert.Single(read.Contents)).Text);
        }

        // A token that outlived its owner's membership reads nothing through a resource either.
        var leaver = await _context.RegisterAsync("leaver@mcp.test", "Lea", "Ver");
        await AddMemberAsync(leaver.User.Id);
        var leaverToken = await CreateTokenAsync(_context.ClientFor(leaver), [Scopes.Mcp, Scopes.Read]);
        (await _owner.DeleteAsync($"/api/v1/orgs/mcp-org/members/{leaver.User.Id}", TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        await using var removed = await ConnectAsync(leaverToken);
        var notMember = await Assert.ThrowsAnyAsync<ModelContextProtocol.McpException>(async () =>
            await removed.ReadResourceAsync(uri, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("not a member", notMember.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task project_and_page_listings_and_the_workflow_resource_answer_with_real_data()
    {
        // Two of each: list_projects ran its per-project lookups concurrently on one
        // DbContext and list_pages sorted a projection EF cannot translate, so both only
        // ever worked on an empty organization.
        var first = await CreateProjectAsync("LSA");
        await CreateProjectAsync("LSB");
        // A project's workflow is created with its first item.
        await CreateItemAsync(first, "Seeds the workflow");
        await using var client = await ConnectAsync();

        var projects = await CallAsync(client, "list_projects", []);
        Assert.NotEqual(true, projects.IsError);
        var keys = Json(projects).EnumerateArray().Select(x => x.GetProperty("key").GetString()).ToList();
        Assert.Contains("LSA", keys);
        Assert.Contains("LSB", keys);

        await CallAsync(client, "create_page", new() { ["project"] = first.Key, ["title"] = "Zeta", ["markdown"] = "z" });
        await CallAsync(client, "create_page", new() { ["project"] = first.Key, ["title"] = "Alpha", ["markdown"] = "a" });
        var pages = await CallAsync(client, "list_pages", new() { ["project"] = first.Key });
        Assert.NotEqual(true, pages.IsError);
        Assert.Equal(["Alpha", "Zeta"], Json(pages).EnumerateArray().Select(x => x.GetProperty("title").GetString()!).ToArray());

        var workflow = await client.ReadResourceAsync($"aictiq://project/{first.Key}/workflow", cancellationToken: TestContext.Current.CancellationToken);
        var document = JsonDocument.Parse(Assert.IsType<TextResourceContents>(Assert.Single(workflow.Contents)).Text).RootElement;
        Assert.Equal(first.Key, document.GetProperty("project").GetString());
        Assert.NotEmpty(document.GetProperty("workflows").EnumerateArray());
    }

    [Fact]
    public async Task update_page_refuses_a_page_in_an_archived_project()
    {
        var project = await CreateProjectAsync("ARW");
        await using var client = await ConnectAsync();
        var created = Json(await CallAsync(client, "create_page", new() { ["project"] = project.Key, ["title"] = "Frozen", ["markdown"] = "before" }));
        var pageId = created.GetProperty("id").GetString()!;
        var page = await _owner.GetFromJsonAsync<WikiPageView>($"/api/v1/orgs/mcp-org/wiki/pages/{pageId}", ApiTestContext.Json, TestContext.Current.CancellationToken);
        (await _owner.PostAsync($"/api/v1/orgs/mcp-org/projects/{project.Key}/archive", null, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        var result = await CallAsync(client, "update_page", new() { ["id"] = pageId, ["version"] = page!.Version, ["markdown"] = "after" });

        Assert.Contains("archived", Text(result), StringComparison.Ordinal);
        var unchanged = await _owner.GetFromJsonAsync<WikiPageView>($"/api/v1/orgs/mcp-org/wiki/pages/{pageId}", ApiTestContext.Json, TestContext.Current.CancellationToken);
        Assert.Equal("before", unchanged!.ContentMarkdown);
    }

    [Fact]
    public async Task bulk_update_refuses_an_assignee_who_cannot_see_the_project()
    {
        var project = await CreateProjectAsync("ASG");
        var item = await CreateItemAsync(project, "Assignable");
        var stranger = await _context.RegisterAsync("stranger@mcp.test", "Stranger", "Outside");
        await using var client = await ConnectAsync();

        var refused = await CallAsync(client, "bulk_update",
            new() { ["project"] = project.Key, ["keys"] = new[] { item.Key }, ["assigneeId"] = stranger.User.Id });
        Assert.Contains("assigneeId", Text(refused));

        var accepted = await CallAsync(client, "bulk_update",
            new() { ["project"] = project.Key, ["keys"] = new[] { item.Key }, ["assigneeId"] = _colleagueId });
        Assert.Equal("1", Text(accepted));
    }

    [Fact]
    public async Task link_item_only_accepts_web_urls()
    {
        var project = await CreateProjectAsync("LNK");
        var item = await CreateItemAsync(project, "Linked");
        await using var client = await ConnectAsync();

        Assert.Equal("false", Text(await CallAsync(client, "link_item", new() { ["key"] = item.Key, ["url"] = "javascript:alert(1)" })));
        Assert.Equal("false", Text(await CallAsync(client, "link_item", new() { ["key"] = item.Key, ["url"] = "http://169.254.169.254/latest/meta-data" })));
        Assert.Equal("true", Text(await CallAsync(client, "link_item", new() { ["key"] = item.Key, ["url"] = "https://example.com/spec" })));
    }

    [Fact]
    public async Task get_item_lists_attachments_and_get_attachment_returns_text_or_an_image()
    {
        var project = await CreateProjectAsync("ATT");
        var item = await CreateItemAsync(project, "Screenshot-only bug");
        var note = await AttachAsync(project, "details.txt", "text/plain", "the exact failure"u8.ToArray(), item.Id, null);
        var image = await AttachAsync(project, "screenshot.png", "image/png", Png(), item.Id, null);
        await using var client = await ConnectAsync();

        var itemResult = Json(await CallAsync(client, "get_item", new() { ["key"] = item.Key }));
        var attachments = itemResult.GetProperty("attachments").EnumerateArray().ToArray();
        Assert.Equal([note.Id, image.Id], attachments.Select(x => x.GetProperty("id").GetGuid()));
        // Null anonymous-object properties are omitted by the API's JSON options; an item
        // attachment consequently has no commentId rather than an explicit null one.
        Assert.All(attachments, attachment => Assert.False(attachment.TryGetProperty("commentId", out _)));

        var text = await CallAsync(client, "get_attachment", new() { ["id"] = note.Id });
        Assert.Equal("the exact failure", Text(text));
        var picture = await CallAsync(client, "get_attachment", new() { ["id"] = image.Id });
        var imageBlock = Assert.IsType<ImageContentBlock>(Assert.Single(picture.Content));
        Assert.Equal("image/webp", imageBlock.MimeType);
        Assert.False(imageBlock.DecodedData.IsEmpty);
    }

    [Fact]
    public async Task get_attachment_does_not_bypass_private_project_access()
    {
        var project = await CreateProjectAsync("LOCK", ProjectVisibility.Private);
        var item = await CreateItemAsync(project, "Private evidence");
        var attachment = await AttachAsync(project, "secret.txt", "text/plain", "private"u8.ToArray(), item.Id, null);
        await using var colleague = await ConnectAsync(await CreateTokenAsync(_colleague, [Scopes.Mcp, Scopes.Read]));

        var result = await CallAsync(colleague, "get_attachment", new() { ["id"] = attachment.Id });

        Assert.NotEqual(true, result.IsError);
        Assert.Contains("not found or no access", Text(result), StringComparison.Ordinal);
    }

    private async Task<McpClient> ConnectAsync() => await ConnectAsync(await CreateTokenAsync([Scopes.Mcp, Scopes.Read, Scopes.Write]));

    private async Task<McpClient> ConnectAsync(string token) => await ConnectAsync(_context, token);

    private static async Task<McpClient> ConnectAsync(ApiTestContext context, string token)
    {
        var http = context.Factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, "/mcp") },
            http, loggerFactory: null, ownsHttpClient: true);
        return await McpClient.CreateAsync(transport, cancellationToken: TestContext.Current.CancellationToken);
    }

    private static ValueTask<CallToolResult> CallAsync(McpClient client, string tool, Dictionary<string, object?> arguments) =>
        client.CallToolAsync(tool, arguments, cancellationToken: TestContext.Current.CancellationToken);

    // A tool with nothing to return answers in words instead of silence. A result
    // with zero content blocks is indistinguishable from a transport fault on the far side
    // of the CLI, so the strict helper refuses to invent "" — if this throws, a tool is
    // returning null or a filter stopped translating answers into text.
    private static string Text(CallToolResult result) =>
        Assert.Single(result.Content.OfType<TextContentBlock>()).Text;

    private static JsonElement Json(CallToolResult result) =>
        JsonDocument.Parse(Text(result)).RootElement.Clone();

    private static Dictionary<string, object?> ArgumentsFor(string tool, string project, string key, uint version) => tool switch
    {
        "whoami" or "list_projects" => [],
        "get_project" or "get_workflow" => new() { ["projectKey"] = project },
        "search_items" => new() { ["project"] = project, ["limit"] = 1 },
        "list_ready_work" => new() { ["project"] = project },
        "get_item" or "release_item" or "heartbeat" or "list_comments" => new() { ["key"] = key },
        "get_attachment" => new() { ["id"] = Guid.NewGuid() },
        "list_sprints" => new() { ["team"] = Guid.NewGuid() },
        "get_sprint" => new() { ["id"] = Guid.NewGuid() },
        "search_wiki" => new() { ["project"] = project, ["q"] = "matrix" },
        "list_pages" => new() { ["project"] = project },
        "get_page" => new() { ["project"] = project, ["slugOrId"] = "matrix" },
        "create_page" => new() { ["project"] = project, ["title"] = "Matrix", ["markdown"] = "matrix" },
        "update_page" => new() { ["id"] = Guid.NewGuid(), ["version"] = 1u, ["markdown"] = "matrix" },
        "claim_item" => new() { ["key"] = key, ["version"] = version },
        "add_comment" => new() { ["key"] = key, ["bodyMarkdown"] = "matrix" },
        "update_comment" => new() { ["key"] = key, ["commentId"] = Guid.NewGuid(), ["bodyMarkdown"] = "matrix" },
        "transition_item" => new() { ["key"] = key, ["version"] = version, ["toState"] = "Active" },
        "set_remaining_hours" => new() { ["key"] = key, ["version"] = version, ["hours"] = 1m },
        "create_item" => new() { ["project"] = project, ["title"] = "Matrix item" },
        "create_subtask" => new() { ["parentKey"] = key, ["title"] = "Matrix subtask" },
        "update_item" => new() { ["key"] = key, ["version"] = version, ["title"] = "Matrix title" },
        "link_item" => new() { ["key"] = key, ["url"] = "https://example.test/matrix" },
        "bulk_update" => new() { ["project"] = project, ["keys"] = new[] { key } },
        "start_run" or "list_runs" => new() { ["key"] = key },
        "get_run" => new() { ["runId"] = Guid.NewGuid() },
        _ => throw new InvalidOperationException($"Add MCP matrix arguments for tool '{tool}'.")
    };

    private async Task<ProjectView> CreateProjectAsync(string key, ProjectVisibility visibility = ProjectVisibility.Organization)
    {
        var response = await _owner.PostAsJsonAsync("/api/v1/orgs/mcp-org/projects/",
            new CreateProjectRequest($"Project {key}", key, null, visibility, null, null),
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

    private async Task<AttachmentView> AttachAsync(ProjectView project, string name, string contentType, byte[] payload, Guid? itemId, Guid? commentId)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(payload);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, "file", name);
        var uploaded = await _owner.PostAsync($"/api/v1/orgs/mcp-org/projects/{project.Key}/attachments", form, TestContext.Current.CancellationToken);
        uploaded.EnsureSuccessStatusCode();
        var attachment = (await uploaded.Content.ReadFromJsonAsync<AttachmentView>(ApiTestContext.Json, TestContext.Current.CancellationToken))!;
        var committed = await _owner.PostAsJsonAsync($"/api/v1/orgs/mcp-org/attachments/{attachment.Id}/commit",
            new CommitAttachmentRequest(itemId, commentId), ApiTestContext.Json, TestContext.Current.CancellationToken);
        committed.EnsureSuccessStatusCode();
        return attachment;
    }

    private static byte[] Png()
    {
        using var bitmap = new SKBitmap(2, 2);
        bitmap.Erase(SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>
    /// An agent connected over MCP starts a run for a subtask it
    /// created, polls <c>get_run</c> and sees it finish — through the same dispatcher the
    /// REST endpoint uses, so the second dispatch conflicts exactly as it does there.
    /// </summary>
    [Fact]
    public async Task an_agent_delegates_a_subtask_over_mcp_and_watches_it_finish()
    {
        var project = await CreateProjectAsync("DLG");
        var factory = await FactoryAsync(project);
        var parent = await CreateItemAsync(project, "Ship the login page");
        await using var client = await ConnectAsync();

        var subtask = Json(await CallAsync(client, "create_subtask", new() { ["parentKey"] = parent.Key, ["title"] = "Write the redirect" })).GetProperty("key").GetString()!;
        var started = await CallAsync(client, "start_run", new() { ["key"] = subtask, ["playbook"] = "implement", ["agent"] = "Builder" });
        Assert.NotEqual(true, started.IsError);
        var runId = Json(started).GetProperty("runId").GetGuid();
        Assert.Equal("queued", Json(started).GetProperty("status").GetString());
        Assert.Equal(factory.AgentId, Json(started).GetProperty("agentId").GetString());

        var queued = Json(await CallAsync(client, "get_run", new() { ["runId"] = runId }));
        Assert.Equal("queued", queued.GetProperty("status").GetString());
        Assert.Equal(subtask, queued.GetProperty("itemKey").GetString());
        Assert.Equal(_ownerId, queued.GetProperty("requestedBy").GetString());
        Assert.Equal(JsonValueKind.Array, queued.GetProperty("log").ValueKind);

        var listed = Json(await CallAsync(client, "list_runs", new() { ["key"] = subtask }));
        Assert.Contains(listed.EnumerateArray(), run => run.GetProperty("id").GetGuid() == runId);

        var again = await CallAsync(client, "start_run", new() { ["key"] = subtask });
        Assert.NotEqual(true, again.IsError);
        Assert.Contains(Text(again), new[] { "item already claimed", "conflict: run in progress" });

        // A runner takes the run, writes two lines and finishes it, as a real one would.
        using var runner = _context.Factory.CreateClient();
        runner.Timeout = TimeSpan.FromSeconds(40);
        runner.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.RunnerSecret);
        var claim = await runner.PostAsJsonAsync("/api/v1/runner/runs/claim", new RunnerClaimRequest(["claude"], 1), ApiTestContext.Json, TestContext.Current.CancellationToken);
        Assert.True(claim.StatusCode == HttpStatusCode.OK, await claim.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(runId, (await claim.Content.ReadFromJsonAsync<RunnerRunClaimed>(ApiTestContext.Json, TestContext.Current.CancellationToken))!.RunId);
        (await runner.PostAsync($"/api/v1/runner/runs/{runId}/started", null, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        (await runner.PostAsJsonAsync($"/api/v1/runner/runs/{runId}/log",
            new RunnerLogRequest([new RunnerLogChunk(0, RunLogStream.Stdout, "line one", null), new RunnerLogChunk(1, RunLogStream.Stderr, "line two", null)]),
            ApiTestContext.Json, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        (await runner.PostAsJsonAsync($"/api/v1/runner/runs/{runId}/finish",
            new RunnerFinishRequest("succeeded", 0, "Opened a pull request", "https://example.test/pr/1", null, null, null, null),
            ApiTestContext.Json, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        var finished = Json(await CallAsync(client, "get_run", new() { ["runId"] = runId }));
        Assert.Equal("succeeded", finished.GetProperty("status").GetString());
        Assert.Equal("https://example.test/pr/1", finished.GetProperty("pullRequestUrl").GetString());
        Assert.Equal(["line one", "line two"], finished.GetProperty("log").EnumerateArray().Select(line => line.GetProperty("text").GetString()!).ToArray());

        var resource = await client.ReadResourceAsync($"aictiq://run/{runId}", cancellationToken: TestContext.Current.CancellationToken);
        var markdown = Assert.IsType<TextResourceContents>(Assert.Single(resource.Contents)).Text;
        Assert.Contains("(succeeded)", markdown, StringComparison.Ordinal);
        Assert.Contains("[stderr] line two", markdown, StringComparison.Ordinal);
        Assert.Contains("Opened a pull request", markdown, StringComparison.Ordinal);
    }

    /// <summary>
    /// The factory flag over MCP: a Member whose flag is off sees the run — it is the item's
    /// history — but may neither start one nor read the agent's raw output.
    /// </summary>
    [Fact]
    public async Task a_stakeholder_sees_a_run_but_neither_starts_one_nor_reads_its_log()
    {
        var project = await CreateProjectAsync("STK");
        await FactoryAsync(project);
        var item = await CreateItemAsync(project, "Operator starts this");
        var other = await CreateItemAsync(project, "Stakeholder may not start this");
        await using var owner = await ConnectAsync();
        var runId = Json(await CallAsync(owner, "start_run", new() { ["key"] = item.Key })).GetProperty("runId").GetGuid();

        var stakeholder = await _context.RegisterAsync("stake@mcp.test", "Sta", "Keholder");
        using var stakeholderHttp = _context.ClientFor(stakeholder);
        await AddMemberAsync(stakeholder.User.Id, OrgRole.Member, canOperateFactory: false);
        await using var client = await ConnectAsync(await CreateTokenAsync(stakeholderHttp, [Scopes.Mcp, Scopes.Read, Scopes.Write]));

        var refused = await CallAsync(client, "start_run", new() { ["key"] = other.Key });
        Assert.NotEqual(true, refused.IsError);
        Assert.Equal("not permitted to operate the factory", Text(refused));

        var seen = Json(await CallAsync(client, "get_run", new() { ["runId"] = runId }));
        Assert.Equal("queued", seen.GetProperty("status").GetString());
        Assert.True(!seen.TryGetProperty("log", out var log) || log.ValueKind == JsonValueKind.Null);
        Assert.True(!seen.TryGetProperty("failureReason", out var reason) || reason.ValueKind == JsonValueKind.Null);
        Assert.Contains(Json(await CallAsync(client, "list_runs", new() { ["key"] = item.Key })).EnumerateArray(), run => run.GetProperty("id").GetGuid() == runId);

        var resource = await client.ReadResourceAsync($"aictiq://run/{runId}", cancellationToken: TestContext.Current.CancellationToken);
        var markdown = Assert.IsType<TextResourceContents>(Assert.Single(resource.Contents)).Text;
        Assert.Contains("(queued)", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("## Log", markdown, StringComparison.Ordinal);

        // The read-only token of an operator is refused by scope, not by the flag.
        await using var readOnly = await ConnectAsync(await CreateTokenAsync([Scopes.Mcp, Scopes.Read]));
        var scoped = await CallAsync(readOnly, "start_run", new() { ["key"] = other.Key });
        Assert.True(scoped.IsError);
        Assert.Contains("requires the 'write'", Text(scoped), StringComparison.Ordinal);
    }

    [Fact]
    public async Task runs_in_a_private_project_answer_not_found_to_a_member_outside_it()
    {
        var project = await CreateProjectAsync("PRV", ProjectVisibility.Private);
        await FactoryAsync(project);
        var item = await CreateItemAsync(project, "Private work");
        await using var owner = await ConnectAsync();
        var runId = Json(await CallAsync(owner, "start_run", new() { ["key"] = item.Key })).GetProperty("runId").GetGuid();

        // Grace is an operator of the organization and not on this project.
        await using var outsider = await ConnectAsync(await CreateTokenAsync(_colleague, [Scopes.Mcp, Scopes.Read, Scopes.Write]));
        foreach (var (tool, arguments) in new (string, Dictionary<string, object?>)[]
        {
            ("start_run", new() { ["key"] = item.Key }),
            ("list_runs", new() { ["key"] = item.Key }),
            ("get_run", new() { ["runId"] = runId }),
        })
        {
            var result = await CallAsync(outsider, tool, arguments);
            Assert.NotEqual(true, result.IsError);
            Assert.Contains("not found or no access", Text(result), StringComparison.Ordinal);
        }
        var resource = await outsider.ReadResourceAsync($"aictiq://run/{runId}", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("# Run not found", Assert.IsType<TextResourceContents>(Assert.Single(resource.Contents)).Text);
    }

    private sealed record FactoryFixture(string AgentId, Guid PlaybookId, string RunnerSecret);

    /// <summary>An agent on the project, the starter playbook as default, the agent as default, and a runner.</summary>
    private async Task<FactoryFixture> FactoryAsync(ProjectView project)
    {
        var agent = await _owner.PostAsJsonAsync("/api/v1/orgs/mcp-org/agents", new CreateAgentRequest("builder", [project.Id]), ApiTestContext.Json, TestContext.Current.CancellationToken);
        agent.EnsureSuccessStatusCode();
        var agentId = (await agent.Content.ReadFromJsonAsync<AgentView>(ApiTestContext.Json, TestContext.Current.CancellationToken))!.UserId;
        var starter = await _owner.PostAsync($"/api/v1/orgs/mcp-org/projects/{project.Key}/playbooks/starter", null, TestContext.Current.CancellationToken);
        Assert.True(starter.StatusCode == HttpStatusCode.Created, await starter.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var playbookId = (await starter.Content.ReadFromJsonAsync<PlaybookView>(ApiTestContext.Json, TestContext.Current.CancellationToken))!.Id;
        (await _owner.PutAsJsonAsync($"/api/v1/orgs/mcp-org/projects/{project.Key}/factory-settings",
            new UpdateFactorySettingsRequest((short)ProjectRepositorySource.RunnerLocal, null, "main", "/srv/mcp", agentId, 0),
            ApiTestContext.Json, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        var runner = await _owner.PostAsJsonAsync("/api/v1/orgs/mcp-org/runners", new CreateRunnerRequest($"box-{project.Key}"), ApiTestContext.Json, TestContext.Current.CancellationToken);
        Assert.True(runner.StatusCode == HttpStatusCode.Created, await runner.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return new FactoryFixture(agentId, playbookId, (await runner.Content.ReadFromJsonAsync<RunnerIssuedView>(ApiTestContext.Json, TestContext.Current.CancellationToken))!.Secret);
    }

    private async Task AssignToColleagueAsync(WorkItemView item)
    {
        var response = await _owner.PatchAsJsonAsync($"/api/v1/orgs/mcp-org/items/{item.Key}",
            new UpdateWorkItemRequest(null, null, null, null, null, _colleagueId, null, null, null, null, null, null, null, null, item.Version),
            ApiTestContext.Json, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private Task<string> CreateTokenAsync(IReadOnlyList<string> scopes) => CreateTokenAsync(_owner, scopes);

    private async Task<string> CreateTokenAsync(HttpClient owner, IReadOnlyList<string> scopes)
    {
        var response = await owner.PostAsJsonAsync("/api/v1/me/tokens",
            new CreateAccessTokenRequest("mcp", scopes, _organizationId, null),
            ApiTestContext.Json, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AccessTokenCreated>(
            ApiTestContext.Json, TestContext.Current.CancellationToken))!.Secret;
    }
}

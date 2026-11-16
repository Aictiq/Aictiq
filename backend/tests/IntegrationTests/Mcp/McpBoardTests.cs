using System.Net;
using System.Net.Http.Json;
using Aictiq.Modules.Tenancy.Domain;
using Aictiq.Modules.WorkItems.Domain;
using Aictiq.Modules.WorkItems.Endpoints;
using Aictiq.SharedKernel.Authorization;

namespace Aictiq.IntegrationTests.Mcp;

public sealed partial class McpTests
{
    [Fact]
    public async Task get_board_exposes_custom_columns_and_get_item_resolves_implicit_placement()
    {
        var project = await CreateProjectAsync("BRD");
        var item = await CreateItemAsync(project, "Board item");
        var workflow = await BoardWorkflowAsync(project.Key);
        var active = workflow.States.Single(x => x.Name == "Active");
        var board = await ConfigureBoardAsync(item,
            new BoardColumnConfig("To do", [item.StateId], null, Guid.NewGuid()),
            new BoardColumnConfig("Blocked", [active.Id, item.StateId], 3, Guid.NewGuid()));
        await using var client = await ConnectAsync(await CreateTokenAsync([Scopes.Mcp, Scopes.Read]));

        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(tools.Single(x => x.Name == "get_board").ProtocolTool.Annotations?.ReadOnlyHint);
        var result = Json(await CallAsync(client, "get_board", new() { ["key"] = item.Key }));
        Assert.Equal(item.TeamId, result.GetProperty("teamId").GetGuid());
        var columns = result.GetProperty("columns").EnumerateArray().ToList();
        Assert.Equal(["To do", "Blocked"], columns.Select(x => x.GetProperty("name").GetString()));
        var blocked = columns[1];
        Assert.Equal(board.Columns[1].Id, blocked.GetProperty("id").GetGuid());
        Assert.Equal(3, blocked.GetProperty("wipLimit").GetInt32());
        Assert.Equal([active.Name, "New"], blocked.GetProperty("states").EnumerateArray().Select(x => x.GetProperty("name").GetString()));
        Assert.Null(item.BoardColumnId);
        Assert.Equal("To do", Json(await CallAsync(client, "get_item", new() { ["key"] = item.Key })).GetProperty("column").GetString());
        Assert.Equal(item.Version, (await GetItemAsync(item.Key)).Version);
    }

    [Fact]
    public async Task move_item_preserves_a_mapped_current_state_and_persists_the_distinct_column()
    {
        var project = await CreateProjectAsync("COL");
        var item = await CreateItemAsync(project, "Keep current state");
        var workflow = await BoardWorkflowAsync(project.Key);
        var active = workflow.States.Single(x => x.Name == "Active");
        var board = await ConfigureBoardAsync(item,
            new BoardColumnConfig("To do", [item.StateId], null, Guid.NewGuid()),
            new BoardColumnConfig("Blocked", [active.Id, item.StateId], null, Guid.NewGuid()));
        await SetBoardTransitionsAsync(project.Key, workflow, new WorkflowTransitionInput(item.StateId, active.Id));
        await using var client = await ConnectAsync();

        var result = Json(await CallAsync(client, "move_item", new()
        {
            ["key"] = item.Key, ["version"] = item.Version, ["column"] = "bLoCkEd",
        }));

        var moved = await GetItemAsync(item.Key);
        Assert.Equal(item.StateId, moved.StateId);
        Assert.Equal(board.Columns[1].Id, moved.BoardColumnId);
        Assert.NotEqual(item.Version, moved.Version);
        Assert.Equal(item.Key, result.GetProperty("key").GetString());
        Assert.Equal(moved.Version, result.GetProperty("version").GetUInt32());
        Assert.Equal("Blocked", result.GetProperty("column").GetString());
        Assert.Equal("Blocked", Json(await CallAsync(client, "get_item", new() { ["key"] = item.Key })).GetProperty("column").GetString());
        var rendered = await ReadBoardAsync(item.TeamId!.Value);
        Assert.Empty(rendered.Columns[0].Cards);
        Assert.Equal(item.Key, Assert.Single(rendered.Columns[1].Cards).Key);
    }

    [Fact]
    public async Task move_item_selects_the_first_allowed_mapped_state()
    {
        var project = await CreateProjectAsync("MUL");
        var item = await CreateItemAsync(project, "Select allowed state");
        var workflow = await BoardWorkflowAsync(project.Key);
        var active = workflow.States.Single(x => x.Name == "Active");
        var review = workflow.States.Single(x => x.Name == "In Review");
        var closed = workflow.States.Single(x => x.Name == "Closed");
        var board = await ConfigureBoardAsync(item,
            new BoardColumnConfig("To do", [item.StateId], null, Guid.NewGuid()),
            new BoardColumnConfig("Doing", [closed.Id, review.Id, active.Id], null, Guid.NewGuid()));
        await SetBoardTransitionsAsync(project.Key, workflow, new WorkflowTransitionInput(item.StateId, active.Id), new WorkflowTransitionInput(item.StateId, review.Id));
        await using var client = await ConnectAsync();

        var result = await CallAsync(client, "move_item", new()
        {
            ["key"] = item.Key, ["version"] = item.Version, ["column"] = "Doing",
        });

        Assert.Equal(item.Key, Json(result).GetProperty("key").GetString());
        var moved = await GetItemAsync(item.Key);
        Assert.Equal(review.Id, moved.StateId);
        Assert.Equal(board.Columns[1].Id, moved.BoardColumnId);
    }

    [Fact]
    public async Task board_tools_explain_missing_team_and_board_without_creating_a_board()
    {
        var project = await CreateProjectAsync("NON");
        var epic = await CreateItemAsync(project, "No team", WorkItemType.Epic);
        var item = await CreateItemAsync(project, "No board");
        await ScalarAsync($"DELETE FROM work.boards WHERE team_id = '{item.TeamId}'");
        await using var client = await ConnectAsync();

        foreach (var target in new[] { epic, item })
        {
            var expected = target == epic ? "team" : "board";
            Assert.Contains(expected, Text(await CallAsync(client, "get_board", new() { ["key"] = target.Key })), StringComparison.OrdinalIgnoreCase);
            Assert.Contains(expected, Text(await CallAsync(client, "move_item", new()
            {
                ["key"] = target.Key, ["version"] = target.Version, ["column"] = "Blocked",
            })), StringComparison.OrdinalIgnoreCase);
            Assert.Equal(target.Version, (await GetItemAsync(target.Key)).Version);
        }
        Assert.Equal(0L, await ScalarAsync($"SELECT count(*) FROM work.boards WHERE team_id = '{item.TeamId}'"));
    }

    [Fact]
    public async Task move_item_refuses_unknown_columns_and_stale_versions_without_moving()
    {
        var project = await CreateProjectAsync("ERR");
        var item = await CreateItemAsync(project, "Do not move");
        await ConfigureBoardAsync(item, new BoardColumnConfig("Blocked", [item.StateId], null, Guid.NewGuid()));
        await using var client = await ConnectAsync();

        Assert.Contains("column", Text(await CallAsync(client, "move_item", new()
        {
            ["key"] = item.Key, ["version"] = item.Version, ["column"] = "Unknown column",
        })), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("version", Text(await CallAsync(client, "move_item", new()
        {
            ["key"] = item.Key, ["version"] = item.Version + 1, ["column"] = "Blocked",
        })), StringComparison.OrdinalIgnoreCase);
        var unchanged = await GetItemAsync(item.Key);
        Assert.Equal(item.Version, unchanged.Version);
        Assert.Equal(item.StateId, unchanged.StateId);
        Assert.Null(unchanged.BoardColumnId);
    }

    [Fact]
    public async Task move_item_refuses_a_column_with_no_allowed_transition()
    {
        var project = await CreateProjectAsync("TRN");
        var item = await CreateItemAsync(project, "Blocked transition");
        var workflow = await BoardWorkflowAsync(project.Key);
        var active = workflow.States.Single(x => x.Name == "Active");
        var closed = workflow.States.Single(x => x.Name == "Closed");
        await ConfigureBoardAsync(item,
            new BoardColumnConfig("To do", [item.StateId], null, Guid.NewGuid()),
            new BoardColumnConfig("Done", [closed.Id], null, Guid.NewGuid()));
        await SetBoardTransitionsAsync(project.Key, workflow, new WorkflowTransitionInput(item.StateId, active.Id));
        await using var client = await ConnectAsync();

        Assert.Contains("transition", Text(await CallAsync(client, "move_item", new()
        {
            ["key"] = item.Key, ["version"] = item.Version, ["column"] = "Done",
        })), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(item.Version, (await GetItemAsync(item.Key)).Version);
    }

    [Fact]
    public async Task mcp_and_rest_board_moves_share_wip_limits_and_rest_keeps_its_force_override()
    {
        var project = await CreateProjectAsync("WIP");
        var first = await CreateItemAsync(project, "First card");
        var second = await CreateItemAsync(project, "Second card");
        var third = await CreateItemAsync(project, "Third card");
        var workflow = await BoardWorkflowAsync(project.Key);
        var active = workflow.States.Single(x => x.Name == "Active");
        var board = await ConfigureBoardAsync(first,
            new BoardColumnConfig("To do", [first.StateId], null, Guid.NewGuid()),
            new BoardColumnConfig("Doing", [active.Id], 1, Guid.NewGuid()));
        await using var client = await ConnectAsync();

        Assert.Equal(first.Key, Json(await CallAsync(client, "move_item", new()
        {
            ["key"] = first.Key, ["version"] = first.Version, ["column"] = "Doing",
        })).GetProperty("key").GetString());
        Assert.Contains("WIP", Text(await CallAsync(client, "move_item", new()
        {
            ["key"] = second.Key, ["version"] = second.Version, ["column"] = "Doing",
        })), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(second.Version, (await GetItemAsync(second.Key)).Version);

        var rest = await _owner.PostAsJsonAsync($"/api/v1/orgs/mcp-org/items/{third.Key}/board-move",
            new BoardMoveRequest(active.Id, null, third.Version, ToColumnId: board.Columns[1].Id), ApiTestContext.Json, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, rest.StatusCode);
        var forced = await _owner.PostAsJsonAsync($"/api/v1/orgs/mcp-org/items/{third.Key}/board-move",
            new BoardMoveRequest(active.Id, first.Key, third.Version, Force: true, ToColumnId: board.Columns[1].Id), ApiTestContext.Json, TestContext.Current.CancellationToken);
        forced.EnsureSuccessStatusCode();
        var rendered = await ReadBoardAsync(first.TeamId!.Value);
        Assert.Equal([first.Key, third.Key], rendered.Columns[1].Cards.Select(x => x.Key));
        Assert.Equal(board.Columns[1].Id, (await GetItemAsync(third.Key)).BoardColumnId);
    }

    [Fact]
    public async Task board_tools_enforce_private_project_access_and_write_scope()
    {
        var project = await CreateProjectAsync("BPR", ProjectVisibility.Private);
        var item = await CreateItemAsync(project, "Private board item");
        await ConfigureBoardAsync(item, new BoardColumnConfig("Private column", [item.StateId], null, Guid.NewGuid()));
        await using var outsider = await ConnectAsync(await CreateTokenAsync(_colleague, [Scopes.Mcp, Scopes.Read, Scopes.Write]));

        foreach (var tool in new[] { "get_board", "move_item" })
        {
            var result = await CallAsync(outsider, tool, tool == "get_board"
                ? new() { ["key"] = item.Key }
                : new() { ["key"] = item.Key, ["version"] = item.Version, ["column"] = "Private column" });
            Assert.Contains("not found or no access", Text(result));
            Assert.DoesNotContain("Private column", Text(result));
        }

        await using var reader = await ConnectAsync(await CreateTokenAsync([Scopes.Mcp, Scopes.Read]));
        var denied = await CallAsync(reader, "move_item", new()
        {
            ["key"] = item.Key, ["version"] = item.Version, ["column"] = "Private column",
        });
        Assert.True(denied.IsError);
        Assert.Contains("requires the 'write'", Text(denied));
        Assert.Equal(item.Version, (await GetItemAsync(item.Key)).Version);
    }

    [Fact]
    public async Task move_item_refuses_archived_projects_while_get_board_remains_readable()
    {
        var project = await CreateProjectAsync("BAR");
        var item = await CreateItemAsync(project, "Frozen board item");
        await ConfigureBoardAsync(item, new BoardColumnConfig("Blocked", [item.StateId], null, Guid.NewGuid()));
        (await _owner.PostAsync($"/api/v1/orgs/mcp-org/projects/{project.Key}/archive", null, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        await using var client = await ConnectAsync();

        Assert.Contains("Blocked", Text(await CallAsync(client, "get_board", new() { ["key"] = item.Key })));
        Assert.Contains("archived", Text(await CallAsync(client, "move_item", new()
        {
            ["key"] = item.Key, ["version"] = item.Version, ["column"] = "Blocked",
        })), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(item.Version, (await GetItemAsync(item.Key)).Version);
    }

    private async Task<WorkflowView> BoardWorkflowAsync(string projectKey) =>
        Assert.Single((await _owner.GetFromJsonAsync<List<WorkflowView>>($"/api/v1/orgs/mcp-org/projects/{projectKey}/workflows/", ApiTestContext.Json, TestContext.Current.CancellationToken))!);

    private async Task<BoardView> ReadBoardAsync(Guid teamId) =>
        (await _owner.GetFromJsonAsync<BoardView>($"/api/v1/orgs/mcp-org/teams/{teamId}/board", ApiTestContext.Json, TestContext.Current.CancellationToken))!;

    private async Task<BoardView> ConfigureBoardAsync(WorkItemView item, params BoardColumnConfig[] columns)
    {
        var board = await ReadBoardAsync(item.TeamId!.Value);
        var response = await _owner.PutAsJsonAsync($"/api/v1/orgs/mcp-org/teams/{item.TeamId}/board",
            new UpdateBoardRequest(columns, null, null, null, board.Version), ApiTestContext.Json, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<BoardView>(ApiTestContext.Json, TestContext.Current.CancellationToken))!;
    }

    private async Task SetBoardTransitionsAsync(string projectKey, WorkflowView workflow, params WorkflowTransitionInput[] transitions)
    {
        var response = await _owner.PutAsJsonAsync($"/api/v1/orgs/mcp-org/projects/{projectKey}/workflows/{workflow.Id}",
            new ReplaceWorkflowRequest(null, workflow.States.Select(x => new WorkflowStateInput(x.Id, x.Name, x.Category, x.Position, x.Color, x.IsInitial, null)).ToList(), transitions, workflow.Version),
            ApiTestContext.Json, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
    }
}

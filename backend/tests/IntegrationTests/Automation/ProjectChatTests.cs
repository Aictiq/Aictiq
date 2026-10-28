using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.Automation.Prompts;
using Aictiq.Modules.Tenancy.Domain;
using Aictiq.Modules.Tenancy.Endpoints;
using Aictiq.Modules.WorkItems.Domain;
using Aictiq.Modules.WorkItems.Endpoints;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Paging;
using Aictiq.SharedKernel.Storage;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Aictiq.IntegrationTests.Automation;

/// <summary>
/// A person's private project chat: a question is a project-chat run with no item on the
/// project's runner, its answer lands in the chat, the chat keeps the last ten pairs, nobody
/// else can read it, and the run's token may create items in the project and write nothing else.
/// </summary>
[Trait("Category", "Automation")]
public sealed class ProjectChatTests(PostgresFixture postgres, GarageFixture garage) : RunTestBase(postgres, garage)
{
    protected override Action<IDictionary<string, string?>>? Configure => settings =>
    {
        settings["Automation:PollTimeoutSeconds"] = "2";
    };

    private string ChatUrl => $"{ProjectBase}/chat";

    private async Task<ChatView> AskAsync(HttpClient client, string question)
    {
        var response = await client.PostAsJsonAsync(ChatUrl, new { question }, ApiTestContext.Json, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<ChatView>(ApiTestContext.Json, Ct))!;
    }

    private async Task<ChatView> ChatAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<ChatView>(ChatUrl, ApiTestContext.Json, Ct))!;

    private async Task FinishAsync(HttpClient runner, Guid runId, RunnerFinishRequest request)
    {
        var response = await runner.PostAsJsonAsync($"/api/v1/runner/runs/{runId}/finish", request, ApiTestContext.Json, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    private async Task<RunnerRunClaimed> StartAsync(HttpClient runner, Guid runId)
    {
        var claimed = await ClaimAsync(runner);
        Assert.Equal(runId, claimed.RunId);
        Assert.Equal(HttpStatusCode.NoContent, (await StartedAsync(runner, runId)).StatusCode);
        return claimed;
    }

    private async Task<McpClient> ConnectAsync(string token)
    {
        var http = Context.Factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, "/mcp") },
            http, loggerFactory: null, ownsHttpClient: true);
        return await McpClient.CreateAsync(transport, cancellationToken: Ct);
    }

    private static string Text(CallToolResult result) =>
        string.Join("\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));

    private async Task<string> CreatedKeyAsync(McpClient mcp, string tool, Dictionary<string, object?> arguments)
    {
        var result = await mcp.CallToolAsync(tool, arguments, cancellationToken: Ct);
        Assert.True(result.IsError != true, Text(result));
        var key = Regex.Match(Text(result), $"{Project.Key}-\\d+", RegexOptions.IgnoreCase).Value;
        Assert.NotEmpty(key);
        return key;
    }

    private async Task<HttpClient> MemberAsync(bool canOperateFactory)
    {
        var auth = await Context.RegisterAsync($"chat-member-{Guid.NewGuid():N}@test.local");
        await AddMemberAsync(auth.User.Id, OrgRole.Member, canOperateFactory);
        return Context.ClientFor(auth);
    }

    [Fact]
    public async Task a_question_runs_with_no_item_and_its_answer_lands_in_the_chat()
    {
        var asked = await AskAsync(Owner, "Where is the login redirect handled?");
        var pending = Assert.Single(asked.Messages);
        Assert.Equal("pending", pending.Status);
        Assert.Equal("queued", pending.RunStatus);
        Assert.Null(pending.Answer);
        // Registered, but no runner has said hello yet.
        Assert.False(asked.RunnerOnline);
        var runId = pending.RunId!.Value;

        // Factory → Runs: a chat run with no item, whose detail opens.
        var listed = Assert.Single((await Owner.GetFromJsonAsync<PagedResult<RunView>>(
            $"/api/v1/orgs/{Slug}/runs?project={Project.Key}", ApiTestContext.Json, Ct))!.Items, run => run.Id == runId);
        Assert.Equal("projectChat", listed.Kind);
        Assert.Null(listed.ItemKey);
        Assert.Null(listed.ItemId);
        Assert.Equal("Project chat", listed.PlaybookName);
        var detail = await RunAsync(runId);
        Assert.Equal(OwnerId, detail.RequestedBy);
        Assert.Contains("> Where is the login redirect handled?", detail.PromptSnapshot);
        Assert.Contains("# Playbook: Project chat", detail.PromptSnapshot);
        Assert.Contains("Never reveal secrets", detail.PromptSnapshot);
        Assert.Contains("(This is the first question of the chat.)", detail.PromptSnapshot);

        // A second question while one is in flight is refused.
        var second = await Owner.PostAsJsonAsync(ChatUrl, new { question = "And the logout?" }, ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(ProblemTypes.Conflict, await ProblemTypeAsync(second));

        using var runner = RunnerClient(Runner.Secret);
        var claimed = await StartAsync(runner, runId);
        Assert.Null(claimed.ItemId);
        Assert.Null(claimed.ItemKey);
        Assert.Equal(Project.Key, claimed.ProjectKey);
        Assert.True(claimed.WorkOnDefaultBranch);
        Assert.Equal("main", claimed.BranchName);
        Assert.StartsWith(SecurityPreamble.Text + "\n\n", claimed.Prompt);
        Assert.Equal("running", Assert.Single((await ChatAsync(Owner)).Messages).RunStatus);

        await FinishAsync(runner, runId, new RunnerFinishRequest(
            "succeeded", 0, "In `LoginRedirect.cs:12`.", null, null, null, null, null));
        var answered = Assert.Single((await ChatAsync(Owner)).Messages);
        Assert.Equal("answered", answered.Status);
        Assert.Equal("In `LoginRedirect.cs:12`.", answered.Answer);
        Assert.NotNull(answered.AnsweredAt);
        // No item to tell: nothing is staged for WorkItems or Notifications.
        Assert.Equal(0, await ScalarAsync($"""
            SELECT count(*) FROM shared.outbox_messages
            WHERE type = '{typeof(RunFinished).FullName}' AND payload::jsonb ->> 'RunId' = '{runId}'
            """));

        // The follow-up carries the saved pair as context.
        var followUp = await AskAsync(Owner, "And what calls it?");
        Assert.Equal(2, followUp.Messages.Count);
        var followUpRun = await RunAsync(followUp.Messages[^1].RunId!.Value);
        Assert.Contains("> Where is the login redirect handled?", followUpRun.PromptSnapshot);
        Assert.Contains("> In `LoginRedirect.cs:12`.", followUpRun.PromptSnapshot);
        Assert.Contains("> And what calls it?", followUpRun.PromptSnapshot);

        // A project chat is asked again, not continued.
        Assert.Equal(HttpStatusCode.Conflict,
            (await Owner.PostAsync($"/api/v1/orgs/{Slug}/runs/{runId}/continue", null, Ct)).StatusCode);
    }

    [Fact]
    public async Task the_chat_keeps_the_last_ten_pairs_and_clear_history_empties_it_and_cancels_the_question()
    {
        for (var i = 1; i <= 10; i++)
        {
            await ExecuteSqlAsync($"""
                INSERT INTO automation.project_chat_messages
                    (id, organization_id, project_id, user_id, question, answer, status, asked_at, answered_at)
                VALUES (gen_random_uuid(), '{OrganizationId}', '{Project.Id}', '{OwnerId}', 'Question {i}', 'Answer {i}', 1,
                        now() - interval '{20 - i} minutes', now() - interval '{20 - i} minutes')
                """);
        }

        var eleventh = await AskAsync(Owner, "Question 11");
        Assert.Equal(10, eleventh.Messages.Count);
        Assert.Equal("Question 2", eleventh.Messages[0].Question);
        Assert.Equal("Question 11", eleventh.Messages[^1].Question);
        Assert.Equal(10, await ScalarAsync($"SELECT count(*) FROM automation.project_chat_messages WHERE user_id = '{OwnerId}'"));
        var runId = eleventh.Messages[^1].RunId!.Value;
        var prompt = (await RunAsync(runId)).PromptSnapshot!;
        Assert.DoesNotContain("> Question 1\n", prompt);
        Assert.Contains("> Answer 2", prompt);

        Assert.Equal(HttpStatusCode.NoContent, (await Owner.DeleteAsync(ChatUrl, Ct)).StatusCode);
        Assert.Empty((await ChatAsync(Owner)).Messages);
        Assert.Equal("cancelled", (await RunAsync(runId)).Status);
        // With nothing in flight any more, the next question goes.
        Assert.Single((await AskAsync(Owner, "A fresh start")).Messages);
    }

    [Fact]
    public async Task nobody_else_reads_my_chat_and_stakeholders_get_no_chat()
    {
        var mine = await AskAsync(Owner, "What does the billing job do?");
        var runId = mine.Messages[0].RunId!.Value;
        using var runner = RunnerClient(Runner.Secret);
        await StartAsync(runner, runId);
        await FinishAsync(runner, runId, new RunnerFinishRequest("succeeded", 0, "It renews plans.", null, null, null, null, null));

        // Another operator of the same project sees their own, empty chat - and my run without
        // its question, answer or log.
        using var colleague = await MemberAsync(canOperateFactory: true);
        Assert.Empty((await ChatAsync(colleague)).Messages);
        var seen = (await colleague.GetFromJsonAsync<RunView>($"/api/v1/orgs/{Slug}/runs/{runId}", ApiTestContext.Json, Ct))!;
        Assert.Null(seen.PromptSnapshot);
        Assert.Null(seen.OutcomeSummary);
        Assert.Equal(HttpStatusCode.NotFound, (await colleague.GetAsync($"/api/v1/orgs/{Slug}/runs/{runId}/log", Ct)).StatusCode);
        var listed = Assert.Single((await colleague.GetFromJsonAsync<PagedResult<RunView>>(
            $"/api/v1/orgs/{Slug}/runs?project={Project.Key}", ApiTestContext.Json, Ct))!.Items, run => run.Id == runId);
        Assert.Null(listed.OutcomeSummary);
        // Clearing their chat leaves mine alone.
        Assert.Equal(HttpStatusCode.NoContent, (await colleague.DeleteAsync(ChatUrl, Ct)).StatusCode);
        Assert.Single((await ChatAsync(Owner)).Messages);
        // I still see my own run in full.
        Assert.Equal("It renews plans.", (await RunAsync(runId)).OutcomeSummary);

        using var stakeholder = await MemberAsync(canOperateFactory: false);
        Assert.Equal(HttpStatusCode.Forbidden, (await stakeholder.GetAsync(ChatUrl, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await stakeholder.PostAsJsonAsync(ChatUrl, new { question = "Hi" }, ApiTestContext.Json, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await stakeholder.DeleteAsync(ChatUrl, Ct)).StatusCode);
    }

    [Fact]
    public async Task the_run_token_creates_items_in_its_project_as_the_asker_and_writes_nothing_else()
    {
        var other = await CreateProjectAsync("Ops", "ops", ProjectVisibility.Organization);
        var teamResponse = await Owner.PostAsJsonAsync($"{ProjectBase}/teams/",
            new CreateTeamRequest("Platform", "PLAT"), ApiTestContext.Json, Ct);
        teamResponse.EnsureSuccessStatusCode();
        var team = (await teamResponse.Content.ReadFromJsonAsync<TeamView>(ApiTestContext.Json, Ct))!;
        var asked = await AskAsync(Owner, "Make tickets for the two redirect bugs");
        var runId = asked.Messages[0].RunId!.Value;
        using var runner = RunnerClient(Runner.Secret);
        var claimed = await StartAsync(runner, runId);

        await using (var mcp = await ConnectAsync(claimed.AgentToken))
        {
            var created = await mcp.CallToolAsync("create_item", new Dictionary<string, object?>
            {
                ["project"] = Project.Key, ["title"] = "Redirect drops the query string", ["description"] = "Seen on /login?next=", ["team"] = team.Key,
            }, cancellationToken: Ct);
            Assert.True(created.IsError != true, Text(created));
            var key = Regex.Match(Text(created), $"{Project.Key}-\\d+", RegexOptions.IgnoreCase).Value;
            var item = await ItemAsync(key);
            Assert.Equal("Redirect drops the query string", item.Title);
            Assert.Equal("Seen on /login?next=", item.DescriptionMarkdown);
            Assert.Equal(team.Id, item.TeamId);
            // Reported by the person who asked, not the agent.
            Assert.Equal(1, await ScalarAsync($"""
                SELECT count(*) FROM work.items WHERE id = '{item.Id}' AND created_by = '{OwnerId}'
                """));

            var elsewhere = await mcp.CallToolAsync("create_item", new Dictionary<string, object?>
            {
                ["project"] = other.Key, ["title"] = "Not here", ["team"] = team.Id.ToString(),
            }, cancellationToken: Ct);
            Assert.Contains("not found or no access", Text(elsewhere), StringComparison.Ordinal);

            var edit = await mcp.CallToolAsync("update_item", new Dictionary<string, object?>
            {
                ["key"] = ItemKey, ["version"] = Item.Version, ["title"] = "Hijacked",
            }, cancellationToken: Ct);
            Assert.True(edit.IsError);
            Assert.Contains("requires the 'write'", Text(edit), StringComparison.Ordinal);

            var comment = await mcp.CallToolAsync("add_comment", new Dictionary<string, object?>
            {
                ["key"] = ItemKey, ["bodyMarkdown"] = "Hello",
            }, cancellationToken: Ct);
            Assert.True(comment.IsError);
        }

        // REST writes are refused by scope too.
        using var agent = TokenClient(claimed.AgentToken);
        var rest = await agent.PostAsJsonAsync($"{ProjectBase}/items/",
            new CreateWorkItemRequest(WorkItemType.Bug, "Via REST", null, null, null, null, null, null, null, null, null, null, null, null),
            ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, rest.StatusCode);
        Assert.Equal("Fix the login redirect", (await ItemAsync(ItemKey)).Title);

        await FinishAsync(runner, runId, new RunnerFinishRequest("succeeded", 0, "Created the tickets.", null, null, null, null, null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task chat_creates_one_parent_with_three_tasks_and_preserves_reporter_and_team_defaults(bool namedTeam)
    {
        Guid? teamId = null;
        string? teamKey = null;
        if (namedTeam)
        {
            var response = await Owner.PostAsJsonAsync($"{ProjectBase}/teams/",
                new CreateTeamRequest("Platform", "PLAT"), ApiTestContext.Json, Ct);
            response.EnsureSuccessStatusCode();
            var team = (await response.Content.ReadFromJsonAsync<TeamView>(ApiTestContext.Json, Ct))!;
            teamId = team.Id;
            teamKey = team.Key;
        }

        var before = await ScalarAsync($"SELECT count(*) FROM work.items WHERE project_id = '{Project.Id}'");
        var asked = await AskAsync(Owner, "Create one redirect ticket with three subtasks: preserve query, validate target, cover logout.");
        var runId = asked.Messages[0].RunId!.Value;
        using var runner = RunnerClient(Runner.Secret);
        var claimed = await StartAsync(runner, runId);
        // The runner receives hierarchy and partial-failure instructions, not one create_item per ticket.
        Assert.Contains("create_subtask", claimed.Prompt);
        Assert.Contains("parent's returned real key as parentKey", claimed.Prompt);
        Assert.Contains("Never replace requested subtasks with standalone tickets", claimed.Prompt);
        Assert.Contains("If any creation fails", claimed.Prompt);
        Assert.Contains($"/o/{Slug}/p/{Project.Key}/items/{Project.Key}-123", claimed.Prompt);

        await using var mcp = await ConnectAsync(claimed.AgentToken);
        var parentKey = await CreatedKeyAsync(mcp, "create_item", new()
        {
            ["project"] = Project.Key, ["title"] = "Fix redirects", ["description"] = "Agreed redirect breakdown", ["team"] = teamKey,
        });
        var parent = await ItemAsync(parentKey);
        Assert.Equal(WorkItemType.Bug, parent.Type);
        Assert.Null(parent.ParentId);
        Assert.Equal("Agreed redirect breakdown", parent.DescriptionMarkdown);
        Assert.Equal(teamId ?? Item.TeamId, parent.TeamId);

        string[] titles = ["Preserve query", "Validate target", "Cover logout"];
        var keys = new List<string>();
        foreach (var title in titles)
        {
            var key = await CreatedKeyAsync(mcp, "create_subtask", new() { ["parentKey"] = parentKey, ["title"] = title });
            keys.Add(key);
            var child = await ItemAsync(key);
            Assert.Equal(title, child.Title);
            Assert.Equal(WorkItemType.Task, child.Type);
            Assert.Equal(parent.Id, child.ParentId);
            Assert.Equal(parent.TeamId, child.TeamId);
            var ancestors = await Owner.GetFromJsonAsync<List<WorkItemView>>($"/api/v1/orgs/{Slug}/items/{key}/ancestors", ApiTestContext.Json, Ct);
            Assert.Equal(parentKey, Assert.Single(ancestors!).Key);
        }
        var children = await Owner.GetFromJsonAsync<List<WorkItemView>>($"/api/v1/orgs/{Slug}/items/{parentKey}/children", ApiTestContext.Json, Ct);
        Assert.Equal(keys, children!.Select(child => child.Key));
        Assert.Equal(before + 4, await ScalarAsync($"SELECT count(*) FROM work.items WHERE project_id = '{Project.Id}'"));
        Assert.Equal(4, await ScalarAsync($"""
            SELECT count(*) FROM work.items WHERE project_id = '{Project.Id}' AND created_by = '{OwnerId}'
                AND (id = '{parent.Id}' OR parent_id = '{parent.Id}')
            """));

        // The creation exception confers no edit permission, even on items this chat created.
        foreach (var key in new[] { parentKey, keys[0] })
        {
            var item = await ItemAsync(key);
            var edit = await mcp.CallToolAsync("update_item", new Dictionary<string, object?>
            {
                ["key"] = key, ["version"] = item.Version, ["title"] = "Hijacked",
            }, cancellationToken: Ct);
            Assert.True(edit.IsError);
            Assert.Contains("requires the 'write'", Text(edit));
            Assert.Equal(item.Title, (await ItemAsync(key)).Title);
        }

        var answer = $"Parent: [{parentKey}](/o/{Slug}/p/{Project.Key}/items/{parentKey})\nSubtasks:\n"
            + string.Join("\n", keys.Select(key => $"- [{key}](/o/{Slug}/p/{Project.Key}/items/{key})"));
        await FinishAsync(runner, runId, new RunnerFinishRequest("succeeded", 0, answer, null, null, null, null, null));
        Assert.Equal(answer, Assert.Single((await ChatAsync(Owner)).Messages).Answer);
    }

    [Fact]
    public async Task chat_creates_every_requested_type_with_its_description_parent_and_requester_as_reporter()
    {
        var auth = await Context.RegisterAsync($"typed-chat-requester-{Guid.NewGuid():N}@test.local");
        await AddMemberAsync(auth.User.Id, OrgRole.Member, canOperateFactory: true);
        using var requester = Context.ClientFor(auth);
        var before = await ScalarAsync($"SELECT count(*) FROM work.items WHERE project_id = '{Project.Id}'");
        var asked = await AskAsync(requester,
            "Create an Epic for redirects, a Feature under that Epic, a Story under that Feature, a Task under that Story, and a Bug under that Story. Use the descriptions we agreed.");
        var runId = asked.Messages[0].RunId!.Value;
        using var runner = RunnerClient(Runner.Secret);
        var claimed = await StartAsync(runner, runId);
        Assert.Contains("Supported types are Epic, Feature, Story, Task and Bug", claimed.Prompt);
        Assert.Contains("Preserve the requested type", claimed.Prompt);
        Assert.Contains("description that carries what the conversation established", claimed.Prompt);
        Assert.Contains("Confirm each created ticket's type and link its returned real key", claimed.Prompt);
        Assert.Contains("making parent relationships clear", claimed.Prompt);

        await using var mcp = await ConnectAsync(claimed.AgentToken);
        var created = new Dictionary<WorkItemType, WorkItemView>();
        foreach (var (type, parentType) in new (WorkItemType Type, WorkItemType? Parent)[]
        {
            (WorkItemType.Epic, null),
            (WorkItemType.Feature, WorkItemType.Epic),
            (WorkItemType.Story, WorkItemType.Feature),
            (WorkItemType.Task, WorkItemType.Story),
            (WorkItemType.Bug, WorkItemType.Story),
        })
        {
            var parent = parentType is { } parentKind ? created[parentKind] : null;
            var description = $"Agreed **{type}** redirect requirements.";
            var result = await mcp.CallToolAsync("create_item", new Dictionary<string, object?>
            {
                ["project"] = Project.Key, ["type"] = type.ToString(), ["title"] = $"Redirect {type}",
                ["description"] = description, ["parentKey"] = parent?.Key,
            }, cancellationToken: Ct);
            Assert.True(result.IsError != true, Text(result));
            using var document = JsonDocument.Parse(Text(result));
            var payload = document.RootElement;
            var item = await ItemAsync(payload.GetProperty("key").GetString()!);
            Assert.Equal(type.ToString(), payload.GetProperty("type").GetString());
            Assert.Equal(parent?.Key, payload.GetProperty("parentKey").GetString());
            Assert.Equal(type, item.Type);
            Assert.Equal($"Redirect {type}", item.Title);
            Assert.Equal(description, item.DescriptionMarkdown);
            Assert.Contains($"<strong>{type}</strong>", item.DescriptionHtml);
            Assert.Equal(parent?.Id, item.ParentId);
            Assert.Equal(1, await ScalarAsync($"SELECT count(*) FROM work.items WHERE id = '{item.Id}' AND created_by = '{auth.User.Id}'"));
            created.Add(type, item);
        }
        Assert.Equal(before + 5, await ScalarAsync($"SELECT count(*) FROM work.items WHERE project_id = '{Project.Id}'"));
    }

    [Fact]
    public async Task chat_requires_an_agreed_parent_for_features_and_tasks_and_creates_no_replacements()
    {
        var asked = await AskAsync(Owner, "Create a Feature and a Task for redirects.");
        var runId = asked.Messages[0].RunId!.Value;
        using var runner = RunnerClient(Runner.Secret);
        var claimed = await StartAsync(runner, runId);
        Assert.Contains("Features require an Epic", claimed.Prompt);
        Assert.Contains("Tasks require a Story or Bug", claimed.Prompt);
        Assert.Contains("Ask for missing or ambiguous required parent information before creating the item", claimed.Prompt);
        Assert.Contains("without creating a different type or a standalone replacement", claimed.Prompt);
        var before = await ScalarAsync("SELECT count(*) FROM work.items");

        await using var mcp = await ConnectAsync(claimed.AgentToken);
        foreach (var type in new[] { "Feature", "Task" })
        {
            var refused = await mcp.CallToolAsync("create_item", new Dictionary<string, object?>
            {
                ["project"] = Project.Key, ["title"] = $"Redirect {type}", ["type"] = type,
            }, cancellationToken: Ct);
            Assert.Contains("parent", Text(refused), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\"key\":", Text(refused));
        }
        Assert.Equal(before, await ScalarAsync("SELECT count(*) FROM work.items"));
    }

    [Fact]
    public async Task a_failed_subtask_leaves_the_created_hierarchy_intact_without_standalone_replacements()
    {
        var asked = await AskAsync(Owner, "Create a redirect ticket with subtasks");
        var runId = asked.Messages[0].RunId!.Value;
        using var runner = RunnerClient(Runner.Secret);
        var claimed = await StartAsync(runner, runId);
        await using var mcp = await ConnectAsync(claimed.AgentToken);
        var parentKey = await CreatedKeyAsync(mcp, "create_item", new() { ["project"] = Project.Key, ["title"] = "Fix redirects" });
        var childKey = await CreatedKeyAsync(mcp, "create_subtask", new() { ["parentKey"] = parentKey, ["title"] = "Preserve query" });
        var parent = await ItemAsync(parentKey);
        var before = await ScalarAsync($"SELECT count(*) FROM work.items WHERE project_id = '{Project.Id}'");

        var failed = await mcp.CallToolAsync("create_subtask", new Dictionary<string, object?>
        {
            ["parentKey"] = parentKey, ["title"] = "Validate target", ["team"] = "MISSING",
        }, cancellationToken: Ct);
        Assert.Contains("not found in project", Text(failed));
        Assert.Equal(before, await ScalarAsync($"SELECT count(*) FROM work.items WHERE project_id = '{Project.Id}'"));
        var children = await Owner.GetFromJsonAsync<List<WorkItemView>>($"/api/v1/orgs/{Slug}/items/{parentKey}/children", ApiTestContext.Json, Ct);
        Assert.Equal(childKey, Assert.Single(children!).Key);
        Assert.Equal(parent.Id, (await ItemAsync(childKey)).ParentId);

        var answer = $"Created parent [{parentKey}](/o/{Slug}/p/{Project.Key}/items/{parentKey}) and subtask [{childKey}](/o/{Slug}/p/{Project.Key}/items/{childKey}). Validate target failed: team MISSING was not found.";
        await FinishAsync(runner, runId, new RunnerFinishRequest("succeeded", 0, answer, null, null, null, null, null));
        Assert.Equal(answer, Assert.Single((await ChatAsync(Owner)).Messages).Answer);
    }

    [Fact]
    public async Task chat_subtasks_refuse_foreign_or_missing_parents_foreign_teams_and_archived_projects()
    {
        var other = await CreateProjectAsync("Ops", "ops", ProjectVisibility.Organization);
        var otherParent = await CreateItemAsync("Outside chat", other.Key);
        var asked = await AskAsync(Owner, "Create subtasks");
        var runId = asked.Messages[0].RunId!.Value;
        using var runner = RunnerClient(Runner.Secret);
        var claimed = await StartAsync(runner, runId);
        await using var mcp = await ConnectAsync(claimed.AgentToken);
        // The agent can read the foreign parent; the chat grant must still refuse creation there.
        var visible = await mcp.CallToolAsync("get_item", new Dictionary<string, object?> { ["key"] = otherParent.Key }, cancellationToken: Ct);
        Assert.Contains("Outside chat", Text(visible));
        var before = await ScalarAsync("SELECT count(*) FROM work.items");
        foreach (var parentKey in new[] { otherParent.Key, $"{Project.Key}-99999" })
        {
            var refused = await mcp.CallToolAsync("create_subtask", new Dictionary<string, object?>
            {
                ["parentKey"] = parentKey, ["title"] = "Refused",
            }, cancellationToken: Ct);
            Assert.Contains("not found or no access", Text(refused));
        }
        // Explicit type and parentKey must not let a chat redirect creation to a
        // readable project, whether the project or the parent names it.
        foreach (var (projectKey, parentKey) in new[]
        {
            (Project.Key, otherParent.Key), (other.Key, ItemKey), (other.Key, otherParent.Key),
        })
        {
            var refused = await mcp.CallToolAsync("create_item", new Dictionary<string, object?>
            {
                ["project"] = projectKey, ["parentKey"] = parentKey, ["title"] = "Refused typed task", ["type"] = "Task",
            }, cancellationToken: Ct);
            Assert.Contains(projectKey == Project.Key ? "same project" : "not found or no access", Text(refused));
        }
        var foreignTeam = await mcp.CallToolAsync("create_subtask", new Dictionary<string, object?>
        {
            ["parentKey"] = ItemKey, ["title"] = "Refused", ["team"] = otherParent.TeamId!.Value.ToString(),
        }, cancellationToken: Ct);
        Assert.Contains("not found in project", Text(foreignTeam));

        (await Owner.PostAsync($"{ProjectBase}/archive", null, Ct)).EnsureSuccessStatusCode();
        var archived = await mcp.CallToolAsync("create_subtask", new Dictionary<string, object?>
        {
            ["parentKey"] = ItemKey, ["title"] = "Refused",
        }, cancellationToken: Ct);
        Assert.Contains("archived", Text(archived));
        Assert.Equal(before, await ScalarAsync("SELECT count(*) FROM work.items"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task chat_subtasks_refuse_creation_after_the_requester_loses_creation_access(bool removeMembership)
    {
        var auth = await Context.RegisterAsync($"chat-requester-{Guid.NewGuid():N}@test.local");
        await AddMemberAsync(auth.User.Id, OrgRole.Member, canOperateFactory: true);
        using var requester = Context.ClientFor(auth);
        var asked = await AskAsync(requester, "Create a ticket with subtasks");
        var runId = asked.Messages[0].RunId!.Value;
        using var runner = RunnerClient(Runner.Secret);
        var claimed = await StartAsync(runner, runId);
        await using var mcp = await ConnectAsync(claimed.AgentToken);
        var parentKey = await CreatedKeyAsync(mcp, "create_item", new() { ["project"] = Project.Key, ["title"] = "Requested parent" });
        var parent = await ItemAsync(parentKey);
        Assert.Equal(1, await ScalarAsync($"SELECT count(*) FROM work.items WHERE id = '{parent.Id}' AND created_by = '{auth.User.Id}'"));
        // Keep the agent's access while removing or demoting the person who requested this run.
        var membershipUrl = $"/api/v1/orgs/{Slug}/members/{auth.User.Id}";
        var changed = removeMembership
            ? await Owner.DeleteAsync(membershipUrl, Ct)
            : await Owner.PutAsJsonAsync(membershipUrl, new UpdateMemberRequest(OrgRole.Guest), ApiTestContext.Json, Ct);
        changed.EnsureSuccessStatusCode();
        var before = await ScalarAsync("SELECT count(*) FROM work.items");
        var refused = await mcp.CallToolAsync("create_subtask", new Dictionary<string, object?>
        {
            ["parentKey"] = parentKey, ["title"] = "Refused",
        }, cancellationToken: Ct);
        Assert.Contains("not found or no access", Text(refused));
        var typedRefused = await mcp.CallToolAsync("create_item", new Dictionary<string, object?>
        {
            ["project"] = Project.Key, ["parentKey"] = parentKey, ["title"] = "Refused typed task", ["type"] = "Task",
        }, cancellationToken: Ct);
        Assert.Contains("not found or no access", Text(typedRefused));
        Assert.Equal(before, await ScalarAsync("SELECT count(*) FROM work.items"));
    }

    [Fact]
    public async Task failed_and_timed_out_questions_say_so_in_the_chat()
    {
        var asked = await AskAsync(Owner, "Explain the sweeper");
        var runId = asked.Messages[0].RunId!.Value;
        using var runner = RunnerClient(Runner.Secret);
        await StartAsync(runner, runId);
        await FinishAsync(runner, runId, new RunnerFinishRequest("failed", 1, null, null, null, null, null, "harness-crashed"));

        var failed = Assert.Single((await ChatAsync(Owner)).Messages);
        Assert.Equal("failed", failed.Status);
        Assert.Null(failed.Answer);
        Assert.Contains("harness-crashed", failed.Error);
        // A failed chat run is not continued automatically.
        Assert.Equal(0, await ScalarAsync($"SELECT count(*) FROM automation.runs WHERE id = '{runId}' AND auto_continue_due_at IS NOT NULL"));

        // A succeeded run that said nothing is not an answer either.
        var silent = await AskAsync(Owner, "Anything?");
        var silentRun = silent.Messages[^1].RunId!.Value;
        await StartAsync(runner, silentRun);
        await FinishAsync(runner, silentRun, new RunnerFinishRequest("succeeded", 0, null, null, null, null, null, null));
        Assert.Equal("failed", (await ChatAsync(Owner)).Messages[^1].Status);
    }

    [Fact]
    public async Task a_project_with_multiple_agents_and_no_default_explains_itself()
    {
        await ExecuteSqlAsync($"UPDATE automation.project_settings SET default_agent_id = NULL WHERE project_id = '{Project.Id}'");
        await CreateAgentAsync("another-agent");
        var response = await Owner.PostAsJsonAsync(ChatUrl, new { question = "Hello?" }, ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Multiple active agents", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Empty((await ChatAsync(Owner)).Messages);

        var blank = await Owner.PostAsJsonAsync(ChatUrl, new { question = "   " }, ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task project_chat_inherits_the_organization_default_or_the_only_eligible_agent(bool organizationDefault)
    {
        await ExecuteSqlAsync($"UPDATE automation.project_settings SET default_agent_id = NULL WHERE project_id = '{Project.Id}'");
        var expectedAgent = AgentId;
        if (organizationDefault)
        {
            var preferred = await CreateAgentAsync("preferred-chat-agent");
            expectedAgent = preferred.UserId;
            (await Owner.PatchAsJsonAsync($"/api/v1/orgs/{Slug}/agents/{expectedAgent}",
                new UpdateAgentRequest(null, null, true), ApiTestContext.Json, Ct)).EnsureSuccessStatusCode();
        }
        var chat = await AskAsync(Owner, "Explain this project");
        var run = await RunAsync(Assert.Single(chat.Messages).RunId!.Value);
        Assert.Equal(expectedAgent, run.AgentId);
    }

    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    private static readonly byte[] Pdf = "%PDF-1.4\n% codename BLUEBIRD\n%%EOF\n"u8.ToArray();

    private static byte[] Zip(params (string Name, string Text)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in entries)
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                writer.Write(text);
            }
        }
        return buffer.ToArray();
    }

    private async Task<HttpResponseMessage> UploadAsync(HttpClient client, string name, string type, byte[] bytes, bool forChat = true)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(type);
        form.Add(file, "file", name);
        return await client.PostAsync($"{ProjectBase}/attachments{(forChat ? "?purpose=chat" : "")}", form, Ct);
    }

    private async Task<Guid> UploadOkAsync(HttpClient client, string name, string type, byte[] bytes, bool forChat = true)
    {
        var response = await UploadAsync(client, name, type, bytes, forChat);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<AttachmentView>(ApiTestContext.Json, Ct))!.Id;
    }

    private async Task<ChatView> AskWithFilesAsync(HttpClient client, string question, params Guid[] attachmentIds)
    {
        var response = await client.PostAsJsonAsync(ChatUrl, new { question, attachmentIds }, ApiTestContext.Json, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<ChatView>(ApiTestContext.Json, Ct))!;
    }

    private async Task<string> ScalarTextAsync(string sql)
    {
        await using var connection = new Npgsql.NpgsqlConnection(Context.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new Npgsql.NpgsqlCommand(sql, connection);
        return (string)(await command.ExecuteScalarAsync(Ct))!;
    }

    private static string Download(Guid attachmentId) => $"/api/v1/orgs/{Slug}/attachments/{attachmentId}/download";

    [Fact]
    public async Task files_go_with_a_question_and_only_the_asker_and_the_run_answering_them_read_them()
    {
        var png = await UploadOkAsync(Owner, "screen.png", "image/png", Png);
        var pdf = await UploadOkAsync(Owner, "brief.pdf", "application/pdf", Pdf);
        var zip = await UploadOkAsync(Owner, "spec.zip", "application/zip", Zip(("docs/codename.txt", "BLUEBIRD")));

        var asked = await AskWithFilesAsync(Owner, "What is the codename?", pdf, zip, png);
        var message = Assert.Single(asked.Messages);
        Assert.Equal(["brief.pdf", "spec.zip", "screen.webp"], message.Attachments.Select(file => file.FileName));
        Assert.Equal("image/webp", message.Attachments[2].ContentType);
        // Reopening the chat shows them again.
        Assert.Equal(3, Assert.Single((await ChatAsync(Owner)).Messages).Attachments.Count);

        var runId = message.RunId!.Value;
        var prompt = (await RunAsync(runId)).PromptSnapshot!;
        Assert.Contains("Files attached to this question:", prompt);
        Assert.Contains("- `spec.zip` (application/zip, ", prompt);
        Assert.Contains($"attachment id {zip}", prompt);
        Assert.Contains("Never imply that you considered content you could not read.", prompt);

        // Private: the asker downloads them, another operator of the project cannot read or delete them.
        Assert.Equal(HttpStatusCode.OK, (await Owner.GetAsync(Download(pdf), Ct)).StatusCode);
        using var colleague = await MemberAsync(canOperateFactory: true);
        Assert.Equal(HttpStatusCode.NotFound, (await colleague.GetAsync(Download(pdf), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await colleague.DeleteAsync($"/api/v1/orgs/{Slug}/attachments/{pdf}", Ct)).StatusCode);
        Assert.Empty((await colleague.GetFromJsonAsync<List<ChatFileView>>($"{ChatUrl}/attachments", ApiTestContext.Json, Ct))!);
        Assert.Equal(0, await ScalarAsync($"SELECT count(*) FROM work.attachments WHERE id = '{pdf}' AND item_id IS NOT NULL"));

        // The run answering the question lists and reads them with its own token.
        using var runner = RunnerClient(Runner.Secret);
        var claimed = await StartAsync(runner, runId);
        using var agent = TokenClient(claimed.AgentToken);
        var listed = (await agent.GetFromJsonAsync<List<ChatFileView>>($"{ChatUrl}/attachments", ApiTestContext.Json, Ct))!;
        Assert.Equal([pdf, zip, png], listed.Select(file => file.Id));
        Assert.All(listed, file => Assert.Equal(message.Id, file.ChatMessageId));
        var zipped = await agent.GetByteArrayAsync(Download(zip), Ct);
        using (var archive = new ZipArchive(new MemoryStream(zipped)))
        {
            Assert.Equal("docs/codename.txt", Assert.Single(archive.Entries).FullName);
        }
        await using (var mcp = await ConnectAsync(claimed.AgentToken))
        {
            var image = await mcp.CallToolAsync("get_attachment", new Dictionary<string, object?> { ["id"] = png }, cancellationToken: Ct);
            Assert.True(image.IsError != true, Text(image));
            Assert.Single(image.Content.OfType<ImageContentBlock>());
        }
        // Another project is not the run's: no listing there.
        var other = await CreateProjectAsync("Ops", "ops", ProjectVisibility.Organization);
        Assert.False((await agent.GetAsync($"/api/v1/orgs/{Slug}/projects/{other.Key}/chat/attachments", Ct)).IsSuccessStatusCode);

        await FinishAsync(runner, runId, new RunnerFinishRequest("succeeded", 0, "BLUEBIRD.", null, null, null, null, null));

        // The follow-up names the earlier files as part of the conversation, and its run gets them.
        var followUp = await AskAsync(Owner, "And in the PDF?");
        var followUpRunId = followUp.Messages[^1].RunId!.Value;
        Assert.Contains("Files attached to it:", (await RunAsync(followUpRunId)).PromptSnapshot);
        var next = await StartAsync(runner, followUpRunId);
        using var nextAgent = TokenClient(next.AgentToken);
        Assert.Equal(3, (await nextAgent.GetFromJsonAsync<List<ChatFileView>>($"{ChatUrl}/attachments", ApiTestContext.Json, Ct))!.Count);
        Assert.Equal(HttpStatusCode.OK, (await nextAgent.GetAsync(Download(pdf), Ct)).StatusCode);

        // Clearing the chat deletes its files.
        Assert.Equal(HttpStatusCode.NoContent, (await Owner.DeleteAsync(ChatUrl, Ct)).StatusCode);
        Assert.Equal(0, await ScalarAsync($"SELECT count(*) FROM work.attachments WHERE uploaded_by = '{OwnerId}' AND chat_message_id IS NOT NULL"));
        Assert.Equal(HttpStatusCode.NotFound, (await Owner.GetAsync(Download(pdf), Ct)).StatusCode);
    }

    [Fact]
    public async Task unreadable_or_foreign_files_are_refused_with_a_reason_and_stay_uploads()
    {
        // Refused at upload, with the reason, when picked for chat.
        var broken = await UploadAsync(Owner, "broken.zip", "application/zip", "not a zip"u8.ToArray());
        Assert.Equal(HttpStatusCode.BadRequest, broken.StatusCode);
        Assert.Contains("broken.zip is not a readable ZIP archive.", await broken.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        var empty = await UploadAsync(Owner, "empty.zip", "application/zip", Zip());
        Assert.Contains("empty.zip is an empty ZIP archive.", await empty.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        var docx = await UploadAsync(Owner, "notes.docx",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document", "PK"u8.ToArray());
        Assert.Contains("cannot be read in chat", await docx.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        // An upload made for something else is checked again when it is sent; nothing is queued.
        var fake = await UploadOkAsync(Owner, "fake.pdf", "application/pdf", "hello"u8.ToArray(), forChat: false);
        var refused = await Owner.PostAsJsonAsync(ChatUrl, new { question = "Read it", attachmentIds = new[] { fake } }, ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("fake.pdf is not a readable PDF.", await refused.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Empty((await ChatAsync(Owner)).Messages);

        // Someone else's upload is not mine to send.
        using var colleague = await MemberAsync(canOperateFactory: true);
        var theirs = await UploadOkAsync(colleague, "theirs.pdf", "application/pdf", Pdf);
        var foreign = await Owner.PostAsJsonAsync(ChatUrl, new { question = "Read theirs", attachmentIds = new[] { theirs } }, ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode);
        Assert.Contains("no longer uploaded", await foreign.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        // Too many at once.
        var six = new List<Guid>();
        for (var i = 0; i < 6; i++) six.Add(await UploadOkAsync(Owner, $"n{i}.txt", "text/plain", "note"u8.ToArray()));
        var tooMany = await Owner.PostAsJsonAsync(ChatUrl, new { question = "All of them", attachmentIds = six }, ApiTestContext.Json, Ct);
        Assert.Contains("at most 5 files", await tooMany.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        // A question refused because another is in flight hands its file back as an upload.
        await AskAsync(Owner, "First");
        var good = await UploadOkAsync(Owner, "brief.pdf", "application/pdf", Pdf);
        var busy = await Owner.PostAsJsonAsync(ChatUrl, new { question = "Second", attachmentIds = new[] { good } }, ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.Conflict, busy.StatusCode);
        Assert.Equal(1, await ScalarAsync($"SELECT count(*) FROM work.attachments WHERE id = '{good}' AND status = 0 AND chat_message_id IS NULL"));
        Assert.Equal(1, await ScalarAsync($"SELECT count(*) FROM work.attachments WHERE id = '{fake}' AND status = 0"));
    }

    [Fact]
    public async Task dropping_the_oldest_question_deletes_its_files()
    {
        var file = await UploadOkAsync(Owner, "old.txt", "text/plain", "old notes"u8.ToArray());
        var first = await AskWithFilesAsync(Owner, "Question 1", file);
        var firstId = first.Messages[0].Id;
        await ExecuteSqlAsync($"""
            UPDATE automation.runs SET status = 5, finished_at = now() WHERE id = '{first.Messages[0].RunId}';
            UPDATE automation.project_chat_messages SET status = 1, answer = 'A', answered_at = now(), asked_at = now() - interval '1 hour'
            WHERE id = '{firstId}'
            """);
        for (var i = 2; i <= 10; i++)
        {
            await ExecuteSqlAsync($"""
                INSERT INTO automation.project_chat_messages
                    (id, organization_id, project_id, user_id, question, answer, status, asked_at, answered_at)
                VALUES (gen_random_uuid(), '{OrganizationId}', '{Project.Id}', '{OwnerId}', 'Question {i}', 'Answer {i}', 1,
                        now() - interval '{20 - i} minutes', now() - interval '{20 - i} minutes')
                """);
        }

        var eleventh = await AskAsync(Owner, "Question 11");
        Assert.DoesNotContain(eleventh.Messages, row => row.Id == firstId);
        Assert.Equal(0, await ScalarAsync($"SELECT count(*) FROM work.attachments WHERE id = '{file}'"));
    }

    [Fact]
    public async Task a_ticket_created_from_chat_gets_its_own_copies_of_the_shared_files()
    {
        var png = await UploadOkAsync(Owner, "screen.png", "image/png", Png);
        var archive = Zip(("logs/error.txt", "NullReference at Checkout"));
        var zip = await UploadOkAsync(Owner, "logs.zip", "application/zip", archive);
        var first = await AskWithFilesAsync(Owner, "Why does checkout crash?", png, zip);
        using var runner = RunnerClient(Runner.Secret);
        var firstRunId = first.Messages[0].RunId!.Value;
        await StartAsync(runner, firstRunId);
        await FinishAsync(runner, firstRunId, new RunnerFinishRequest("succeeded", 0, "A null cart.", null, null, null, null, null));

        // The files were shared a turn earlier; the run that creates the ticket is told how to attach them.
        var second = await AskAsync(Owner, "Make a bug for it with the screenshot and logs");
        var runId = second.Messages[^1].RunId!.Value;
        var prompt = (await RunAsync(runId)).PromptSnapshot!;
        Assert.Contains("pass their attachment ids listed above as attachmentIds", prompt);
        Assert.Contains("When the person names which files to attach, attach exactly those.", prompt);
        var claimed = await StartAsync(runner, runId);
        var items = await ScalarAsync($"SELECT count(*) FROM work.items WHERE project_id = '{Project.Id}'");

        await using var mcp = await ConnectAsync(claimed.AgentToken);
        // Someone else's chat file, and a plain upload, are not this chat's to share: nothing is created.
        using var colleague = await MemberAsync(canOperateFactory: true);
        var theirs = await UploadOkAsync(colleague, "theirs.pdf", "application/pdf", Pdf);
        await AskWithFilesAsync(colleague, "Their question", theirs);
        var upload = await UploadOkAsync(Owner, "loose.txt", "text/plain", "loose"u8.ToArray());
        foreach (var foreign in new[] { theirs, upload })
        {
            var refused = await mcp.CallToolAsync("create_item", new Dictionary<string, object?>
            {
                ["project"] = Project.Key, ["title"] = "Not mine", ["attachmentIds"] = new[] { png, foreign },
            }, cancellationToken: Ct);
            Assert.Contains("No item was created", Text(refused));
            Assert.Contains(foreign.ToString(), Text(refused));
        }
        Assert.Equal(items, await ScalarAsync($"SELECT count(*) FROM work.items WHERE project_id = '{Project.Id}'"));

        var created = await mcp.CallToolAsync("create_item", new Dictionary<string, object?>
        {
            ["project"] = Project.Key, ["title"] = "Checkout crashes on an empty cart", ["attachmentIds"] = new[] { png, zip },
        }, cancellationToken: Ct);
        Assert.True(created.IsError != true, Text(created));
        using var result = JsonDocument.Parse(Text(created));
        var key = result.RootElement.GetProperty("key").GetString()!;
        Assert.False(result.RootElement.TryGetProperty("attachmentFailures", out _), Text(created));
        var copies = result.RootElement.GetProperty("attachments").EnumerateArray()
            .Select(x => (Id: x.GetProperty("id").GetGuid(), Source: x.GetProperty("sourceId").GetGuid(), Name: x.GetProperty("fileName").GetString())).ToList();
        Assert.Equal([png, zip], copies.Select(x => x.Source));
        Assert.Equal(["screen.webp", "logs.zip"], copies.Select(x => x.Name));
        Assert.DoesNotContain(copies, x => x.Id == png || x.Id == zip);
        // The item page shows the files where the description links them: the image inline, the ZIP as a download.
        var item = await mcp.CallToolAsync("get_item", new Dictionary<string, object?> { ["key"] = key }, cancellationToken: Ct);
        var description = JsonDocument.Parse(Text(item)).RootElement.GetProperty("descriptionMarkdown").GetString()!;
        Assert.Contains("**Files from project chat**", description);
        Assert.Contains($"![screen.webp](/api/v1/orgs/{Slug}/attachments/{copies[0].Id}/download)", description);
        Assert.Contains($"[logs.zip](/api/v1/orgs/{Slug}/attachments/{copies[1].Id}/download)", description);
        Assert.DoesNotContain($"![logs.zip]", description);
        Assert.Equal(result.RootElement.GetProperty("version").GetUInt32(), JsonDocument.Parse(Text(item)).RootElement.GetProperty("version").GetUInt32());

        // Another member who can see the ticket lists and downloads them, the ZIP as the archive itself;
        // the asker is their uploader. The chat files stay private and in the chat.
        var listed = (await colleague.GetFromJsonAsync<List<AttachmentView>>($"/api/v1/orgs/{Slug}/items/{key}/attachments", ApiTestContext.Json, Ct))!;
        Assert.Equal(copies.Select(x => x.Id), listed.Select(x => x.Id));
        Assert.All(listed, x => Assert.Equal(OwnerId, x.UploadedBy));
        Assert.Equal(archive, await colleague.GetByteArrayAsync(Download(copies[1].Id), Ct));
        Assert.Equal(HttpStatusCode.OK, (await colleague.GetAsync(Download(copies[0].Id), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await colleague.GetAsync(Download(zip), Ct)).StatusCode);
        Assert.Equal(2, (await ChatAsync(Owner)).Messages[0].Attachments.Count);
        Assert.Equal(HttpStatusCode.OK, (await Owner.GetAsync(Download(zip), Ct)).StatusCode);

        // A chat file whose object is gone does not stop the ticket, and the result says which file it lost.
        var storage = Context.Factory.Services.GetRequiredService<IBlobStorage>();
        var pngKey = await ScalarTextAsync($"SELECT object_key FROM work.attachments WHERE id = '{png}'");
        await storage.DeleteAsync(pngKey, Ct);
        var partial = await mcp.CallToolAsync("create_item", new Dictionary<string, object?>
        {
            ["project"] = Project.Key, ["title"] = "Partly attached", ["attachmentIds"] = new[] { zip, png },
        }, cancellationToken: Ct);
        Assert.True(partial.IsError != true, Text(partial));
        using var partialResult = JsonDocument.Parse(Text(partial));
        var partialKey = partialResult.RootElement.GetProperty("key").GetString()!;
        Assert.Equal(png, Assert.Single(partialResult.RootElement.GetProperty("attachmentFailures").EnumerateArray()).GetProperty("sourceId").GetGuid());
        Assert.Contains($"{partialKey} was created, but 1 of 2 files could not be attached: screen.webp", partialResult.RootElement.GetProperty("warning").GetString());
        Assert.Equal("logs.zip", Assert.Single((await Owner.GetFromJsonAsync<List<AttachmentView>>(
            $"/api/v1/orgs/{Slug}/items/{partialKey}/attachments", ApiTestContext.Json, Ct))!).FileName);
        Assert.Equal(0, await ScalarAsync($"SELECT count(*) FROM work.attachments WHERE item_id IS NULL AND chat_message_id IS NULL AND uploaded_by = '{OwnerId}' AND id <> '{upload}'"));

        // A subtask can carry files too.
        var subtask = await mcp.CallToolAsync("create_subtask", new Dictionary<string, object?>
        {
            ["parentKey"] = key, ["title"] = "Reproduce with the logs", ["attachmentIds"] = new[] { zip },
        }, cancellationToken: Ct);
        Assert.True(subtask.IsError != true, Text(subtask));
        Assert.Single(JsonDocument.Parse(Text(subtask)).RootElement.GetProperty("attachments").EnumerateArray());

        // Clearing the chat deletes the chat files, never the ticket's copies.
        Assert.Equal(HttpStatusCode.NoContent, (await Owner.DeleteAsync(ChatUrl, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Owner.GetAsync(Download(zip), Ct)).StatusCode);
        Assert.Equal(archive, await colleague.GetByteArrayAsync(Download(copies[1].Id), Ct));
        Assert.Equal(HttpStatusCode.OK, (await colleague.GetAsync(Download(copies[0].Id), Ct)).StatusCode);
    }

    private sealed record ChatView(IReadOnlyList<ChatMessageView> Messages, bool RunnerOnline);

    private sealed record ChatMessageView(
        Guid Id, string Question, string? Answer, string Status, string? RunStatus, string? Error,
        Guid? RunId, DateTimeOffset AskedAt, DateTimeOffset? AnsweredAt, IReadOnlyList<ChatFileView> Attachments);

    private sealed record ChatFileView(Guid Id, Guid? ChatMessageId, string FileName, string ContentType, long SizeBytes);
}

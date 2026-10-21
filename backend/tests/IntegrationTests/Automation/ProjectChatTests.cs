using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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

    private sealed record ChatView(IReadOnlyList<ChatMessageView> Messages, bool RunnerOnline);

    private sealed record ChatMessageView(
        Guid Id, string Question, string? Answer, string Status, string? RunStatus, string? Error,
        Guid? RunId, DateTimeOffset AskedAt, DateTimeOffset? AnsweredAt);
}

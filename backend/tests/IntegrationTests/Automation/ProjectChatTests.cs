using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.Automation.Prompts;
using Aictiq.Modules.Tenancy.Domain;
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
        var asked = await AskAsync(Owner, "Make tickets for the two redirect bugs");
        var runId = asked.Messages[0].RunId!.Value;
        using var runner = RunnerClient(Runner.Secret);
        var claimed = await StartAsync(runner, runId);

        await using (var mcp = await ConnectAsync(claimed.AgentToken))
        {
            var created = await mcp.CallToolAsync("create_item", new Dictionary<string, object?>
            {
                ["project"] = Project.Key, ["title"] = "Redirect drops the query string", ["description"] = "Seen on /login?next=",
            }, cancellationToken: Ct);
            Assert.True(created.IsError != true, Text(created));
            var key = Regex.Match(Text(created), $"{Project.Key}-\\d+", RegexOptions.IgnoreCase).Value;
            var item = await ItemAsync(key);
            Assert.Equal("Redirect drops the query string", item.Title);
            Assert.Equal("Seen on /login?next=", item.DescriptionMarkdown);
            // Reported by the person who asked, not the agent.
            Assert.Equal(1, await ScalarAsync($"""
                SELECT count(*) FROM work.items WHERE id = '{item.Id}' AND created_by = '{OwnerId}'
                """));

            var elsewhere = await mcp.CallToolAsync("create_item", new Dictionary<string, object?>
            {
                ["project"] = other.Key, ["title"] = "Not here",
            }, cancellationToken: Ct);
            Assert.Contains("not found or no access", Text(elsewhere), StringComparison.Ordinal);

            var subtask = await mcp.CallToolAsync("create_subtask", new Dictionary<string, object?>
            {
                ["parentKey"] = key, ["title"] = "Nope",
            }, cancellationToken: Ct);
            Assert.True(subtask.IsError);
            Assert.Contains("requires the 'write'", Text(subtask), StringComparison.Ordinal);

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
    public async Task a_project_without_a_default_agent_explains_itself()
    {
        await ExecuteSqlAsync($"UPDATE automation.project_settings SET default_agent_id = NULL WHERE project_id = '{Project.Id}'");
        var response = await Owner.PostAsJsonAsync(ChatUrl, new { question = "Hello?" }, ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("no default agent", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Empty((await ChatAsync(Owner)).Messages);

        var blank = await Owner.PostAsJsonAsync(ChatUrl, new { question = "   " }, ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
    }

    private sealed record ChatView(IReadOnlyList<ChatMessageView> Messages, bool RunnerOnline);

    private sealed record ChatMessageView(
        Guid Id, string Question, string? Answer, string Status, string? RunStatus, string? Error,
        Guid? RunId, DateTimeOffset AskedAt, DateTimeOffset? AnsweredAt);
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.Automation.Workers;
using Aictiq.Modules.Tenancy.Domain;
using Aictiq.Modules.WorkItems.Contracts;
using Aictiq.Modules.WorkItems.Endpoints;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Paging;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Aictiq.IntegrationTests.Automation;

/// <summary>
/// Steering an agent from the comments: a mention starts a run, or a follow-up of the agent's
/// earlier run on the runner that kept its session; a mention made while the item is busy waits
/// its turn; the agent answers in the comment's thread; and a stakeholder's mention only notifies.
/// The Workers handlers are invoked in line, the way the outbox would deliver them.
/// </summary>
[Trait("Category", "Automation")]
public sealed class RunMentionTests(PostgresFixture postgres, GarageFixture garage) : RunTestBase(postgres, garage)
{
    private const string PullRequest = "https://github.com/acme/web/pull/7";

    protected override Action<IDictionary<string, string?>>? Configure => settings =>
    {
        settings["Automation:PollTimeoutSeconds"] = "2";
    };

    private async Task<CommentView> CommentAsync(HttpClient author, string itemKey, string body, Guid? parent = null)
    {
        var response = await author.PostAsJsonAsync($"/api/v1/orgs/{Slug}/items/{itemKey}/comments/",
            new CreateCommentRequest(body, parent), ApiTestContext.Json, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<CommentView>(ApiTestContext.Json, Ct))!;
    }

    /// <summary>Delivers the comment's staged <c>CommentAdded</c> to the Automation handler.</summary>
    private async Task DeliverAsync(CommentView comment)
    {
        var @event = await StagedAsync<CommentAdded>("CommentId", comment.Id);
        using var scope = Context.Factory.Services.CreateScope();
        var handler = ActivatorUtilities.CreateInstance<Aictiq.Modules.Automation.Events.AgentMentionHandler>(scope.ServiceProvider);
        await handler.HandleAsync(@event, Ct);
    }

    private async Task<T> StagedAsync<T>(string field, Guid value)
    {
        await using var connection = new NpgsqlConnection(Context.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "SELECT payload FROM shared.outbox_messages WHERE type = @type AND payload::jsonb ->> @field = @value",
            connection);
        command.Parameters.AddWithValue("type", typeof(T).FullName!);
        command.Parameters.AddWithValue("field", field);
        command.Parameters.AddWithValue("value", value.ToString());
        var payload = Assert.IsType<string>(await command.ExecuteScalarAsync(Ct));
        return JsonSerializer.Deserialize<T>(payload)!;
    }

    /// <summary>Applies the run's outcome to the item and marks it delivered, as Workers would.</summary>
    private async Task SettleAsync(Guid runId)
    {
        await InvokeRunFinishedAsync(await StagedRunFinishedAsync(runId));
        await ExecuteSqlAsync($"""
            UPDATE shared.outbox_messages SET processed_at = now()
            WHERE type = '{typeof(RunFinished).FullName}' AND payload::jsonb ->> 'RunId' = '{runId}'
            """);
    }

    private async Task SweepAsync() =>
        await ActivatorUtilities.CreateInstance<RunSweeper>(Context.Factory.Services).RunOnceAsync(Ct);

    private async Task<List<RunView>> ItemRunsAsync(string itemKey) =>
        [.. (await Owner.GetFromJsonAsync<PagedResult<RunView>>(
            $"/api/v1/orgs/{Slug}/items/{itemKey}/runs", ApiTestContext.Json, Ct))!.Items];

    private Task<long> PendingMentionsAsync() =>
        ScalarAsync("SELECT count(*) FROM automation.run_mentions WHERE status = 0");

    private async Task<RunnerRunClaimed> StartAsync(HttpClient runner, Guid runId)
    {
        var claimed = await ClaimAsync(runner);
        Assert.Equal(runId, claimed.RunId);
        Assert.Equal(HttpStatusCode.NoContent, (await StartedAsync(runner, runId)).StatusCode);
        return claimed;
    }

    private async Task FinishAsync(HttpClient runner, Guid runId, RunnerFinishRequest request)
    {
        var response = await runner.PostAsJsonAsync($"/api/v1/runner/runs/{runId}/finish", request, ApiTestContext.Json, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task a_mention_of_an_agent_that_never_ran_starts_an_implement_run_and_it_replies_in_the_thread()
    {
        var comment = await CommentAsync(Owner, ItemKey, "@builder the redirect should keep the query string");
        await DeliverAsync(comment);

        var run = Assert.Single(await ItemRunsAsync(ItemKey));
        Assert.Equal(comment.Id, run.TriggerCommentId);
        Assert.Null(run.FollowsUpRunId);
        Assert.Equal(OwnerId, run.RequestedBy);
        Assert.Equal(AgentId, run.AgentId);
        var detail = await RunAsync(run.Id);
        Assert.Contains("> @builder the redirect should keep the query string", detail.PromptSnapshot);
        Assert.Contains($"comment {comment.Id}", detail.PromptSnapshot);

        // Replayed delivery asks for nothing more.
        await DeliverAsync(comment);
        Assert.Single(await ItemRunsAsync(ItemKey));

        using var runner = RunnerClient(Runner.Secret);
        var claimed = await StartAsync(runner, run.Id);
        Assert.Null(claimed.FollowUp);
        await FinishAsync(runner, run.Id, new RunnerFinishRequest(
            "succeeded", 0, "Kept the query string on redirect.", PullRequest, null, null, null, null, "sess-1"));
        await SettleAsync(run.Id);

        var reply = (await CommentsAsync(ItemKey)).Items.Single(c => c.Author.IsAgent);
        Assert.Equal(comment.Id, reply.ParentCommentId);
        Assert.Contains("Kept the query string on redirect.", reply.BodyMarkdown);
        Assert.Contains($"[Pull request]({PullRequest})", reply.BodyMarkdown);

        // A stakeholder sees the item's discussion but not the agent's answer.
        var stakeholderAuth = await Context.RegisterAsync($"stakeholder-{Guid.NewGuid():N}@test.local");
        using var stakeholder = Context.ClientFor(stakeholderAuth);
        await AddMemberAsync(stakeholderAuth.User.Id, OrgRole.Member, canOperateFactory: false);
        var seen = (await stakeholder.GetFromJsonAsync<PagedResult<CommentView>>(
            $"/api/v1/orgs/{Slug}/items/{ItemKey}/comments/", ApiTestContext.Json, Ct))!.Items;
        Assert.Contains(seen, c => c.Id == comment.Id);
        Assert.DoesNotContain(seen, c => c.Author.IsAgent);
    }

    [Fact]
    public async Task a_follow_up_goes_to_the_runner_that_kept_the_session_and_may_start_a_new_branch()
    {
        var first = await DispatchAsync(ItemKey);
        var dispatchedState = (await ItemAsync(ItemKey)).StateId;
        using var runner = RunnerClient(Runner.Secret);
        await StartAsync(runner, first.Id);
        await FinishAsync(runner, first.Id, new RunnerFinishRequest(
            "succeeded", 0, "done", PullRequest, null, null, null, null, "sess-1", "/runner/r1/repo"));
        await SettleAsync(first.Id);
        Assert.NotEqual(dispatchedState, (await ItemAsync(ItemKey)).StateId);

        var other = await RegisterRunnerAsync("box-2");
        var comment = await CommentAsync(Owner, ItemKey, "@builder please also cover the logout redirect");
        await DeliverAsync(comment);

        var followUp = (await ItemRunsAsync(ItemKey)).Single(run => run.Id != first.Id);
        Assert.Equal(first.Id, followUp.FollowsUpRunId);
        Assert.Equal(comment.Id, followUp.TriggerCommentId);
        Assert.Equal(Runner.Runner.Id, followUp.RequestedRunnerId);
        Assert.Equal(first.BranchName, followUp.BranchName);
        // It moves the item the way a first implement run does.
        Assert.Equal(dispatchedState, (await ItemAsync(ItemKey)).StateId);

        // Only the runner with the session may take it.
        using var otherClient = RunnerClient(other.Secret);
        Assert.Equal(HttpStatusCode.NoContent, (await ClaimMaybeAsync(otherClient))!.StatusCode);
        var claimed = await StartAsync(runner, followUp.Id);
        Assert.Null(claimed.Resume);
        var view = Assert.IsType<RunFollowUpView>(claimed.FollowUp);
        Assert.Equal(first.Id, view.PreviousRunId);
        Assert.Equal("sess-1", view.SessionId);
        Assert.Equal(PullRequest, view.PullRequestUrl);
        Assert.Equal(first.BranchName, view.PreviousBranchName);
        Assert.Equal($"{first.BranchName}-2", view.NewBranchName);
        Assert.Equal("@builder please also cover the logout redirect", view.Instruction);
        Assert.Equal("Alice Anderson", view.RequestedByName);
        Assert.Equal(comment.Id, view.CommentId);

        // The earlier pull request was merged: the runner reports the fresh branch it started.
        var arbitrary = await runner.PostAsJsonAsync($"/api/v1/runner/runs/{followUp.Id}/finish",
            new RunnerFinishRequest("succeeded", 0, "done", null, null, null, null, null, BranchName: "main"),
            ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, arbitrary.StatusCode);
        await FinishAsync(runner, followUp.Id, new RunnerFinishRequest(
            "succeeded", 0, "Covered logout too.", "https://github.com/acme/web/pull/8", null, null, null, null,
            "sess-1", BranchName: view.NewBranchName));
        Assert.Equal(view.NewBranchName, (await RunAsync(followUp.Id)).BranchName);
    }

    [Fact]
    public async Task mentions_made_while_a_run_holds_the_item_wait_and_run_in_order()
    {
        var first = await DispatchAsync(ItemKey);
        var one = await CommentAsync(Owner, ItemKey, "@builder first, rename the helper");
        var two = await CommentAsync(Owner, ItemKey, "@builder then add a test");
        await DeliverAsync(one);
        await DeliverAsync(two);
        Assert.Single(await ItemRunsAsync(ItemKey));
        Assert.Equal(2, await PendingMentionsAsync());

        using var runner = RunnerClient(Runner.Secret);
        await StartAsync(runner, first.Id);
        await FinishAsync(runner, first.Id, new RunnerFinishRequest(
            "succeeded", 0, "done", PullRequest, null, null, null, null, "sess-1"));
        // Not before the finished run's outcome has reached the item.
        await SweepAsync();
        Assert.Equal(2, await PendingMentionsAsync());

        await SettleAsync(first.Id);
        await SweepAsync();
        var second = (await ItemRunsAsync(ItemKey)).Single(run => run.Id != first.Id);
        Assert.Equal(one.Id, second.TriggerCommentId);
        Assert.Equal(first.Id, second.FollowsUpRunId);
        Assert.Equal(1, await PendingMentionsAsync());

        // Never two at once.
        await SweepAsync();
        Assert.Equal(2, (await ItemRunsAsync(ItemKey)).Count);

        // The earlier pull request is gone: the runner stops before the harness and says why.
        await StartAsync(runner, second.Id);
        await FinishAsync(runner, second.Id, new RunnerFinishRequest(
            "failed", null, "I didn't start: the pull request can no longer be found.", null, null, null, null,
            "follow-up-target-missing", "sess-1"));
        await SettleAsync(second.Id);
        var reply = (await CommentsAsync(ItemKey)).Items.Single(c => c.Author.IsAgent && c.ParentCommentId == one.Id);
        Assert.Contains("the pull request can no longer be found", reply.BodyMarkdown);

        await SweepAsync();
        var third = (await ItemRunsAsync(ItemKey)).Single(run => run.Id != first.Id && run.Id != second.Id);
        Assert.Equal(two.Id, third.TriggerCommentId);
        Assert.Equal(second.Id, third.FollowsUpRunId);
        Assert.Equal(0, await PendingMentionsAsync());
    }

    [Fact]
    public async Task a_stakeholders_mention_and_a_mention_of_a_person_only_notify()
    {
        var stakeholderAuth = await Context.RegisterAsync($"stakeholder-{Guid.NewGuid():N}@test.local");
        using var stakeholder = Context.ClientFor(stakeholderAuth);
        await AddMemberAsync(stakeholderAuth.User.Id, OrgRole.Member, canOperateFactory: false);

        var fromStakeholder = await CommentAsync(stakeholder, ItemKey, "@builder can you change the colour?");
        Assert.Contains(AgentId, fromStakeholder.Mentions);
        await DeliverAsync(fromStakeholder);

        var ofPerson = await CommentAsync(Owner, ItemKey, "@Alice have a look");
        await DeliverAsync(ofPerson);

        Assert.Empty(await ItemRunsAsync(ItemKey));
        Assert.Equal(0, await ScalarAsync("SELECT count(*) FROM automation.run_mentions"));
    }

    [Fact]
    public async Task a_refine_run_is_never_followed_up()
    {
        var refine = await DispatchAsync(ItemKey);
        using var runner = RunnerClient(Runner.Secret);
        await StartAsync(runner, refine.Id);
        await FinishAsync(runner, refine.Id, new RunnerFinishRequest(
            "succeeded", 0, "done", null, null, null, null, null, "sess-1"));
        await SettleAsync(refine.Id);
        // Recast as a refine run: what matters here is only its kind.
        await ExecuteSqlAsync($"UPDATE automation.runs SET kind = 1 WHERE id = '{refine.Id}'");

        var comment = await CommentAsync(Owner, ItemKey, "@builder go ahead and build it");
        await DeliverAsync(comment);
        var run = (await ItemRunsAsync(ItemKey)).Single(r => r.Id != refine.Id);
        Assert.Null(run.FollowsUpRunId);
        Assert.Null(run.RequestedRunnerId);
        Assert.Equal(comment.Id, run.TriggerCommentId);
    }

    [Fact]
    public async Task a_mention_that_can_never_start_gets_a_reply_saying_why()
    {
        var api = await CreateProjectAsync("Api", "api", ProjectVisibility.Organization);
        await CreateAgentAsync("helper", [api.Id]);
        var item = await CreateItemAsync("Add rate limits", "api");

        // The project has no playbook at all.
        var comment = await CommentAsync(Owner, item.Key, "@helper add rate limits please");
        await DeliverAsync(comment);
        Assert.Empty(await ItemRunsAsync(item.Key));

        var refused = await StagedAsync<RunMentionRefused>("CommentId", comment.Id);
        using (var scope = Context.Factory.Services.CreateScope())
        {
            var handler = ActivatorUtilities.CreateInstance<Aictiq.Modules.WorkItems.Events.RunMentionRefusedHandler>(scope.ServiceProvider);
            await handler.HandleAsync(refused, Ct);
            // A replay changes nothing.
            await handler.HandleAsync(refused, Ct);
        }

        var reply = (await CommentsAsync(item.Key)).Items.Single(c => c.Author.IsAgent);
        Assert.Equal(comment.Id, reply.ParentCommentId);
        Assert.Contains("no default playbook", reply.BodyMarkdown);
        Assert.Equal(1, await ScalarAsync("SELECT count(*) FROM automation.run_mentions WHERE status = 2"));
    }
}

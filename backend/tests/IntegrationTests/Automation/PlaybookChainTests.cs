using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.Automation.Endpoints;
using Aictiq.Modules.Automation.Workers;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Paging;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Aictiq.IntegrationTests.Automation;

/// <summary>
/// Playbook chaining: a builder playbook's follow-up playbooks check its pull request one after
/// another before the item reaches human review; changes requested rerun the builder with the
/// findings, at most three times per item; a stage that fails never blocks the ticket. The
/// Workers handler and the sweeper are driven in line, the way the outbox and the timer would.
/// </summary>
[Trait("Category", "Automation")]
public sealed class PlaybookChainTests(PostgresFixture postgres, GarageFixture garage) : RunTestBase(postgres, garage)
{
    private const string PullRequest = "https://github.com/acme/web/pull/7";

    protected override Action<IDictionary<string, string?>>? Configure => settings =>
    {
        settings["Automation:PollTimeoutSeconds"] = "2";
    };

    private async Task<PlaybookView> CreatePlaybookAsync(string name, string harness = "claude")
    {
        var response = await Owner.PostAsJsonAsync($"{ProjectBase}/playbooks",
            new CreatePlaybookRequest(name, null, harness, null, null, 30, $"Check the pull request as {name}."), ApiTestContext.Json, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<PlaybookView>(ApiTestContext.Json, Ct))!;
    }

    private Task<HttpResponseMessage> PutStagesAsync(Guid playbookId, params Guid[] stages) =>
        Owner.PutAsJsonAsync($"{ProjectBase}/playbooks/{playbookId}/stages",
            new PutPlaybookStagesRequest(stages), ApiTestContext.Json, Ct);

    /// <summary>The builder playbook sends the item to In Review; its stages go after it in this order.</summary>
    private async Task<Guid> ChainAsync(params Guid[] stages)
    {
        var inReview = await StateIdAsync("In Review");
        var builder = (await Owner.GetFromJsonAsync<PlaybookView>($"{ProjectBase}/playbooks/{PlaybookId}", ApiTestContext.Json, Ct))!;
        (await Owner.PatchAsJsonAsync($"{ProjectBase}/playbooks/{PlaybookId}",
            new UpdatePlaybookRequest { OnSuccessStateId = inReview, Version = builder.Version }, ApiTestContext.Json, Ct))
            .EnsureSuccessStatusCode();
        (await PutStagesAsync(PlaybookId, stages)).EnsureSuccessStatusCode();
        return inReview;
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

    private async Task<List<RunView>> ItemRunsAsync() =>
        [.. (await Owner.GetFromJsonAsync<PagedResult<RunView>>(
            $"/api/v1/orgs/{Slug}/items/{ItemKey}/runs", ApiTestContext.Json, Ct))!.Items];

    /// <summary>Claims and starts the item's queued run, which must be <paramref name="runId"/> when given.</summary>
    private async Task<RunnerRunClaimed> StartNextAsync(HttpClient runner, Guid? runId = null)
    {
        var claimed = await ClaimAsync(runner);
        if (runId is { } expected) Assert.Equal(expected, claimed.RunId);
        Assert.Equal(HttpStatusCode.NoContent, (await StartedAsync(runner, claimed.RunId)).StatusCode);
        return claimed;
    }

    private async Task FinishOkAsync(HttpClient runner, Guid runId, string summary, string? pullRequest = null)
    {
        var response = await FinishAsync(runner, runId, RunOutcomes.Succeeded, summary, pullRequest);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    private async Task<CallToolResult> VerdictAsync(string token, string verdict, string? findings = null)
    {
        await using var client = await ConnectAsync(token);
        return await client.CallToolAsync("submit_stage_verdict", new Dictionary<string, object?>
        {
            ["key"] = ItemKey, ["verdict"] = verdict, ["findings"] = findings,
        }, cancellationToken: Ct);
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

    private Task<long> PendingStepsAsync() =>
        ScalarAsync("SELECT count(*) FROM automation.chain_steps WHERE status = 0");

    [Fact]
    public async Task follow_up_playbooks_are_saved_in_order_and_validated()
    {
        var review = await CreatePlaybookAsync("Code review", "codex");
        var qa = await CreatePlaybookAsync("QA");

        var plain = (await Owner.GetFromJsonAsync<List<PlaybookView>>($"{ProjectBase}/playbooks", ApiTestContext.Json, Ct))!;
        Assert.All(plain, playbook => Assert.Empty(playbook.StagePlaybookIds!));

        Assert.Equal(HttpStatusCode.BadRequest, (await PutStagesAsync(PlaybookId, PlaybookId)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PutStagesAsync(PlaybookId, review.Id, review.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PutStagesAsync(PlaybookId, Guid.NewGuid())).StatusCode);
        var otherProject = await CreateProjectAsync("Mobile", "mob", Aictiq.Modules.Tenancy.Domain.ProjectVisibility.Organization);
        var foreign = await Owner.PostAsJsonAsync($"/api/v1/orgs/{Slug}/projects/{otherProject.Key}/playbooks",
            new CreatePlaybookRequest("Foreign", null, "claude", null, null, 30, "Elsewhere."), ApiTestContext.Json, Ct);
        var foreignId = (await foreign.Content.ReadFromJsonAsync<PlaybookView>(ApiTestContext.Json, Ct))!.Id;
        Assert.Equal(HttpStatusCode.BadRequest, (await PutStagesAsync(PlaybookId, foreignId)).StatusCode);

        var saved = await PutStagesAsync(PlaybookId, review.Id, qa.Id);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal([review.Id, qa.Id], (await saved.Content.ReadFromJsonAsync<PlaybookView>(ApiTestContext.Json, Ct))!.StagePlaybookIds);

        (await PutStagesAsync(PlaybookId, qa.Id, review.Id)).EnsureSuccessStatusCode();
        var listed = (await Owner.GetFromJsonAsync<List<PlaybookView>>($"{ProjectBase}/playbooks", ApiTestContext.Json, Ct))!;
        Assert.Equal([qa.Id, review.Id], listed.Single(playbook => playbook.Id == PlaybookId).StagePlaybookIds);
        Assert.Empty(listed.Single(playbook => playbook.Id == qa.Id).StagePlaybookIds!);

        // Deleting a stage's playbook takes it out of the chain.
        (await Owner.DeleteAsync($"{ProjectBase}/playbooks/{qa.Id}", Ct)).EnsureSuccessStatusCode();
        var single = (await Owner.GetFromJsonAsync<PlaybookView>($"{ProjectBase}/playbooks/{PlaybookId}", ApiTestContext.Json, Ct))!;
        Assert.Equal([review.Id], single.StagePlaybookIds);

        (await PutStagesAsync(PlaybookId)).EnsureSuccessStatusCode();
        Assert.Equal(0, await ScalarAsync("SELECT count(*) FROM automation.playbook_stages"));
    }

    [Fact]
    public async Task stages_run_one_after_another_on_the_pull_request_and_the_item_reaches_review_when_all_pass()
    {
        var review = await CreatePlaybookAsync("Code review");
        var qa = await CreatePlaybookAsync("QA");
        var inReview = await ChainAsync(review.Id, qa.Id);
        // A stage's own chain is never followed.
        (await PutStagesAsync(review.Id, qa.Id)).EnsureSuccessStatusCode();
        using var runner = RunnerClient(Runner.Secret);

        var builder = await DispatchAsync(ItemKey);
        var active = (await ItemAsync(ItemKey)).StateId;
        await StartNextAsync(runner, builder.Id);
        await FinishOkAsync(runner, builder.Id, "Built it.", PullRequest);
        var staged = await StagedRunFinishedAsync(builder.Id);
        Assert.True(staged.Chained);
        await SettleAsync(builder.Id);
        var waiting = await ItemAsync(ItemKey);
        Assert.Equal(active, waiting.StateId);
        Assert.Null(waiting.ClaimedBy);
        Assert.Equal(1, await PendingStepsAsync());

        // Stage 1 on the builder's branch and pull request, as the builder's agent, with a fresh session.
        await SweepAsync();
        var first = (await ItemRunsAsync()).Single(run => run.Kind == "stage");
        Assert.Equal(review.Id, first.PlaybookId);
        Assert.Equal("Code review", first.PlaybookName);
        Assert.Equal(0, first.StageIndex);
        Assert.Equal(AgentId, first.AgentId);
        Assert.Equal(OwnerId, first.RequestedBy);
        var claimed = await StartNextAsync(runner, first.Id);
        Assert.Equal(builder.BranchName, claimed.BranchName);
        Assert.Equal("stage", claimed.FollowUp!.Kind);
        Assert.Null(claimed.FollowUp.SessionId);
        Assert.Null(claimed.FollowUp.CommentId);
        Assert.Equal(PullRequest, claimed.FollowUp.PullRequestUrl);
        Assert.Contains("follow-up playbook 1 of 2", claimed.Prompt);
        Assert.Contains("# Playbook: Code review", claimed.Prompt);
        Assert.Contains($"submit_stage_verdict(\"{ItemKey}\"", claimed.Prompt);
        Assert.Contains(PullRequest, claimed.Prompt);
        // Claimed where it stands: the chain, not the stage, moves the item.
        Assert.Equal(active, (await ItemAsync(ItemKey)).StateId);

        Assert.True((await VerdictAsync(claimed.AgentToken, "changes_requested")).IsError);
        Assert.NotEqual(true, (await VerdictAsync(claimed.AgentToken, "pass")).IsError);
        await FinishOkAsync(runner, first.Id, "Looks good.");
        Assert.Equal("pass", (await RunAsync(first.Id)).StageVerdict);
        await SettleAsync(first.Id);
        Assert.Equal(active, (await ItemAsync(ItemKey)).StateId);

        await SweepAsync();
        var second = (await ItemRunsAsync()).Single(run => run.Kind == "stage" && run.Id != first.Id);
        Assert.Equal(qa.Id, second.PlaybookId);
        Assert.Equal(1, second.StageIndex);
        claimed = await StartNextAsync(runner, second.Id);
        Assert.Contains("follow-up playbook 2 of 2", claimed.Prompt);
        Assert.NotEqual(true, (await VerdictAsync(claimed.AgentToken, "pass")).IsError);
        await FinishOkAsync(runner, second.Id, "Tests pass.");
        Assert.False((await StagedRunFinishedAsync(second.Id)).Chained);
        await SettleAsync(second.Id);

        var done = await ItemAsync(ItemKey);
        Assert.Equal(inReview, done.StateId);
        Assert.Equal(0, await PendingStepsAsync());
        await SweepAsync();
        Assert.Equal(3, (await ItemRunsAsync()).Count);
        var comment = (await CommentsAsync(ItemKey)).Items.Last(c => c.Author.IsAgent);
        Assert.StartsWith($"Follow-up playbook QA: run {second.Id} passed", comment.BodyMarkdown);
        Assert.Contains("Every follow-up playbook passed", comment.BodyMarkdown);
    }

    [Fact]
    public async Task changes_requested_rerun_the_builder_with_the_findings_then_restart_the_chain_until_three_rounds()
    {
        var review = await CreatePlaybookAsync("Code review");
        var inReview = await ChainAsync(review.Id);
        using var runner = RunnerClient(Runner.Secret);

        var builder = await DispatchAsync(ItemKey);
        await StartNextAsync(runner, builder.Id);
        var finish = await runner.PostAsJsonAsync($"/api/v1/runner/runs/{builder.Id}/finish",
            new RunnerFinishRequest(RunOutcomes.Succeeded, 0, "Built it.", PullRequest, null, null, null, null, "sess-b"),
            ApiTestContext.Json, Ct);
        finish.EnsureSuccessStatusCode();
        await SettleAsync(builder.Id);

        for (var round = 1; round <= 4; round++)
        {
            await SweepAsync();
            var stage = (await ItemRunsAsync()).First(run => run.Kind == "stage" && run.Status == "queued");
            var claimed = await StartNextAsync(runner, stage.Id);
            Assert.NotEqual(true, (await VerdictAsync(claimed.AgentToken, "changes_requested", $"Round {round}: handle the empty list.")).IsError);
            await FinishOkAsync(runner, stage.Id, "Found a bug.");
            await SettleAsync(stage.Id);
            var comment = (await CommentsAsync(ItemKey)).Items.Last(c => c.Author.IsAgent);
            Assert.Contains("requested changes", comment.BodyMarkdown);
            Assert.Contains($"> Round {round}: handle the empty list.", comment.BodyMarkdown);
            if (round == 4)
            {
                // Three rework rounds already: the item goes to a person with the findings open.
                Assert.Contains("already had 3 rework rounds", comment.BodyMarkdown);
                Assert.Equal(inReview, (await ItemAsync(ItemKey)).StateId);
                break;
            }
            Assert.NotEqual(inReview, (await ItemAsync(ItemKey)).StateId);

            await SweepAsync();
            var rework = (await ItemRunsAsync()).Single(run => run.Status == "queued");
            Assert.Equal("implement", rework.Kind);
            Assert.Equal(stage.Id, rework.ReworkOfRunId);
            Assert.Equal(PlaybookId, rework.PlaybookId);
            var reworkClaim = await StartNextAsync(runner, rework.Id);
            Assert.Equal("rework", reworkClaim.FollowUp!.Kind);
            Assert.Equal("sess-b", reworkClaim.FollowUp.SessionId);
            Assert.Equal("Code review", reworkClaim.FollowUp.RequestedByName);
            Assert.Equal($"Round {round}: handle the empty list.", reworkClaim.FollowUp.Instruction);
            Assert.Equal(PullRequest, reworkClaim.FollowUp.PullRequestUrl);
            Assert.Contains($"rework round {round} of 3", reworkClaim.Prompt);
            var reworkFinish = await runner.PostAsJsonAsync($"/api/v1/runner/runs/{rework.Id}/finish",
                new RunnerFinishRequest(RunOutcomes.Succeeded, 0, "Handled it.", null, null, null, null, null, "sess-b"),
                ApiTestContext.Json, Ct);
            reworkFinish.EnsureSuccessStatusCode();
            await SettleAsync(rework.Id);
        }

        var runs = await ItemRunsAsync();
        Assert.Equal(3, runs.Count(run => run.ReworkOfRunId is not null));
        Assert.Equal(4, runs.Count(run => run.Kind == "stage"));
        Assert.Equal(0, await PendingStepsAsync());
    }

    [Fact]
    public async Task a_failed_stage_sends_the_item_to_review_and_is_not_continued()
    {
        var review = await CreatePlaybookAsync("Security");
        var inReview = await ChainAsync(review.Id);
        using var runner = RunnerClient(Runner.Secret);

        var builder = await DispatchAsync(ItemKey);
        await StartNextAsync(runner, builder.Id);
        await FinishOkAsync(runner, builder.Id, "Built it.", PullRequest);
        await SettleAsync(builder.Id);
        await SweepAsync();
        var stage = (await ItemRunsAsync()).Single(run => run.Kind == "stage");
        await StartNextAsync(runner, stage.Id);
        var failed = await runner.PostAsJsonAsync($"/api/v1/runner/runs/{stage.Id}/finish",
            new RunnerFinishRequest(RunOutcomes.Failed, 1, "Rate limited.", null, null, null, null, "harness-rate-limited", "sess-s"),
            ApiTestContext.Json, Ct);
        failed.EnsureSuccessStatusCode();
        await SettleAsync(stage.Id);

        Assert.Equal(inReview, (await ItemAsync(ItemKey)).StateId);
        var comment = (await CommentsAsync(ItemKey)).Items.Last(c => c.Author.IsAgent);
        Assert.StartsWith($"Follow-up playbook Security: run {stage.Id} failed", comment.BodyMarkdown);
        Assert.Contains("chain ends here and the item goes to human review", comment.BodyMarkdown);
        Assert.Equal(0, await ScalarAsync($"SELECT count(*) FROM automation.runs WHERE id = '{stage.Id}' AND auto_continue_due_at IS NOT NULL"));
        await SweepAsync();
        Assert.Equal(2, (await ItemRunsAsync()).Count);
    }

    [Fact]
    public async Task a_step_that_can_never_start_sends_the_item_to_review_with_the_reason()
    {
        var review = await CreatePlaybookAsync("Code review");
        var inReview = await ChainAsync(review.Id);
        using var runner = RunnerClient(Runner.Secret);

        var builder = await DispatchAsync(ItemKey);
        await StartNextAsync(runner, builder.Id);
        await FinishOkAsync(runner, builder.Id, "Built it.", PullRequest);
        await SettleAsync(builder.Id);
        // The stage list changes before the stage could start.
        (await PutStagesAsync(PlaybookId)).EnsureSuccessStatusCode();
        await SweepAsync();

        Assert.Equal(0, await PendingStepsAsync());
        Assert.Single(await ItemRunsAsync());
        var stopped = await StagedChainStoppedAsync(builder.Id);
        Assert.True(stopped.ChainStopped);
        await InvokeRunFinishedAsync(stopped);
        Assert.Equal(inReview, (await ItemAsync(ItemKey)).StateId);
        var comment = (await CommentsAsync(ItemKey)).Items.Last(c => c.Author.IsAgent);
        Assert.StartsWith("Follow-up playbook chain stopped", comment.BodyMarkdown);
        Assert.Contains("follow-up playbooks changed while the chain ran", comment.BodyMarkdown);
    }

    [Fact]
    public async Task only_a_live_stage_run_may_submit_a_verdict()
    {
        var review = await CreatePlaybookAsync("Code review");
        await ChainAsync(review.Id);
        using var runner = RunnerClient(Runner.Secret);
        var builder = await DispatchAsync(ItemKey);
        var claimed = await StartNextAsync(runner, builder.Id);

        var refused = await VerdictAsync(claimed.AgentToken, "pass");
        Assert.Contains("no follow-up playbook run in progress", Assert.Single(refused.Content.OfType<TextContentBlock>()).Text);
        Assert.True((await VerdictAsync(claimed.AgentToken, "maybe")).IsError);
    }

    private async Task<RunFinished> StagedChainStoppedAsync(Guid runId)
    {
        await using var connection = new Npgsql.NpgsqlConnection(Context.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new Npgsql.NpgsqlCommand(
            "SELECT payload FROM shared.outbox_messages WHERE type = @type AND payload::jsonb ->> 'RunId' = @runId AND processed_at IS NULL",
            connection);
        command.Parameters.AddWithValue("type", typeof(RunFinished).FullName!);
        command.Parameters.AddWithValue("runId", runId.ToString());
        var payload = Assert.IsType<string>(await command.ExecuteScalarAsync(Ct));
        return System.Text.Json.JsonSerializer.Deserialize<RunFinished>(payload)!;
    }
}

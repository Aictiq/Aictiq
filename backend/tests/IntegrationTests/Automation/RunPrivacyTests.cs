using System.Net;
using System.Net.Http.Json;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.Automation.Workers;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Paging;
using Microsoft.Extensions.DependencyInjection;

namespace Aictiq.IntegrationTests.Automation;

/// <summary>
/// Who reads a run in full: an organization Admin or Owner reads every run; a member reads
/// only the runs they started, and of anyone else's sees that it ran but not its prompt,
/// summary, failure reason or log. An automatic continue belongs to whoever started the run
/// it continues.
/// </summary>
[Trait("Category", "Automation")]
public sealed class RunPrivacyTests(PostgresFixture postgres, GarageFixture garage) : RunTestBase(postgres, garage)
{
    protected override Action<IDictionary<string, string?>>? Configure => settings =>
    {
        // An idle claim long-polls; a short poll keeps "nothing to claim" cheap.
        settings["Automation:PollTimeoutSeconds"] = "2";
    };

    private async Task<(HttpClient Client, string UserId)> PersonAsync(string name, OrgRole role)
    {
        var auth = await Context.RegisterAsync($"{name}-{Guid.NewGuid():N}@test.local");
        await AddMemberAsync(auth.User.Id, role, canOperateFactory: true);
        return (Context.ClientFor(auth), auth.User.Id);
    }

    private async Task<RunView> DispatchAsAsync(HttpClient client, string itemKey)
    {
        var response = await client.PostAsJsonAsync($"/api/v1/orgs/{Slug}/items/{itemKey}/runs",
            new DispatchRunRequest(null, null), ApiTestContext.Json, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<RunView>(ApiTestContext.Json, Ct))!;
    }

    /// <summary>Claims, logs and fails the next run, as the runner would.</summary>
    private async Task FailAsync(HttpClient runner, Guid runId, string failureReason)
    {
        var claimed = await ClaimAsync(runner);
        Assert.Equal(runId, claimed.RunId);
        Assert.Equal(HttpStatusCode.NoContent, (await StartedAsync(runner, runId)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await PostLogAsync(runner, runId, (0, "secret agent output\n"))).StatusCode);
        var finish = await runner.PostAsJsonAsync($"/api/v1/runner/runs/{runId}/finish",
            new RunnerFinishRequest("failed", 1, "What the agent found", null, null, null, null, failureReason, "sess-1"),
            ApiTestContext.Json, Ct);
        Assert.True(finish.IsSuccessStatusCode, await finish.Content.ReadAsStringAsync(Ct));
        await InvokeRunFinishedAsync(await StagedRunFinishedAsync(runId));
    }

    private async Task<RunView> DetailAsync(HttpClient client, Guid runId) =>
        (await client.GetFromJsonAsync<RunView>($"/api/v1/orgs/{Slug}/runs/{runId}", ApiTestContext.Json, Ct))!;

    private async Task<RunView> ListedAsync(HttpClient client, Guid runId) =>
        Assert.Single((await client.GetFromJsonAsync<PagedResult<RunView>>(
            $"/api/v1/orgs/{Slug}/runs?project={Project.Key}", ApiTestContext.Json, Ct))!.Items, run => run.Id == runId);

    private async Task<RunView> ItemListedAsync(HttpClient client, string itemKey, Guid runId) =>
        Assert.Single((await client.GetFromJsonAsync<PagedResult<RunView>>(
            $"/api/v1/orgs/{Slug}/items/{itemKey}/runs", ApiTestContext.Json, Ct))!.Items, run => run.Id == runId);

    private Task<HttpResponseMessage> GetLogAsync(HttpClient client, Guid runId) =>
        client.GetAsync($"/api/v1/orgs/{Slug}/runs/{runId}/log", Ct);

    private async Task AssertSeesInFullAsync(HttpClient client, Guid runId)
    {
        var detail = await DetailAsync(client, runId);
        Assert.False(detail.Private);
        Assert.Equal("What the agent found", detail.OutcomeSummary);
        Assert.Equal("harness-exit-1", detail.FailureReason);
        Assert.NotNull(detail.PromptSnapshot);
        var listed = await ListedAsync(client, runId);
        Assert.False(listed.Private);
        Assert.Equal("What the agent found", listed.OutcomeSummary);
        var log = await GetLogAsync(client, runId);
        Assert.Equal(HttpStatusCode.OK, log.StatusCode);
        var page = (await log.Content.ReadFromJsonAsync<RunLogPage>(ApiTestContext.Json, Ct))!;
        Assert.Contains(page.Items, entry => entry.Text.Contains("secret agent output", StringComparison.Ordinal));
    }

    private async Task AssertSeesOnlyThatItRanAsync(HttpClient client, string itemKey, Guid runId)
    {
        var detail = await DetailAsync(client, runId);
        Assert.True(detail.Private);
        Assert.Equal(itemKey, detail.ItemKey);
        Assert.Equal("failed", detail.Status);
        Assert.Null(detail.OutcomeSummary);
        Assert.Null(detail.FailureReason);
        Assert.Null(detail.PromptSnapshot);
        foreach (var listed in new[] { await ListedAsync(client, runId), await ItemListedAsync(client, itemKey, runId) })
        {
            Assert.True(listed.Private);
            Assert.Null(listed.OutcomeSummary);
        }
        Assert.Equal(HttpStatusCode.NotFound, (await GetLogAsync(client, runId)).StatusCode);
    }

    [Theory]
    [InlineData("implement")]
    [InlineData("refine")]
    public async Task a_member_reads_only_their_own_runs_and_an_admin_reads_every_run(string kind)
    {
        var (ana, _) = await PersonAsync("ana", OrgRole.Member);
        var (marko, _) = await PersonAsync("marko", OrgRole.Member);
        var (admin, _) = await PersonAsync("admin", OrgRole.Admin);
        using (ana) using (marko) using (admin)
        {
            var run = await DispatchAsAsync(ana, ItemKey);
            using var runner = RunnerClient(Runner.Secret);
            await FailAsync(runner, run.Id, "harness-exit-1");
            if (kind == "refine")
            {
                await ExecuteSqlAsync($"UPDATE automation.runs SET kind = 1 WHERE id = '{run.Id}'");
            }
            Assert.Equal(kind, (await DetailAsync(ana, run.Id)).Kind);

            await AssertSeesInFullAsync(ana, run.Id);
            await AssertSeesOnlyThatItRanAsync(marko, ItemKey, run.Id);
            await AssertSeesInFullAsync(admin, run.Id);
            await AssertSeesInFullAsync(Owner, run.Id);
        }
    }

    [Fact]
    public async Task an_automatic_continue_is_read_by_whoever_started_the_run_it_continues()
    {
        var (ana, anaId) = await PersonAsync("ana", OrgRole.Member);
        var (marko, _) = await PersonAsync("marko", OrgRole.Member);
        using (ana) using (marko)
        {
            var run = await DispatchAsAsync(ana, ItemKey);
            using var runner = RunnerClient(Runner.Secret);
            await FailAsync(runner, run.Id, "harness-rate-limited");

            await ExecuteSqlAsync(
                "UPDATE automation.runs SET auto_continue_due_at = now() - interval '1 second' WHERE auto_continue_due_at IS NOT NULL");
            await ActivatorUtilities.CreateInstance<RunSweeper>(Context.Factory.Services).RunOnceAsync(Ct);
            var continued = (await RunAsync(run.Id)).ContinuedByRunId!.Value;
            var continuedRun = await RunAsync(continued);
            Assert.True(continuedRun.AutoContinued);
            Assert.Equal(anaId, continuedRun.RequestedBy);

            Assert.False((await DetailAsync(ana, continued)).Private);
            Assert.Equal(HttpStatusCode.OK, (await GetLogAsync(ana, continued)).StatusCode);
            Assert.True((await DetailAsync(marko, continued)).Private);
            Assert.Equal(HttpStatusCode.NotFound, (await GetLogAsync(marko, continued)).StatusCode);
        }
    }
}

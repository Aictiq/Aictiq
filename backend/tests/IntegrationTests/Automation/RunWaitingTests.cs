using System.Net;
using System.Net.Http.Json;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.Automation.Domain;
using Aictiq.Modules.Automation.Endpoints;
using Aictiq.SharedKernel.Authorization;

namespace Aictiq.IntegrationTests.Automation;

/// <summary>
/// The run page says what a queued run waits for - which runner, and whether it is offline or
/// busy - instead of a bare "waiting for a runner", which reads like something is stuck.
/// </summary>
[Trait("Category", "Automation")]
public sealed class RunWaitingTests(PostgresFixture postgres, GarageFixture garage) : RunTestBase(postgres, garage)
{
    protected override Action<IDictionary<string, string?>>? Configure => settings =>
        settings["Automation:PollTimeoutSeconds"] = "2";

    private async Task HelloAsync(string secret, int maxParallel = 1)
    {
        using var client = RunnerClient(secret);
        var capabilities = new RunnerCapabilities(1, [new RunnerHarness("claude", "2.1.0")], "linux", "x64", "0.8.0", maxParallel);
        (await client.PostAsJsonAsync("/api/v1/runner/hello",
            new RunnerHelloRequest(capabilities), ApiTestContext.Json, Ct)).EnsureSuccessStatusCode();
    }

    private async Task<RunView> DispatchToAsync(string itemKey, Guid runnerId)
    {
        var response = await Owner.PostAsJsonAsync($"/api/v1/orgs/{Slug}/items/{itemKey}/runs",
            new DispatchRunRequest(null, null, runnerId), ApiTestContext.Json, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<RunView>(ApiTestContext.Json, Ct))!;
    }

    [Fact]
    public async Task a_queued_run_names_the_busy_runner_and_the_runs_ahead_of_it()
    {
        // Nobody has said hello yet: no runner offers claude.
        var first = await DispatchAsync(ItemKey);
        var none = (await RunAsync(first.Id)).Waiting!;
        Assert.Equal("no-runner", none.Reason);
        Assert.Empty(none.Runners);

        await HelloAsync(Runner.Secret);
        var free = (await RunAsync(first.Id)).Waiting!;
        Assert.Equal("runner-free", free.Reason);
        Assert.Equal(["box-1"], free.Runners);

        using var runner = RunnerClient(Runner.Secret);
        Assert.Equal(first.Id, (await ClaimAsync(runner)).RunId);
        Assert.Null((await RunAsync(first.Id)).Waiting);

        // Its only slot is taken: the next two wait for it, the second behind the first.
        var second = await DispatchAsync((await CreateItemAsync("Second")).Key);
        var third = await DispatchAsync((await CreateItemAsync("Third")).Key);
        var busy = (await RunAsync(third.Id)).Waiting!;
        Assert.Equal("runners-busy", busy.Reason);
        Assert.Equal(["box-1"], busy.Runners);
        Assert.Equal(1, busy.Ahead);
        Assert.Equal(0, (await RunAsync(second.Id)).Waiting!.Ahead);
    }

    [Fact]
    public async Task a_run_sent_to_one_runner_says_whether_that_runner_is_offline_or_busy()
    {
        await HelloAsync(Runner.Secret);
        var other = await RegisterRunnerAsync("box-2");
        var run = await DispatchToAsync(ItemKey, other.Runner.Id);

        // box-1 is free, but the run is box-2's alone, and box-2 has never connected.
        var offline = (await RunAsync(run.Id)).Waiting!;
        Assert.Equal("runner-offline", offline.Reason);
        Assert.Equal(["box-2"], offline.Runners);

        await HelloAsync(other.Secret);
        using var otherClient = RunnerClient(other.Secret);
        var blocking = await DispatchToAsync((await CreateItemAsync("Blocking")).Key, other.Runner.Id);
        Assert.Equal(run.Id, (await ClaimAsync(otherClient)).RunId);
        var busy = (await RunAsync(blocking.Id)).Waiting!;
        Assert.Equal("runner-busy", busy.Reason);
        Assert.Equal(["box-2"], busy.Runners);
    }

    [Fact]
    public async Task only_factory_operators_see_why_and_a_scheduled_run_has_nothing_to_explain()
    {
        await HelloAsync(Runner.Secret);
        var run = await DispatchAsync(ItemKey);

        var bob = await Context.RegisterAsync($"run-viewer-{Guid.NewGuid():N}@test.local", "Bob", "Brown");
        await AddMemberAsync(bob.User.Id, OrgRole.Member, canOperateFactory: false);
        using var member = Context.ClientFor(bob);
        var seen = (await member.GetFromJsonAsync<RunView>($"/api/v1/orgs/{Slug}/runs/{run.Id}", ApiTestContext.Json, Ct))!;
        Assert.Null(seen.Waiting);

        var later = await Owner.PostAsJsonAsync($"/api/v1/orgs/{Slug}/items/{(await CreateItemAsync("Later")).Key}/runs",
            new DispatchRunRequest(null, null, ScheduledFor: DateTimeOffset.UtcNow.AddHours(6)), ApiTestContext.Json, Ct);
        var scheduled = (await later.Content.ReadFromJsonAsync<RunView>(ApiTestContext.Json, Ct))!;
        Assert.Null((await RunAsync(scheduled.Id)).Waiting);
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.Automation.Workers;
using Microsoft.Extensions.DependencyInjection;

namespace Aictiq.IntegrationTests.Automation;

/// <summary>A run handed to an agent for later: it waits, queued, until its start time.</summary>
[Trait("Category", "Automation")]
public sealed class ScheduledRunTests(PostgresFixture postgres, GarageFixture garage) : RunTestBase(postgres, garage)
{
    // An idle claim long-polls; a short poll keeps "nothing to claim" from costing 25 s a test.
    protected override Action<IDictionary<string, string?>>? Configure => settings =>
        settings["Automation:PollTimeoutSeconds"] = "2";

    private Task<HttpResponseMessage> ScheduleAsync(string itemKey, DateTimeOffset startAt) =>
        Owner.PostAsJsonAsync($"/api/v1/orgs/{Slug}/items/{itemKey}/runs",
            new DispatchRunRequest(null, null, ScheduledFor: startAt), ApiTestContext.Json, Ct);

    private async Task<RunView> ScheduledAsync(string itemKey, DateTimeOffset startAt)
    {
        var response = await ScheduleAsync(itemKey, startAt);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<RunView>(ApiTestContext.Json, Ct))!;
    }

    private Task MakeDueAsync(Guid runId) => ExecuteSqlAsync(
        $"UPDATE automation.runs SET scheduled_for = now() - interval '1 second' WHERE id = '{runId}'");

    [Fact]
    public async Task a_scheduled_run_is_stored_in_utc_and_waits_for_its_time()
    {
        // 22:00 in Sarajevo (UTC+2 in summer) is 20:00 UTC.
        var tomorrow = DateTimeOffset.UtcNow.AddDays(1);
        var local = new DateTimeOffset(tomorrow.Year, tomorrow.Month, tomorrow.Day, 22, 0, 0, TimeSpan.FromHours(2));
        var run = await ScheduledAsync(ItemKey, local);
        Assert.Equal("queued", run.Status);
        Assert.Equal(local.UtcDateTime, run.ScheduledFor!.Value.UtcDateTime);
        Assert.Equal(TimeSpan.Zero, run.ScheduledFor.Value.Offset);
        Assert.Equal(20, run.ScheduledFor.Value.Hour);

        // The item is the agent's from the moment the run is scheduled.
        Assert.Equal(AgentId, (await ItemAsync(ItemKey)).ClaimedBy);
        var second = await Owner.PostAsJsonAsync($"/api/v1/orgs/{Slug}/items/{ItemKey}/runs",
            new DispatchRunRequest(null, null), ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        using var runner = RunnerClient(Runner.Secret);
        Assert.Equal(HttpStatusCode.NoContent, (await ClaimMaybeAsync(runner))!.StatusCode);

        // A run queued now is not held up behind the scheduled one.
        var other = await CreateItemAsync("Tidy the footer");
        var now = await DispatchAsync(other.Key);
        Assert.Null(now.ScheduledFor);
        Assert.Equal(now.Id, (await ClaimAsync(runner)).RunId);

        await MakeDueAsync(run.Id);
        Assert.Equal(run.Id, (await ClaimAsync(runner)).RunId);
        Assert.Equal("assigned", (await RunAsync(run.Id)).Status);
    }

    [Fact]
    public async Task a_scheduled_run_sent_to_one_runner_still_waits_for_that_runner()
    {
        var other = await RegisterRunnerAsync("box-2");
        var response = await Owner.PostAsJsonAsync($"/api/v1/orgs/{Slug}/items/{ItemKey}/runs",
            new DispatchRunRequest(null, null, Runner.Runner.Id, DateTimeOffset.UtcNow.AddHours(6)), ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var run = (await response.Content.ReadFromJsonAsync<RunView>(ApiTestContext.Json, Ct))!;

        await MakeDueAsync(run.Id);
        using var otherClient = RunnerClient(other.Secret);
        Assert.Equal(HttpStatusCode.NoContent, (await ClaimMaybeAsync(otherClient))!.StatusCode);
        using var requested = RunnerClient(Runner.Secret);
        Assert.Equal(run.Id, (await ClaimAsync(requested)).RunId);
    }

    [Fact]
    public async Task a_start_time_in_the_past_is_refused_and_claims_nothing()
    {
        var refused = await ScheduleAsync(ItemKey, DateTimeOffset.UtcNow.AddMinutes(-1));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        using var problem = JsonDocument.Parse(await refused.Content.ReadAsStringAsync(Ct));
        Assert.Equal("Choose a start time in the future.",
            problem.RootElement.GetProperty("errors").GetProperty("scheduledFor")[0].GetString());

        Assert.Null((await ItemAsync(ItemKey)).ClaimedBy);
        Assert.Equal(0, await ScalarAsync("SELECT count(*) FROM automation.runs"));
    }

    [Fact]
    public async Task a_scheduled_run_can_be_cancelled_before_it_starts()
    {
        var run = await ScheduledAsync(ItemKey, DateTimeOffset.UtcNow.AddHours(6));
        var cancel = await Owner.PostAsync($"/api/v1/orgs/{Slug}/runs/{run.Id}/cancel", null, Ct);
        Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode);
        Assert.Equal("cancelled", (await RunAsync(run.Id)).Status);

        await MakeDueAsync(run.Id);
        using var runner = RunnerClient(Runner.Secret);
        Assert.Equal(HttpStatusCode.NoContent, (await ClaimMaybeAsync(runner))!.StatusCode);
    }

    [Fact]
    public async Task the_sweeper_leaves_a_waiting_scheduled_run_alone()
    {
        var run = await ScheduledAsync(ItemKey, DateTimeOffset.UtcNow.AddHours(6));
        // However long ago it was asked for, the wait counts from its start time.
        await ExecuteSqlAsync($"UPDATE automation.runs SET queued_at = now() - interval '2 days' WHERE id = '{run.Id}'");

        var sweeper = ActivatorUtilities.CreateInstance<RunSweeper>(Context.Factory.Services);
        await sweeper.RunOnceAsync(Ct);

        var after = await RunAsync(run.Id);
        Assert.Equal("queued", after.Status);
        Assert.Null(after.FailureReason);
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Aictiq.IntegrationTests.Storage;
using Aictiq.SharedKernel.Authorization;
using Aictiq.Modules.Automation.Workers;
using Aictiq.Modules.Tenancy.Endpoints;
using Microsoft.Extensions.DependencyInjection;

namespace Aictiq.IntegrationTests.Automation;

/// <summary>A run handed to an agent for later: it waits, queued, until its start time.</summary>
[Trait("Category", "Automation")]
public sealed class ScheduledRunTests(PostgresFixture postgres, GarageFixture garage) : RunTestBase(postgres, garage)
{
    // An idle claim long-polls; a short poll keeps "nothing to claim" from costing 25 s a test.
    protected override Action<IDictionary<string, string?>>? Configure => settings =>
        settings["Automation:PollTimeoutSeconds"] = "2";

    private Task<HttpResponseMessage> ScheduleAsync(string itemKey, DateTimeOffset startAt, Guid? runnerId = null) =>
        Owner.PostAsJsonAsync($"/api/v1/orgs/{Slug}/items/{itemKey}/runs",
            new DispatchRunRequest(null, null, runnerId, ScheduledFor: startAt), ApiTestContext.Json, Ct);

    private Task<HttpResponseMessage> RescheduleAsync(Guid runId, DateTimeOffset startAt, HttpClient? client = null) =>
        (client ?? Owner).PostAsJsonAsync($"/api/v1/orgs/{Slug}/runs/{runId}/reschedule",
            new { scheduledFor = startAt }, ApiTestContext.Json, Ct);

    private async Task<RunView> ScheduledAsync(string itemKey, DateTimeOffset startAt, Guid? runnerId = null)
    {
        var response = await ScheduleAsync(itemKey, startAt, runnerId);
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
    public async Task start_now_lets_a_scheduled_run_go_without_giving_up_the_item()
    {
        var run = await ScheduledAsync(ItemKey, DateTimeOffset.UtcNow.AddHours(6));
        var start = await Owner.PostAsync($"/api/v1/orgs/{Slug}/runs/{run.Id}/start-now", null, Ct);
        Assert.Equal(HttpStatusCode.NoContent, start.StatusCode);

        // Still the same run and the same claim: nothing was cancelled, nothing re-dispatched.
        var after = await RunAsync(run.Id);
        Assert.Equal("queued", after.Status);
        Assert.Null(after.ScheduledFor);
        Assert.Equal(AgentId, (await ItemAsync(ItemKey)).ClaimedBy);
        Assert.Equal(1, await ScalarAsync("SELECT count(*) FROM automation.runs"));
        Assert.Equal(0, await ScalarAsync("SELECT count(*) FROM shared.outbox_messages WHERE type LIKE '%RunFinished%'"));

        using var runner = RunnerClient(Runner.Secret);
        Assert.Equal(run.Id, (await ClaimAsync(runner)).RunId);

        // Once a runner has it there is nothing left to start early.
        var again = await Owner.PostAsync($"/api/v1/orgs/{Slug}/runs/{run.Id}/start-now", null, Ct);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Fact]
    public async Task start_now_is_refused_for_a_run_that_is_not_waiting_for_its_time()
    {
        var now = await DispatchAsync(ItemKey);
        var refused = await Owner.PostAsync($"/api/v1/orgs/{Slug}/runs/{now.Id}/start-now", null, Ct);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

        var other = await CreateItemAsync("Tidy the footer");
        var due = await ScheduledAsync(other.Key, DateTimeOffset.UtcNow.AddHours(6));
        await MakeDueAsync(due.Id);
        var late = await Owner.PostAsync($"/api/v1/orgs/{Slug}/runs/{due.Id}/start-now", null, Ct);
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
    }

    [Fact]
    public async Task only_the_requester_or_a_project_admin_may_start_a_run_early()
    {
        var run = await ScheduledAsync(ItemKey, DateTimeOffset.UtcNow.AddHours(6));
        var bob = await Context.RegisterAsync($"run-member-{Guid.NewGuid():N}@test.local", "Bob", "Brown");
        await AddMemberAsync(bob.User.Id, OrgRole.Member, canOperateFactory: true);
        using var member = Context.ClientFor(bob);

        var refused = await member.PostAsync($"/api/v1/orgs/{Slug}/runs/{run.Id}/start-now", null, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.NotNull((await RunAsync(run.Id)).ScheduledFor);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(12)]
    public async Task rescheduling_moves_the_same_run_earlier_or_later_and_keeps_its_claim(int hours)
    {
        var run = await ScheduledAsync(ItemKey, DateTimeOffset.UtcNow.AddHours(6), Runner.Runner.Id);
        run = await RunAsync(run.Id);
        var originalItem = await ItemAsync(ItemKey);
        var newTime = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.AddHours(hours).ToUnixTimeSeconds())
            .ToOffset(TimeSpan.FromHours(2));
        var response = await RescheduleAsync(run.Id, newTime);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var after = await RunAsync(run.Id);
        Assert.Equal(run.Id, after.Id);
        Assert.Equal("queued", after.Status);
        Assert.Equal(newTime.UtcDateTime, after.ScheduledFor!.Value.UtcDateTime);
        Assert.Equal(TimeSpan.Zero, after.ScheduledFor.Value.Offset);
        Assert.Equal(run.QueuedAt, after.QueuedAt);
        Assert.Equal(run.RequestedBy, after.RequestedBy);
        Assert.Equal(run.PlaybookId, after.PlaybookId);
        Assert.Equal(run.PlaybookRevisionId, after.PlaybookRevisionId);
        Assert.Equal(run.AgentId, after.AgentId);
        Assert.Equal(Runner.Runner.Id, after.RequestedRunnerId);
        Assert.Equal(run.RequiredTags, after.RequiredTags);
        var afterItem = await ItemAsync(ItemKey);
        Assert.Equal(originalItem.Id, afterItem.Id);
        Assert.Equal(originalItem.ClaimedBy, afterItem.ClaimedBy);
        Assert.Equal(originalItem.ClaimedAt, afterItem.ClaimedAt);
        Assert.Equal(originalItem.ClaimHeartbeatAt, afterItem.ClaimHeartbeatAt);
        Assert.Equal(1, await ScalarAsync("SELECT count(*) FROM automation.runs"));
        Assert.Equal(0, await ScalarAsync("SELECT count(*) FROM shared.outbox_messages WHERE type LIKE '%RunFinished%'"));

        using var runner = RunnerClient(Runner.Secret);
        Assert.Equal(HttpStatusCode.NoContent, (await ClaimMaybeAsync(runner))!.StatusCode);
        await MakeDueAsync(run.Id);
        Assert.Equal(run.Id, (await ClaimAsync(runner)).RunId);
    }

    [Fact]
    public async Task rescheduling_refuses_a_past_or_missing_time_without_changing_the_schedule()
    {
        var run = await ScheduledAsync(ItemKey, DateTimeOffset.UtcNow.AddHours(6));
        run = await RunAsync(run.Id);
        var responses = new[]
        {
            await RescheduleAsync(run.Id, DateTimeOffset.UtcNow.AddMinutes(-1)),
            await Owner.PostAsJsonAsync($"/api/v1/orgs/{Slug}/runs/{run.Id}/reschedule", new { }, ApiTestContext.Json, Ct),
        };
        foreach (var response in responses)
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
            Assert.Equal("Choose a start time in the future.",
                problem.RootElement.GetProperty("errors").GetProperty("scheduledFor")[0].GetString());
        }
        Assert.Equal(run.ScheduledFor, (await RunAsync(run.Id)).ScheduledFor);
        Assert.Equal(AgentId, (await ItemAsync(ItemKey)).ClaimedBy);
    }

    [Fact]
    public async Task rescheduling_conflicts_when_a_run_is_no_longer_waiting_for_its_time()
    {
        var immediate = await DispatchAsync(ItemKey);
        Assert.Equal(HttpStatusCode.Conflict,
            (await RescheduleAsync(immediate.Id, DateTimeOffset.UtcNow.AddHours(12))).StatusCode);

        using var runner = RunnerClient(Runner.Secret);
        Assert.Equal(immediate.Id, (await ClaimAsync(runner)).RunId);
        Assert.Equal(HttpStatusCode.Conflict,
            (await RescheduleAsync(immediate.Id, DateTimeOffset.UtcNow.AddHours(12))).StatusCode);
        (await StartedAsync(runner, immediate.Id)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict,
            (await RescheduleAsync(immediate.Id, DateTimeOffset.UtcNow.AddHours(12))).StatusCode);

        var item = await CreateItemAsync("Scheduled work");
        var run = await ScheduledAsync(item.Key, DateTimeOffset.UtcNow.AddHours(6));
        await MakeDueAsync(run.Id);
        var due = await RescheduleAsync(run.Id, DateTimeOffset.UtcNow.AddHours(12));
        Assert.Equal(HttpStatusCode.Conflict, due.StatusCode);
        using var problem = JsonDocument.Parse(await due.Content.ReadAsStringAsync(Ct));
        Assert.Equal("This run is no longer waiting for its start time.", problem.RootElement.GetProperty("title").GetString());

        (await Owner.PostAsync($"/api/v1/orgs/{Slug}/runs/{run.Id}/cancel", null, Ct)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict,
            (await RescheduleAsync(run.Id, DateTimeOffset.UtcNow.AddHours(12))).StatusCode);
    }

    [Fact]
    public async Task rescheduling_requires_the_requester_or_project_admin_and_factory_permission()
    {
        var run = await ScheduledAsync(ItemKey, DateTimeOffset.UtcNow.AddHours(6));
        run = await RunAsync(run.Id);
        var bob = await Context.RegisterAsync($"reschedule-member-{Guid.NewGuid():N}@test.local");
        await AddMemberAsync(bob.User.Id, OrgRole.Member, canOperateFactory: true);
        using var member = Context.ClientFor(bob);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await RescheduleAsync(run.Id, DateTimeOffset.UtcNow.AddHours(12), member)).StatusCode);
        Assert.Equal(run.ScheduledFor, (await RunAsync(run.Id)).ScheduledFor);

        var memberItem = await CreateItemAsync("Member's scheduled work");
        var dispatch = await member.PostAsJsonAsync($"/api/v1/orgs/{Slug}/items/{memberItem.Key}/runs",
            new DispatchRunRequest(null, null, ScheduledFor: DateTimeOffset.UtcNow.AddHours(6)), ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.Created, dispatch.StatusCode);
        var memberRun = (await dispatch.Content.ReadFromJsonAsync<RunView>(ApiTestContext.Json, Ct))!;
        Assert.Equal(HttpStatusCode.NoContent,
            (await RescheduleAsync(memberRun.Id, DateTimeOffset.UtcNow.AddHours(12), member)).StatusCode);

        (await Owner.PutAsJsonAsync($"{ProjectBase}/members/{bob.User.Id}",
            new UpdateProjectMemberRequest(ProjectRole.Admin), ApiTestContext.Json, Ct))
            .EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NoContent,
            (await RescheduleAsync(run.Id, DateTimeOffset.UtcNow.AddHours(12), member)).StatusCode);

        var stakeholder = await Context.RegisterAsync($"reschedule-stakeholder-{Guid.NewGuid():N}@test.local");
        await AddMemberAsync(stakeholder.User.Id, OrgRole.Member, canOperateFactory: false);
        (await Owner.PutAsJsonAsync($"{ProjectBase}/members/{stakeholder.User.Id}",
            new UpdateProjectMemberRequest(ProjectRole.Admin), ApiTestContext.Json, Ct))
            .EnsureSuccessStatusCode();
        using var stakeholderClient = Context.ClientFor(stakeholder);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await RescheduleAsync(run.Id, DateTimeOffset.UtcNow.AddHours(18), stakeholderClient)).StatusCode);
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

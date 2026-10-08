using System.Net;
using System.Net.Http.Json;
using Aictiq.IntegrationTests.Storage;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Contracts;

namespace Aictiq.IntegrationTests.Automation;

/// <summary>The Runners tab's load: what each runner holds, what waits for it, and what waits for any runner.</summary>
[Trait("Category", "Automation")]
public sealed class RunnerLoadTests(PostgresFixture postgres, GarageFixture garage) : RunTestBase(postgres, garage)
{
    protected override Action<IDictionary<string, string?>>? Configure => settings =>
        settings["Automation:PollTimeoutSeconds"] = "2";

    private async Task<RunView> DispatchToAsync(string itemKey, Guid? runnerId, DateTimeOffset? scheduledFor = null)
    {
        var response = await Owner.PostAsJsonAsync($"/api/v1/orgs/{Slug}/items/{itemKey}/runs",
            new DispatchRunRequest(null, null, runnerId, scheduledFor), ApiTestContext.Json, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<RunView>(ApiTestContext.Json, Ct))!;
    }

    private async Task<LoadView> LoadAsync() =>
        (await Owner.GetFromJsonAsync<LoadView>($"/api/v1/orgs/{Slug}/runners/load", ApiTestContext.Json, Ct))!;

    [Fact]
    public async Task load_counts_running_queued_and_scheduled_per_runner_and_unassigned_once()
    {
        var empty = await LoadAsync();
        Assert.Empty(empty.Runners);
        Assert.Equal(0, empty.UnassignedQueued);

        // box-1 takes a run, then has one due and one scheduled waiting for it.
        var held = await DispatchToAsync(ItemKey, null);
        using var box1 = RunnerClient(Runner.Secret);
        Assert.Equal(held.Id, (await ClaimAsync(box1)).RunId);

        var due = await DispatchToAsync((await CreateItemAsync("Due for box-1")).Key, Runner.Runner.Id);
        var later = DateTimeOffset.UtcNow.AddHours(6);
        var scheduled = await DispatchToAsync((await CreateItemAsync("Later for box-1")).Key, Runner.Runner.Id, later);

        // Any runner: one due, one later.
        await DispatchToAsync((await CreateItemAsync("Any runner now")).Key, null);
        var anyLater = DateTimeOffset.UtcNow.AddHours(2);
        await DispatchToAsync((await CreateItemAsync("Any runner later")).Key, null, anyLater);

        // A run waiting for a runner that is deleted goes to any runner, so it counts there.
        var gone = await RegisterRunnerAsync("gone-box");
        await DispatchToAsync((await CreateItemAsync("For a retired box")).Key, gone.Runner.Id);
        (await Owner.DeleteAsync($"/api/v1/orgs/{Slug}/runners/{gone.Runner.Id}", Ct)).EnsureSuccessStatusCode();

        var load = await LoadAsync();
        var entry = Assert.Single(load.Runners);
        Assert.Equal(Runner.Runner.Id, entry.RunnerId);
        Assert.Equal(1, entry.Running);
        Assert.Equal(1, entry.Queued);
        Assert.Equal(1, entry.Scheduled);
        Assert.Equal(later.ToUnixTimeSeconds(), entry.NextScheduledFor!.Value.ToUnixTimeSeconds());
        var active = Assert.Single(entry.Active);
        Assert.Equal(held.Id, active.Id);
        Assert.Equal(ItemKey, active.ItemKey);
        Assert.Equal("assigned", active.Status);
        Assert.NotNull(active.PlaybookName);

        Assert.Equal(2, load.UnassignedQueued);
        Assert.Equal(1, load.UnassignedScheduled);
        Assert.Equal(anyLater.ToUnixTimeSeconds(), load.NextUnassignedScheduledFor!.Value.ToUnixTimeSeconds());

        // Once its time passes, a scheduled run is queued, not scheduled.
        await ExecuteSqlAsync($"UPDATE automation.runs SET scheduled_for = now() - interval '1 second' WHERE id = '{scheduled.Id}'");
        entry = Assert.Single((await LoadAsync()).Runners);
        Assert.Equal(2, entry.Queued);
        Assert.Equal(0, entry.Scheduled);
        Assert.Null(entry.NextScheduledFor);

        // A started run stays on the runner; a finished one frees its slot.
        Assert.Equal(HttpStatusCode.NoContent, (await StartedAsync(box1, held.Id)).StatusCode);
        Assert.Equal("running", Assert.Single(Assert.Single((await LoadAsync()).Runners).Active).Status);
        (await FinishAsync(box1, held.Id, RunOutcomes.Succeeded)).EnsureSuccessStatusCode();
        entry = Assert.Single((await LoadAsync()).Runners);
        Assert.Equal(0, entry.Running);
        Assert.Empty(entry.Active);
        Assert.Equal(2, entry.Queued);
        Assert.Equal("queued", (await RunAsync(due.Id)).Status);
    }

    [Fact]
    public async Task load_is_an_admins_like_the_roster()
    {
        var memberAuth = await Context.RegisterAsync($"member-{Guid.NewGuid():N}@test.local");
        using var member = Context.ClientFor(memberAuth);
        await AddMemberAsync(memberAuth.User.Id, OrgRole.Member, true);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync($"/api/v1/orgs/{Slug}/runners/load", Ct)).StatusCode);
    }

    private sealed record LoadView(
        List<LoadEntry> Runners, int UnassignedQueued, int UnassignedScheduled, DateTimeOffset? NextUnassignedScheduledFor);

    private sealed record LoadEntry(
        Guid RunnerId, int Running, int Queued, int Scheduled, DateTimeOffset? NextScheduledFor, List<ActiveRun> Active);

    private sealed record ActiveRun(Guid Id, string ItemKey, string? PlaybookName, string Status, DateTimeOffset? StartedAt);
}

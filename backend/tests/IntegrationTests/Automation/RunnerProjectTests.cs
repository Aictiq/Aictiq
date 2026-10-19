using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.Automation.Domain;
using Aictiq.Modules.Automation.Endpoints;

namespace Aictiq.IntegrationTests.Automation;

/// <summary>
/// A run of a project whose repository lives on the runner goes only to a runner that has it:
/// mapped (<c>aictiq runner map</c>), or under one of its repository roots. A runner on an older
/// CLI that reports neither still takes anything, and a runner that finds no clone after all
/// hands the run back for another.
/// </summary>
[Trait("Category", "Automation")]
public sealed class RunnerProjectTests(PostgresFixture postgres, GarageFixture garage) : RunTestBase(postgres, garage)
{
    protected override Action<IDictionary<string, string?>>? Configure => settings =>
    {
        settings["Billing:Mode"] = "saas";
        // An idle claim long-polls; a short poll keeps "nothing to claim" from costing 25 s a test.
        settings["Automation:PollTimeoutSeconds"] = "1";
        settings["Automation:FleetBalanceGraceSeconds"] = "300";
    };

    private static RunnerCapabilities Reports(IReadOnlyList<string>? workspaces, IReadOnlyList<string>? roots) =>
        new(1, [new RunnerHarness("claude", "2.1.0")], "linux", "x64", "0.16.0", 1, Workspaces: workspaces, RepoRoots: roots);

    private async Task HelloAsync(string secret, RunnerCapabilities capabilities)
    {
        using var client = RunnerClient(secret);
        var response = await client.PostAsJsonAsync("/api/v1/runner/hello", new RunnerHelloRequest(capabilities), ApiTestContext.Json, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    private async Task HeartbeatAsync(string secret, RunnerCapabilities capabilities)
    {
        using var client = RunnerClient(secret);
        var response = await client.PostAsJsonAsync("/api/v1/runner/heartbeat", new RunnerHeartbeatRequest(capabilities), ApiTestContext.Json, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    private Task<HttpResponseMessage> ReleaseAsync(HttpClient runner, Guid runId, string? reason) =>
        runner.PostAsJsonAsync($"/api/v1/runner/runs/{runId}/release", new RunnerReleaseRequest(reason), ApiTestContext.Json, Ct);

    private async Task<HttpStatusCode?> ClaimStatusAsync(string secret)
    {
        using var client = RunnerClient(secret);
        return (await ClaimMaybeAsync(client))?.StatusCode;
    }

    [Fact]
    public async Task only_a_runner_that_maps_the_project_claims_its_run_and_a_new_mapping_counts_from_the_next_heartbeat()
    {
        // box-1 maps another project only; its roots do not hold /srv/acme.
        await HelloAsync(Runner.Secret, Reports(["IDA"], ["/home/martijn/src"]));
        var run = await DispatchAsync(ItemKey);

        Assert.Equal(HttpStatusCode.NoContent, await ClaimStatusAsync(Runner.Secret));
        var waiting = (await RunAsync(run.Id)).Waiting!;
        Assert.Equal("no-runner-for-project", waiting.Reason);
        Assert.Equal(Project.Key, waiting.ProjectKey);

        // `aictiq runner map`, reported on the next heartbeat.
        await HeartbeatAsync(Runner.Secret, Reports(["IDA", Project.Key], ["/home/martijn/src"]));
        Assert.Equal("runner-free", (await RunAsync(run.Id)).Waiting!.Reason);
        using var box = RunnerClient(Runner.Secret);
        Assert.Equal(run.Id, (await ClaimAsync(box)).RunId);
    }

    [Fact]
    public async Task a_runner_whose_root_holds_the_path_hint_claims_without_a_mapping()
    {
        var unmapped = await RegisterRunnerAsync("box-2");
        await HelloAsync(unmapped.Secret, Reports([], []));
        await HelloAsync(Runner.Secret, Reports([], ["/srv/"]));
        var run = await DispatchAsync(ItemKey);

        Assert.Equal(HttpStatusCode.NoContent, await ClaimStatusAsync(unmapped.Secret));
        using var box = RunnerClient(Runner.Secret);
        Assert.Equal(run.Id, (await ClaimAsync(box)).RunId);
    }

    [Fact]
    public async Task a_runner_that_reports_no_mappings_claims_as_before()
    {
        await HelloAsync(Runner.Secret, Reports(null, null));
        var run = await DispatchAsync(ItemKey);
        using var box = RunnerClient(Runner.Secret);
        Assert.Equal(run.Id, (await ClaimAsync(box)).RunId);
    }

    [Fact]
    public async Task dispatch_refuses_a_chosen_runner_without_the_project()
    {
        await HelloAsync(Runner.Secret, Reports(["IDA"], []));
        var response = await Owner.PostAsJsonAsync($"/api/v1/orgs/{Slug}/items/{ItemKey}/runs",
            new DispatchRunRequest(null, null, Runner.Runner.Id), ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.StartsWith($"box-1 has no local repository for {Project.Key}",
            problem.RootElement.GetProperty("errors").GetProperty("runnerId")[0].GetString());
        Assert.Null((await ItemAsync(ItemKey)).ClaimedBy);
    }

    [Fact]
    public async Task a_run_handed_back_for_want_of_a_clone_goes_to_another_runner_and_stays_queued_meanwhile()
    {
        // box-1 is on an older CLI: it claims everything, and finds no clone.
        await HelloAsync(Runner.Secret, Reports(null, null));
        var run = await DispatchAsync(ItemKey);
        using var box = RunnerClient(Runner.Secret);
        Assert.Equal(run.Id, (await ClaimAsync(box)).RunId);

        var unknownReason = await ReleaseAsync(box, run.Id, "bored");
        Assert.Equal(HttpStatusCode.BadRequest, unknownReason.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await ReleaseAsync(box, run.Id, "no-local-repository")).StatusCode);

        // Queued, not failed, and not offered to box-1 again.
        var queued = await RunAsync(run.Id);
        Assert.Equal("queued", queued.Status);
        Assert.Equal("no-runner-for-project", queued.Waiting!.Reason);
        Assert.Equal(HttpStatusCode.NoContent, await ClaimStatusAsync(Runner.Secret));

        var other = await RegisterRunnerAsync("box-2");
        await HelloAsync(other.Secret, Reports([Project.Key], []));
        using var otherClient = RunnerClient(other.Secret);
        Assert.Equal(run.Id, (await ClaimAsync(otherClient)).RunId);
    }

    [Fact]
    public async Task a_runner_that_handed_a_run_back_is_offered_it_again_once_its_mappings_change()
    {
        await HelloAsync(Runner.Secret, Reports([Project.Key], []));
        var run = await DispatchAsync(ItemKey);
        using var box = RunnerClient(Runner.Secret);
        Assert.Equal(run.Id, (await ClaimAsync(box)).RunId);
        Assert.Equal(HttpStatusCode.NoContent, (await ReleaseAsync(box, run.Id, "no-local-repository")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, await ClaimStatusAsync(Runner.Secret));

        // The same report changes nothing; a remapped clone does.
        await HeartbeatAsync(Runner.Secret, Reports([Project.Key], []));
        Assert.Equal(HttpStatusCode.NoContent, await ClaimStatusAsync(Runner.Secret));
        await HeartbeatAsync(Runner.Secret, Reports([Project.Key], ["/srv"]));
        Assert.Equal(run.Id, (await ClaimAsync(box)).RunId);
    }

    [Fact]
    public async Task a_run_sent_to_one_runner_is_not_handed_back()
    {
        await HelloAsync(Runner.Secret, Reports([Project.Key], []));
        var response = await Owner.PostAsJsonAsync($"/api/v1/orgs/{Slug}/items/{ItemKey}/runs",
            new DispatchRunRequest(null, null, Runner.Runner.Id), ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var run = (await response.Content.ReadFromJsonAsync<RunView>(ApiTestContext.Json, Ct))!;
        using var box = RunnerClient(Runner.Secret);
        Assert.Equal(run.Id, (await ClaimAsync(box)).RunId);

        // Nobody else may take it, so the runner runs it and reports what it lacks.
        Assert.Equal(HttpStatusCode.Conflict, (await ReleaseAsync(box, run.Id, "no-local-repository")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await StartedAsync(box, run.Id)).StatusCode);
    }

    [Fact]
    public async Task fleet_mode_sends_the_run_only_to_a_runner_with_the_project()
    {
        await ExecuteSqlAsync($"""
            INSERT INTO billing.subscriptions (id, organization_id, plan, stripe_customer_id, stripe_subscription_id, status,
                cancel_at_period_end, seats_human, seats_agent, created_at, updated_at)
            VALUES (gen_random_uuid(), '{OrganizationId}', 'hosted', 'cus_fleet', 'sub_fleet', 4, false, 1, 0, now(), now())
            """);
        var fleet = await Owner.PutAsJsonAsync($"/api/v1/orgs/{Slug}/fleet", new UpdateFleetRequest(true), ApiTestContext.Json, Ct);
        Assert.True(fleet.IsSuccessStatusCode, await fleet.Content.ReadAsStringAsync(Ct));

        // box-2 has more free slots, so box-1 would leave it the run for the grace period - but
        // box-2 cannot serve the project, so box-1 takes it at once.
        var freer = await RegisterRunnerAsync("box-2");
        await HelloAsync(freer.Secret, Reports([], []) with { MaxParallel = 4 });
        await HelloAsync(Runner.Secret, Reports([Project.Key], []));
        var run = await DispatchAsync(ItemKey);

        Assert.Equal(HttpStatusCode.NoContent, await ClaimStatusAsync(freer.Secret));
        using var box = RunnerClient(Runner.Secret);
        Assert.Equal(run.Id, (await ClaimAsync(box)).RunId);
    }
}

/// <summary>Whether a path hint lies under a runner's roots, as text, without a database.</summary>
[Trait("Category", "Automation")]
public sealed class RunnerProjectMatchTests
{
    [Theory]
    [InlineData("/srv/acme", "/srv", true)]
    [InlineData("/srv/acme/", "/srv/", true)]
    [InlineData("/srv/acme", "/srv/acme", true)]
    [InlineData("/srv/acme", "/sr", false)]
    [InlineData("/opt/acme", "/srv", false)]
    [InlineData("~/src/acme", "~/src", true)]
    [InlineData("~/src/acme", "/home/ana/src", true)]
    [InlineData("~/other/acme", "/home/ana/src", false)]
    [InlineData("~/src/acme", "/srv", null)]
    [InlineData("/home/ana/src/acme", "~/src", null)]
    [InlineData("C:\\src\\acme", "C:/src", true)]
    [InlineData("acme", "/srv", null)]
    public void a_hint_is_under_a_root_when_the_text_says_so(string hint, string root, bool? expected) =>
        Assert.Equal(expected, RunnerProjects.HintUnderRoot(hint, [root]));

    [Fact]
    public void a_runner_serves_what_it_maps_what_its_roots_may_hold_and_anything_when_it_reports_nothing()
    {
        RunnerCapabilities Caps(IReadOnlyList<string>? workspaces, IReadOnlyList<string>? roots) =>
            new(1, [], null, null, null, 1, Workspaces: workspaces, RepoRoots: roots);

        Assert.True(RunnerProjects.CanServe(Caps(null, null), "IDA", null));
        Assert.True(RunnerProjects.CanServe(null, "IDA", null));
        Assert.True(RunnerProjects.CanServe(Caps(["IDA"], null), "IDA", null));
        Assert.False(RunnerProjects.CanServe(Caps(["ida"], []), "IDA", "/srv/ida"));
        Assert.False(RunnerProjects.CanServe(Caps([], ["/srv"]), "IDA", null));
        Assert.True(RunnerProjects.CanServe(Caps([], ["/srv"]), "IDA", "/srv/ida"));
        // Undecidable from here: the runner decides, and hands the run back if it has none.
        Assert.True(RunnerProjects.CanServe(Caps([], ["/srv"]), "IDA", "~/ida"));
    }
}

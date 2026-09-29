using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.Automation.Domain;
using Aictiq.Modules.Automation.Endpoints;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Authorization;

namespace Aictiq.IntegrationTests.Automation;

/// <summary>A run sent to one runner: only that runner takes it, unless the runner is gone.</summary>
[Trait("Category", "Automation")]
public sealed class RequestedRunnerTests(PostgresFixture postgres, GarageFixture garage) : RunTestBase(postgres, garage)
{
    // An idle claim long-polls; a short poll keeps "nothing to claim" from costing 25 s a test.
    protected override Action<IDictionary<string, string?>>? Configure => settings =>
        settings["Automation:PollTimeoutSeconds"] = "2";

    private Task<HttpResponseMessage> DispatchToAsync(string itemKey, Guid runnerId) =>
        Owner.PostAsJsonAsync($"/api/v1/orgs/{Slug}/items/{itemKey}/runs",
            new DispatchRunRequest(null, null, runnerId), ApiTestContext.Json, Ct);

    private async Task<RunView> DispatchedToAsync(string itemKey, Guid runnerId)
    {
        var response = await DispatchToAsync(itemKey, runnerId);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<RunView>(ApiTestContext.Json, Ct))!;
    }

    private async Task<string?> ValidationMessageAsync(HttpResponseMessage response, string field)
    {
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return problem.RootElement.GetProperty("errors").GetProperty(field)[0].GetString();
    }

    [Fact]
    public async Task only_the_requested_runner_claims_the_run_and_others_take_the_rest()
    {
        var other = await RegisterRunnerAsync("box-2");
        var run = await DispatchedToAsync(ItemKey, Runner.Runner.Id);
        Assert.Equal(Runner.Runner.Id, run.RequestedRunnerId);
        Assert.Equal("box-1", run.RequestedRunnerName);

        using var otherClient = RunnerClient(other.Secret);
        Assert.Equal(HttpStatusCode.NoContent, (await ClaimMaybeAsync(otherClient))!.StatusCode);

        // A run queued later for any runner is not held up behind the one sent to box-1.
        var open = await CreateItemAsync("Tidy the footer");
        var anyRun = await DispatchAsync(open.Key);
        Assert.Null(anyRun.RequestedRunnerId);
        Assert.Equal(anyRun.Id, (await ClaimAsync(otherClient)).RunId);

        using var requested = RunnerClient(Runner.Secret);
        Assert.Equal(run.Id, (await ClaimAsync(requested)).RunId);
        Assert.Equal("box-1", (await RunAsync(run.Id)).RunnerName);
    }

    [Fact]
    public async Task a_run_sent_to_a_runner_that_is_disabled_while_it_waits_goes_to_any_runner()
    {
        var other = await RegisterRunnerAsync("box-2");
        var run = await DispatchedToAsync(ItemKey, Runner.Runner.Id);

        (await Owner.PatchAsJsonAsync($"/api/v1/orgs/{Slug}/runners/{Runner.Runner.Id}",
            new UpdateRunnerRequest(null, true), ApiTestContext.Json, Ct)).EnsureSuccessStatusCode();

        using var otherClient = RunnerClient(other.Secret);
        Assert.Equal(run.Id, (await ClaimAsync(otherClient)).RunId);
    }

    [Fact]
    public async Task dispatch_refuses_a_runner_that_cannot_take_the_run()
    {
        var unknown = await DispatchToAsync(ItemKey, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Equal("Choose a runner of this organization.", await ValidationMessageAsync(unknown, "runnerId"));

        var codexOnly = await RegisterRunnerAsync("codex-box");
        using (var codexClient = RunnerClient(codexOnly.Secret))
        {
            var capabilities = new RunnerCapabilities(1, [new RunnerHarness("codex", "1.0")], "linux", "x64", "0.3.0", 1);
            (await codexClient.PostAsJsonAsync("/api/v1/runner/hello",
                new RunnerHelloRequest(capabilities), ApiTestContext.Json, Ct)).EnsureSuccessStatusCode();
        }
        var wrongHarness = await DispatchToAsync(ItemKey, codexOnly.Runner.Id);
        Assert.Equal(HttpStatusCode.BadRequest, wrongHarness.StatusCode);
        Assert.Equal("codex-box does not report claude, which this playbook uses.",
            await ValidationMessageAsync(wrongHarness, "runnerId"));

        (await Owner.PatchAsJsonAsync($"/api/v1/orgs/{Slug}/runners/{Runner.Runner.Id}",
            new UpdateRunnerRequest(null, true), ApiTestContext.Json, Ct)).EnsureSuccessStatusCode();
        var disabled = await DispatchToAsync(ItemKey, Runner.Runner.Id);
        Assert.Equal(HttpStatusCode.BadRequest, disabled.StatusCode);

        // Nothing was claimed by the refused attempts.
        Assert.Null((await ItemAsync(ItemKey)).ClaimedBy);
    }

    [Fact]
    public async Task factory_operators_list_runner_choices_and_other_members_cannot()
    {
        var retired = await RegisterRunnerAsync("old-box");
        (await Owner.DeleteAsync($"/api/v1/orgs/{Slug}/runners/{retired.Runner.Id}", Ct)).EnsureSuccessStatusCode();

        var operatorAuth = await Context.RegisterAsync($"operator-{Guid.NewGuid():N}@test.local");
        using var operatorClient = Context.ClientFor(operatorAuth);
        await AddMemberAsync(operatorAuth.User.Id, OrgRole.Member, true);

        var choices = (await operatorClient.GetFromJsonAsync<List<RunnerChoiceView>>(
            $"/api/v1/orgs/{Slug}/runners/choices", ApiTestContext.Json, Ct))!;
        var choice = Assert.Single(choices);
        Assert.Equal("box-1", choice.Name);
        Assert.False(choice.IsOnline);

        var memberAuth = await Context.RegisterAsync($"member-{Guid.NewGuid():N}@test.local");
        using var member = Context.ClientFor(memberAuth);
        await AddMemberAsync(memberAuth.User.Id, OrgRole.Member, false);
        var refused = await member.GetAsync($"/api/v1/orgs/{Slug}/runners/choices", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(ProblemTypes.FactoryNotPermitted, await ProblemTypeAsync(refused));

        // The full roster stays an Admin's.
        Assert.Equal(HttpStatusCode.Forbidden, (await operatorClient.GetAsync($"/api/v1/orgs/{Slug}/runners", Ct)).StatusCode);
    }
}

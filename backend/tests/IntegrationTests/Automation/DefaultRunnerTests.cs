using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.Automation.Domain;
using Aictiq.Modules.Automation.Endpoints;
using Aictiq.Modules.Tenancy.Endpoints;

namespace Aictiq.IntegrationTests.Automation;

/// <summary>
/// A project's default runner: saved in Factory settings, it takes the project's chat questions,
/// and deleting it sends them back to any free runner.
/// </summary>
[Trait("Category", "Automation")]
public sealed class DefaultRunnerTests(PostgresFixture postgres, GarageFixture garage) : RunTestBase(postgres, garage)
{
    // An idle claim long-polls; a short poll keeps "nothing to claim" from costing 25 s a test.
    protected override Action<IDictionary<string, string?>>? Configure => settings =>
        settings["Automation:PollTimeoutSeconds"] = "2";

    private string SettingsUrl => $"{ProjectBase}/factory-settings";

    private async Task<FactorySettingsView> SettingsAsync() =>
        (await Owner.GetFromJsonAsync<FactorySettingsView>(SettingsUrl, ApiTestContext.Json, Ct))!;

    private async Task<HttpResponseMessage> PutDefaultRunnerAsync(Guid? runnerId)
    {
        var current = await SettingsAsync();
        return await Owner.PutAsJsonAsync(SettingsUrl,
            new UpdateFactorySettingsRequest(current.RepoSource, current.RepoFullName, current.DefaultBranch,
                current.LocalPathHint, current.DefaultAgentId, current.Version, runnerId),
            ApiTestContext.Json, Ct);
    }

    private async Task<ChatView> AskAsync(string question)
    {
        var response = await Owner.PostAsJsonAsync($"{ProjectBase}/chat", new { question }, ApiTestContext.Json, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<ChatView>(ApiTestContext.Json, Ct))!;
    }

    private async Task<string?> ValidationMessageAsync(HttpResponseMessage response, string field)
    {
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return problem.RootElement.GetProperty("errors").GetProperty(field)[0].GetString();
    }

    [Fact]
    public async Task the_default_runner_saves_reloads_and_resets_to_any_free_runner()
    {
        Assert.Null((await SettingsAsync()).DefaultRunnerId);

        var saved = await PutDefaultRunnerAsync(Runner.Runner.Id);
        Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync(Ct));
        Assert.Equal(Runner.Runner.Id, (await SettingsAsync()).DefaultRunnerId);

        (await PutDefaultRunnerAsync(null)).EnsureSuccessStatusCode();
        Assert.Null((await SettingsAsync()).DefaultRunnerId);
    }

    [Fact]
    public async Task a_runner_of_another_organization_or_a_deleted_or_disabled_one_is_refused()
    {
        (await Owner.PostAsJsonAsync("/api/v1/orgs", new CreateOrganizationRequest("Globex", "globex", null, null),
            ApiTestContext.Json, Ct)).EnsureSuccessStatusCode();
        var foreign = await Owner.PostAsJsonAsync("/api/v1/orgs/globex/runners", new CreateRunnerRequest("globex-box"),
            ApiTestContext.Json, Ct);
        Assert.True(foreign.StatusCode == HttpStatusCode.Created, await foreign.Content.ReadAsStringAsync(Ct));
        var foreignRunner = (await foreign.Content.ReadFromJsonAsync<RunnerIssuedView>(ApiTestContext.Json, Ct))!;

        var refused = await PutDefaultRunnerAsync(foreignRunner.Runner.Id);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("Choose an enabled runner of this organization.", await ValidationMessageAsync(refused, "defaultRunnerId"));

        var retired = await RegisterRunnerAsync("old-box");
        (await Owner.DeleteAsync($"/api/v1/orgs/{Slug}/runners/{retired.Runner.Id}", Ct)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.BadRequest, (await PutDefaultRunnerAsync(retired.Runner.Id)).StatusCode);

        (await Owner.PatchAsJsonAsync($"/api/v1/orgs/{Slug}/runners/{Runner.Runner.Id}",
            new UpdateRunnerRequest(null, true), ApiTestContext.Json, Ct)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.BadRequest, (await PutDefaultRunnerAsync(Runner.Runner.Id)).StatusCode);

        Assert.Null((await SettingsAsync()).DefaultRunnerId);
    }

    [Fact]
    public async Task a_chat_question_goes_to_any_free_runner_without_a_default()
    {
        var asked = await AskAsync("Where is the login redirect handled?");
        Assert.Null(asked.RunnerName);
        var runId = Assert.Single(asked.Messages).RunId!.Value;
        Assert.Null((await RunAsync(runId)).RequestedRunnerId);

        var other = await RegisterRunnerAsync("box-2");
        using var otherClient = RunnerClient(other.Secret);
        Assert.Equal(runId, (await ClaimAsync(otherClient)).RunId);
    }

    [Fact]
    public async Task a_chat_question_goes_only_to_the_default_runner()
    {
        var other = await RegisterRunnerAsync("box-2");
        (await PutDefaultRunnerAsync(Runner.Runner.Id)).EnsureSuccessStatusCode();

        var asked = await AskAsync("Where is the login redirect handled?");
        // box-1 has never said hello, so the chat says it is the one that is offline.
        Assert.False(asked.RunnerOnline);
        Assert.Equal("box-1", asked.RunnerName);
        var runId = Assert.Single(asked.Messages).RunId!.Value;
        Assert.Equal(Runner.Runner.Id, (await RunAsync(runId)).RequestedRunnerId);

        using var otherClient = RunnerClient(other.Secret);
        Assert.Equal(HttpStatusCode.NoContent, (await ClaimMaybeAsync(otherClient))!.StatusCode);
        using var defaultClient = RunnerClient(Runner.Secret);
        Assert.Equal(runId, (await ClaimAsync(defaultClient)).RunId);
    }

    [Fact]
    public async Task deleting_the_default_runner_clears_it_and_chat_goes_to_any_free_runner()
    {
        var other = await RegisterRunnerAsync("box-2");
        (await PutDefaultRunnerAsync(Runner.Runner.Id)).EnsureSuccessStatusCode();
        // A question already waiting for box-1 when it is deleted.
        var waiting = Assert.Single((await AskAsync("Where is the login redirect handled?")).Messages).RunId!.Value;

        (await Owner.DeleteAsync($"/api/v1/orgs/{Slug}/runners/{Runner.Runner.Id}", Ct)).EnsureSuccessStatusCode();
        Assert.Null((await SettingsAsync()).DefaultRunnerId);

        using var otherClient = RunnerClient(other.Secret);
        Assert.Equal(waiting, (await ClaimAsync(otherClient)).RunId);
        Assert.Equal(HttpStatusCode.NoContent, (await StartedAsync(otherClient, waiting)).StatusCode);
        (await FinishAsync(otherClient, waiting, "succeeded")).EnsureSuccessStatusCode();

        var asked = await AskAsync("And the logout?");
        Assert.Null(asked.RunnerName);
        var runId = asked.Messages[^1].RunId!.Value;
        Assert.Null((await RunAsync(runId)).RequestedRunnerId);
        Assert.Equal(runId, (await ClaimAsync(otherClient)).RunId);
    }

    private sealed record ChatView(IReadOnlyList<ChatMessageView> Messages, bool RunnerOnline, string? RunnerName);

    private sealed record ChatMessageView(Guid Id, string Status, string? RunStatus, Guid? RunId);
}

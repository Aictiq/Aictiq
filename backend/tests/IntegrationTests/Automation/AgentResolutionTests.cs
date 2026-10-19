using System.Net;
using System.Net.Http.Json;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.Automation.Endpoints;
using Aictiq.Modules.Tenancy.Domain;
using Aictiq.Modules.Tenancy.Endpoints;

namespace Aictiq.IntegrationTests.Automation;

[Trait("Category", "Automation")]
public sealed class AgentResolutionTests(PostgresFixture postgres, GarageFixture garage) : RunTestBase(postgres, garage)
{
    private Task ClearProjectDefaultAsync() => ExecuteSqlAsync(
        $"UPDATE automation.project_settings SET default_agent_id = NULL WHERE project_id = '{Project.Id}'");

    private async Task SetOrganizationDefaultAsync(string id)
    {
        var response = await Owner.PatchAsJsonAsync($"/api/v1/orgs/{Slug}/agents/{id}",
            new UpdateAgentRequest(null, null, true), ApiTestContext.Json, Ct);
        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task the_only_active_visible_agent_is_chosen_without_project_settings()
    {
        await ExecuteSqlAsync($"DELETE FROM automation.project_settings WHERE project_id = '{Project.Id}'");
        var inactive = await CreateAgentAsync("inactive");
        (await Owner.PatchAsJsonAsync($"/api/v1/orgs/{Slug}/agents/{inactive.UserId}",
            new UpdateAgentRequest(null, false), ApiTestContext.Json, Ct)).EnsureSuccessStatusCode();

        Assert.Equal(AgentId, (await DispatchAsync(ItemKey)).AgentId);
    }

    [Fact]
    public async Task organization_default_breaks_a_tie_and_project_and_explicit_defaults_take_precedence()
    {
        var preferred = await CreateAgentAsync("organization-default");
        await SetOrganizationDefaultAsync(preferred.UserId);
        Assert.Equal(AgentId, (await DispatchAsync(ItemKey)).AgentId);
        var explicitItem = await CreateItemAsync("Explicit agent");
        Assert.Equal(preferred.UserId, (await DispatchAsync(explicitItem.Key, agentId: preferred.UserId)).AgentId);

        await ClearProjectDefaultAsync();
        var inheritedItem = await CreateItemAsync("Organization default");
        Assert.Equal(preferred.UserId, (await DispatchAsync(inheritedItem.Key)).AgentId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task an_inaccessible_organization_default_cannot_enter_a_private_project(bool projectChat)
    {
        var privateProject = await CreateProjectAsync("Private", "private", ProjectVisibility.Private);
        var preferred = await CreateAgentAsync("outside-private");
        await SetOrganizationDefaultAsync(preferred.UserId);
        var sole = await CreateAgentAsync("private-builder", [privateProject.Id]);
        (await Owner.PostAsync($"/api/v1/orgs/{Slug}/projects/private/playbooks/starter", null, Ct)).EnsureSuccessStatusCode();
        var item = await CreateItemAsync("Private task", "private");
        var run = projectChat ? await AskProjectAsync("private") : await DispatchAsync(item.Key);
        Assert.Equal(sole.UserId, run.AgentId);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task zero_or_multiple_agents_fail_clearly_without_claiming_the_item(bool multiple, bool projectChat)
    {
        await ClearProjectDefaultAsync();
        if (multiple)
        {
            await CreateAgentAsync("another-agent");
        }
        else
        {
            (await Owner.PatchAsJsonAsync($"/api/v1/orgs/{Slug}/agents/{AgentId}",
                new UpdateAgentRequest(null, false), ApiTestContext.Json, Ct)).EnsureSuccessStatusCode();
        }
        var response = projectChat
            ? await Owner.PostAsJsonAsync($"{ProjectBase}/chat", new { question = "Explain the project" }, ApiTestContext.Json, Ct)
            : await Owner.PostAsJsonAsync($"/api/v1/orgs/{Slug}/items/{ItemKey}/runs",
                new DispatchRunRequest(null, null), ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(multiple ? "Multiple active agents" : "No active agent", await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(0, await ScalarAsync($"SELECT count(*) FROM automation.runs WHERE item_id = '{Item.Id}'"));
        Assert.Null((await ItemAsync(ItemKey)).ClaimedBy);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task an_invalid_explicit_or_project_default_never_falls_back(bool explicitAgent)
    {
        var fallback = await CreateAgentAsync("valid-fallback");
        await SetOrganizationDefaultAsync(fallback.UserId);
        (await Owner.PatchAsJsonAsync($"/api/v1/orgs/{Slug}/agents/{AgentId}",
            new UpdateAgentRequest(null, false), ApiTestContext.Json, Ct)).EnsureSuccessStatusCode();
        var response = await Owner.PostAsJsonAsync($"/api/v1/orgs/{Slug}/items/{ItemKey}/runs",
            new DispatchRunRequest(null, explicitAgent ? AgentId : null), ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("The agent must be an active agent that can see this project.", await response.Content.ReadAsStringAsync(Ct));
    }
    [Fact]
    public async Task project_chat_keeps_the_project_default_ahead_of_the_organization_default()
    {
        var preferred = await CreateAgentAsync("organization-preference");
        await SetOrganizationDefaultAsync(preferred.UserId);
        Assert.Equal(AgentId, (await AskProjectAsync(Project.Key)).AgentId);
    }

    [Fact]
    public async Task project_chat_refuses_an_invalid_project_default_instead_of_using_the_organization_default()
    {
        var fallback = await CreateAgentAsync("valid-chat-fallback");
        await SetOrganizationDefaultAsync(fallback.UserId);
        (await Owner.PatchAsJsonAsync($"/api/v1/orgs/{Slug}/agents/{AgentId}",
            new UpdateAgentRequest(null, false), ApiTestContext.Json, Ct)).EnsureSuccessStatusCode();
        var response = await Owner.PostAsJsonAsync($"{ProjectBase}/chat", new { question = "Explain the project" }, ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("active agent that can see this project", await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(0, await ScalarAsync($"SELECT count(*) FROM automation.runs WHERE project_id = '{Project.Id}'"));
    }

    private async Task<RunView> AskProjectAsync(string projectKey)
    {
        var response = await Owner.PostAsJsonAsync($"/api/v1/orgs/{Slug}/projects/{projectKey}/chat",
            new { question = "Explain the project" }, ApiTestContext.Json, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        using var body = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return await RunAsync(body.RootElement.GetProperty("messages")[0].GetProperty("runId").GetGuid());
    }

}

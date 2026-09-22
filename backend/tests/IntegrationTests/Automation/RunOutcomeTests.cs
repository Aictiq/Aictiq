using System.Net.Http.Headers;
using System.Net.Http.Json;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.Tenancy.Domain;
using Aictiq.Modules.Tenancy.Endpoints;
using Aictiq.Modules.WorkItems.Domain;
using Aictiq.Modules.WorkItems.Endpoints;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Paging;
using Microsoft.Extensions.DependencyInjection;

namespace Aictiq.IntegrationTests.Automation;

[Trait("Category", "RunOutcome")]
[Collection("postgres")]
public sealed class RunOutcomeTests(PostgresFixture postgres, GarageFixture garage) : IAsyncLifetime
{
    private const string Slug = "acme";
    private const string ProjectKey = "web";

    private ApiTestContext _context = null!;
    private HttpClient _owner = null!;
    private Guid _organizationId;
    private ProjectView _project = null!;
    private string _agentId = null!;
    private string _ownerId = null!;
    private string _agentToken = null!;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _context = await ApiTestContext.CreateAsync(postgres, garage, "run_outcomes");

        var alice = await _context.RegisterAsync($"outcome-owner-{Guid.NewGuid():N}@test.local", "Alice", "Anderson");
        _owner = _context.ClientFor(alice);
        _ownerId = alice.User.Id;

        var organization = await _owner.PostAsJsonAsync("/api/v1/orgs",
            new CreateOrganizationRequest("Acme", Slug, null, null), ApiTestContext.Json, Ct);
        organization.EnsureSuccessStatusCode();
        _organizationId = (await organization.Content.ReadFromJsonAsync<OrganizationView>(ApiTestContext.Json, Ct))!.Id;

        var project = await _owner.PostAsJsonAsync($"/api/v1/orgs/{Slug}/projects",
            new CreateProjectRequest("Web", ProjectKey, null, ProjectVisibility.Organization, null, null),
            ApiTestContext.Json, Ct);
        project.EnsureSuccessStatusCode();
        _project = (await project.Content.ReadFromJsonAsync<ProjectView>(ApiTestContext.Json, Ct))!;

        var agent = await _owner.PostAsJsonAsync($"/api/v1/orgs/{Slug}/agents",
            new CreateAgentRequest("builder", [_project.Id]), ApiTestContext.Json, Ct);
        agent.EnsureSuccessStatusCode();
        _agentId = (await agent.Content.ReadFromJsonAsync<AgentView>(ApiTestContext.Json, Ct))!.UserId;

        var issued = await _owner.PostAsJsonAsync($"/api/v1/orgs/{Slug}/agents/{_agentId}/tokens",
            new CreateAgentTokenRequest("ci", [], null), ApiTestContext.Json, Ct);
        issued.EnsureSuccessStatusCode();
        _agentToken = (await issued.Content.ReadFromJsonAsync<AgentTokenIssued>(ApiTestContext.Json, Ct))!.Secret;
    }

    public async ValueTask DisposeAsync()
    {
        _owner.Dispose();
        await _context.DisposeAsync();
    }

    [Fact]
    public async Task success_transitions_and_comments_exactly_once_under_replay()
    {
        var item = await CreateItemAsync("Repair onboarding");
        await ClaimByAgentAsync(item.Key);
        var resolved = await StateIdAsync("Resolved");
        var finished = new RunFinished(_organizationId, _project.Id, item.Id, item.Key, Guid.NewGuid(), _agentId,
            RunOutcomes.Succeeded, resolved, null, "Shipped the fix.", null, null);

        await HandleAsync(finished);
        await HandleAsync(finished);

        var after = await ItemAsync(item.Key);
        Assert.Equal(resolved, after.StateId);
        Assert.Equal(WorkflowStateCategory.Resolved, after.StateCategory);
        Assert.Null(after.ClaimedBy);

        var comments = await CommentsAsync(item.Key);
        Assert.Single(comments.Items);
        Assert.Contains("succeeded", comments.Items[0].BodyMarkdown);
    }

    [Fact]
    public async Task failure_uses_the_playbooks_failure_state_and_reports_the_reason()
    {
        var item = await CreateItemAsync("Break the build");
        await ClaimByAgentAsync(item.Key);
        var inReview = await StateIdAsync("In Review");

        await HandleAsync(new RunFinished(_organizationId, _project.Id, item.Id, item.Key, Guid.NewGuid(), _agentId,
            RunOutcomes.Failed, null, inReview, null, null, "exit code 1: unit tests broke"));

        var after = await ItemAsync(item.Key);
        Assert.Equal(inReview, after.StateId);
        Assert.Equal(WorkflowStateCategory.Active, after.StateCategory);
        Assert.Null(after.ClaimedBy);

        var comments = await CommentsAsync(item.Key);
        Assert.Contains(comments.Items, comment =>
            comment.BodyMarkdown.Contains("failed") && comment.BodyMarkdown.Contains("unit tests broke"));
    }

    [Fact]
    public async Task refused_transition_is_logged_not_thrown()
    {
        await RestrictTransitionsToInitialToFirstActiveAsync();
        var item = await CreateItemAsync("Stuck in active");
        var claimed = await ClaimByAgentAsync(item.Key);
        var activeId = claimed.StateId;
        var resolved = await StateIdAsync("Resolved");

        await HandleAsync(new RunFinished(_organizationId, _project.Id, item.Id, item.Key, Guid.NewGuid(), _agentId,
            RunOutcomes.Succeeded, resolved, null, "Shipped anyway.", null, null));

        var after = await ItemAsync(item.Key);
        Assert.Equal(activeId, after.StateId);
        var history = await _owner.GetStringAsync($"/api/v1/orgs/{Slug}/items/{item.Key}/history", Ct);
        Assert.Contains("auto-transition-skipped", history);
    }

    [Fact]
    public async Task unknown_item_is_a_silent_no_op()
    {
        var item = await CreateItemAsync("Bystander");
        var resolved = await StateIdAsync("Resolved");

        await HandleAsync(new RunFinished(_organizationId, _project.Id, Guid.NewGuid(), "WEB-999", Guid.NewGuid(),
            _agentId, RunOutcomes.Succeeded, resolved, null, "ghost run", null, null));

        var after = await ItemAsync(item.Key);
        Assert.Equal(item.StateId, after.StateId);
        Assert.Null(after.ClaimedBy);
        Assert.Empty((await CommentsAsync(item.Key)).Items);
    }

    [Fact]
    public async Task pull_request_url_is_linked()
    {
        var item = await CreateItemAsync("Link me");
        await ClaimByAgentAsync(item.Key);
        var resolved = await StateIdAsync("Resolved");
        var pullRequestUrl = "https://github.com/acme/web/pull/42";

        await HandleAsync(new RunFinished(_organizationId, _project.Id, item.Id, item.Key, Guid.NewGuid(), _agentId,
            RunOutcomes.Succeeded, resolved, null, "Shipped the fix.", pullRequestUrl, null));

        var links = (await _owner.GetFromJsonAsync<List<ItemLinkView>>(
            $"/api/v1/orgs/{Slug}/items/{item.Key}/links", ApiTestContext.Json, Ct))!;
        Assert.Contains(links, link => link.Url == pullRequestUrl);
    }

    [Fact]
    public async Task an_item_a_run_completed_counts_for_the_agent_even_when_a_person_is_assigned()
    {
        var team = (await _owner.GetFromJsonAsync<List<TeamView>>(
            $"/api/v1/orgs/{Slug}/projects/{ProjectKey}/teams/", ApiTestContext.Json, Ct))![0];
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var sprintResponse = await _owner.PostAsJsonAsync($"/api/v1/orgs/{Slug}/teams/{team.Id}/sprints",
            new CreateSprintRequest("Now", null, today.AddDays(-1), today.AddDays(13)), ApiTestContext.Json, Ct);
        sprintResponse.EnsureSuccessStatusCode();
        var sprint = (await sprintResponse.Content.ReadFromJsonAsync<SprintView>(ApiTestContext.Json, Ct))!;
        (await _owner.PostAsync($"/api/v1/orgs/{Slug}/sprints/{sprint.Id}/start", null, Ct)).EnsureSuccessStatusCode();

        // Dispatched by Alice and still hers: the assignee alone would credit a person.
        var item = await CreateItemAsync("Ship the release notes");
        var planned = await _owner.PostAsJsonAsync($"/api/v1/orgs/{Slug}/projects/{ProjectKey}/items/bulk",
            new BulkUpdateItemsRequest([item.Key], new BulkItemSet(null, _ownerId, null, team.Id, sprint.Id, null, null),
                new Dictionary<string, uint> { [item.Key] = item.Version }), ApiTestContext.Json, Ct);
        Assert.True(planned.IsSuccessStatusCode, await planned.Content.ReadAsStringAsync(Ct));
        await ClaimByAgentAsync(item.Key);

        var closed = await StateIdAsync("Closed");
        await HandleAsync(new RunFinished(_organizationId, _project.Id, item.Id, item.Key, Guid.NewGuid(), _agentId,
            RunOutcomes.Succeeded, closed, null, "Wrote them.", null, null));

        var contribution = (await _owner.GetFromJsonAsync<AgentContribution>(
            $"/api/v1/orgs/{Slug}/agent-activity/contribution", ApiTestContext.Json, Ct))!;
        Assert.Equal(1, contribution.CompletedTotal);
        Assert.Equal(1, contribution.CompletedByAgents);
        var row = Assert.Single(contribution.ByAgent);
        Assert.Equal(_agentId, row.Agent.Id);
        Assert.Equal(1, row.Completed);
    }

    private async Task<WorkItemView> CreateItemAsync(string title)
    {
        var response = await _owner.PostAsJsonAsync($"/api/v1/orgs/{Slug}/projects/{ProjectKey}/items/",
            new CreateWorkItemRequest(WorkItemType.Bug, title, null, null, null, null, null, null, null, null, null, null, null, null),
            ApiTestContext.Json, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<WorkItemView>(ApiTestContext.Json, Ct))!;
    }

    private async Task<WorkItemView> ItemAsync(string itemKey) =>
        (await _owner.GetFromJsonAsync<WorkItemView>($"/api/v1/orgs/{Slug}/items/{itemKey}", ApiTestContext.Json, Ct))!;

    private async Task<WorkItemView> ClaimByAgentAsync(string itemKey)
    {
        var item = await ItemAsync(itemKey);
        using var agent = TokenClient(_agentToken);
        var claimed = await agent.PostAsJsonAsync($"/api/v1/orgs/{Slug}/items/{itemKey}/claim",
            new ClaimRequest(item.Version), ApiTestContext.Json, Ct);
        Assert.True(claimed.IsSuccessStatusCode, await claimed.Content.ReadAsStringAsync(Ct));
        return (await claimed.Content.ReadFromJsonAsync<WorkItemView>(ApiTestContext.Json, Ct))!;
    }

    private HttpClient TokenClient(string secret)
    {
        var client = _context.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        return client;
    }

    private async Task<Guid> StateIdAsync(string name)
    {
        var workflows = (await _owner.GetFromJsonAsync<List<WorkflowView>>(
            $"/api/v1/orgs/{Slug}/projects/{ProjectKey}/workflows/", ApiTestContext.Json, Ct))!;
        return workflows.Single(workflow => workflow.IsDefault).States.Single(state => state.Name == name).Id;
    }

    private async Task RestrictTransitionsToInitialToFirstActiveAsync()
    {
        var workflows = (await _owner.GetFromJsonAsync<List<WorkflowView>>(
            $"/api/v1/orgs/{Slug}/projects/{ProjectKey}/workflows/", ApiTestContext.Json, Ct))!;
        var workflow = workflows.Single(entry => entry.IsDefault);
        var initial = workflow.States.Single(state => state.IsInitial);
        var firstActive = workflow.States.First(state => state.Category == WorkflowStateCategory.Active);
        var replace = new ReplaceWorkflowRequest(
            workflow.Name,
            workflow.States.Select(state =>
                new WorkflowStateInput(state.Id, state.Name, state.Category, state.Position, state.Color, state.IsInitial, null)).ToList(),
            [new WorkflowTransitionInput(initial.Id, firstActive.Id)],
            workflow.Version);
        var response = await _owner.PutAsJsonAsync(
            $"/api/v1/orgs/{Slug}/projects/{ProjectKey}/workflows/{workflow.Id}", replace, ApiTestContext.Json, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    private async Task<PagedResult<CommentView>> CommentsAsync(string itemKey) =>
        (await _owner.GetFromJsonAsync<PagedResult<CommentView>>(
            $"/api/v1/orgs/{Slug}/items/{itemKey}/comments/", ApiTestContext.Json, Ct))!;

    private async Task HandleAsync(RunFinished @event)
    {
        using var scope = _context.Factory.Services.CreateScope();
        var handler = ActivatorUtilities.CreateInstance<Aictiq.Modules.WorkItems.Events.RunFinishedHandler>(scope.ServiceProvider);
        await handler.HandleAsync(@event, Ct);
    }
}

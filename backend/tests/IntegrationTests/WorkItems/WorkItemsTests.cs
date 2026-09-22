using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.Tenancy.Domain;
using Aictiq.Modules.Tenancy.Endpoints;
using Aictiq.Modules.WorkItems.Domain;
using Aictiq.Modules.WorkItems.Endpoints;
using Aictiq.SharedKernel.Paging;

namespace Aictiq.IntegrationTests.WorkItems;

/// <summary>
/// HTTP-level coverage for the first three WorkItems tickets. Each test owns a project so
/// numbering, workflow state, and hierarchy assertions are never coupled to another test.
/// </summary>
[Trait("Category", "WorkItems")]
[Collection("postgres")]
public sealed class WorkflowTests(PostgresFixture postgres, GarageFixture garage) : WorkItemsTestBase(postgres, garage)
{
    [Fact]
    public async Task a_fresh_project_has_the_default_workflow_and_required_categories()
    {
        var workflows = await WorkflowsAsync();

        var workflow = Assert.Single(workflows);
        Assert.True(workflow.IsDefault);
        Assert.Equal("Default", workflow.Name);
        Assert.Collection(workflow.States.OrderBy(s => s.Position),
            state => AssertState(state, "New", WorkflowStateCategory.Proposed, initial: true),
            state => AssertState(state, "Active", WorkflowStateCategory.Active),
            state => AssertState(state, "In Review", WorkflowStateCategory.Active),
            state => AssertState(state, "Resolved", WorkflowStateCategory.Resolved),
            state => AssertState(state, "Closed", WorkflowStateCategory.Completed),
            state => AssertState(state, "Removed", WorkflowStateCategory.Removed));
    }

    [Fact]
    public async Task workflow_replacement_requires_required_categories_and_a_replacement_for_an_in_use_state()
    {
        var workflow = Assert.Single(await WorkflowsAsync());
        var active = workflow.States.Single(s => s.Name == "Active");
        var inReview = workflow.States.Single(s => s.Name == "In Review");
        var item = await CreateAsync(WorkItemType.Bug, "State consumer");

        var moved = await Client.PostAsJsonAsync(ItemPath(item.Key, "transition"),
            new TransitionRequest(active.Id, item.Version), ApiTestContext.Json, CancellationToken);
        moved.EnsureSuccessStatusCode();

        var withoutCompleted = await Client.PutAsJsonAsync(WorkflowPath(workflow.Id),
            new ReplaceWorkflowRequest(workflow.Name,
                workflow.States.Where(s => s.Category != WorkflowStateCategory.Completed).Select(ToInput).ToList(),
                [], workflow.Version), ApiTestContext.Json, CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, withoutCompleted.StatusCode);

        var noReplacement = await Client.PutAsJsonAsync(WorkflowPath(workflow.Id),
            new ReplaceWorkflowRequest(workflow.Name,
                workflow.States.Where(s => s.Id != active.Id).Select(ToInput).ToList(),
                [], workflow.Version), ApiTestContext.Json, CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, noReplacement.StatusCode);

        var replacement = await Client.PutAsJsonAsync(WorkflowPath(workflow.Id),
            new ReplaceWorkflowRequest(workflow.Name,
                workflow.States.Where(s => s.Id != active.Id)
                    .Select(s => s.Id == inReview.Id ? ToInput(s) with { ReplacementStateId = active.Id } : ToInput(s)).ToList(),
                [], workflow.Version), ApiTestContext.Json, CancellationToken);
        replacement.EnsureSuccessStatusCode();
    }

    private static WorkflowStateInput ToInput(WorkflowStateView state) =>
        new(state.Id, state.Name, state.Category, state.Position, state.Color, state.IsInitial, null);

    private static void AssertState(WorkflowStateView state, string name, WorkflowStateCategory category, bool initial = false)
    {
        Assert.Equal(name, state.Name);
        Assert.Equal(category, state.Category);
        Assert.Equal(initial, state.IsInitial);
    }
}

[Trait("Category", "WorkItems")]
[Collection("postgres")]
public sealed class WorkItemCoreTests(PostgresFixture postgres, GarageFixture garage) : WorkItemsTestBase(postgres, garage)
{
    [Fact]
    public async Task time_logging_initializes_remaining_hours_updates_totals_and_writes_one_history_event()
    {
        var story = await CreateAsync(WorkItemType.Epic, "Epic");
        var feature = await CreateAsync(WorkItemType.Feature, "Feature", parentId: story.Id);
        var parent = await CreateAsync(WorkItemType.Story, "Story", parentId: feature.Id);
        var task = await CreateAsync(WorkItemType.Task, "Timed task", parentId: parent.Id, estimateHours: 5);

        Assert.Equal(5, task.RemainingHours);
        var response = await Client.PostAsJsonAsync(ItemPath(task.Key, "log-time"), new LogTimeRequest(2, task.Version), ApiTestContext.Json, CancellationToken);
        response.EnsureSuccessStatusCode();
        var logged = (await response.Content.ReadFromJsonAsync<WorkItemView>(ApiTestContext.Json, CancellationToken))!;
        Assert.Equal(3, logged.RemainingHours);
        Assert.Equal(2, logged.CompletedHours);

        var historyResponse = await Client.GetAsync(ItemPath(task.Key, "history"), CancellationToken);
        Assert.True(historyResponse.IsSuccessStatusCode, await historyResponse.Content.ReadAsStringAsync(CancellationToken));
        var history = await historyResponse.Content.ReadFromJsonAsync<PagedResult<ItemHistoryEventView>>(ApiTestContext.Json, CancellationToken);
        Assert.Single(history!.Items);
        Assert.Equal("log-time", Assert.Single(history.Items[0].Changes).Field);
    }

    [Fact]
    public async Task completing_a_task_sets_remaining_hours_to_zero_and_hours_filters_are_supported()
    {
        var epic = await CreateAsync(WorkItemType.Epic, "Epic");
        var feature = await CreateAsync(WorkItemType.Feature, "Feature", parentId: epic.Id);
        var story = await CreateAsync(WorkItemType.Story, "Story", parentId: feature.Id);
        var task = await CreateAsync(WorkItemType.Task, "Timed task", parentId: story.Id, estimateHours: 4);
        var closed = (await WorkflowsAsync()).Single().States.Single(s => s.Category == WorkflowStateCategory.Completed);

        var completion = await Client.PostAsJsonAsync(ItemPath(task.Key, "transition"), new TransitionRequest(closed.Id, task.Version), ApiTestContext.Json, CancellationToken);
        completion.EnsureSuccessStatusCode();
        Assert.Equal(0, (await completion.Content.ReadFromJsonAsync<WorkItemView>(ApiTestContext.Json, CancellationToken))!.RemainingHours);

        var noneResponse = await Client.GetAsync($"/api/v1/orgs/work-items/projects/{Project.Key}/items/?filter=estimate:none", CancellationToken);
        Assert.True(noneResponse.IsSuccessStatusCode, await noneResponse.Content.ReadAsStringAsync(CancellationToken));
        var none = await noneResponse.Content.ReadFromJsonAsync<PagedResult<WorkItemView>>(ApiTestContext.Json, CancellationToken);
        Assert.DoesNotContain(none!.Items, item => item.Key == task.Key);
        var remaining = await Client.GetFromJsonAsync<PagedResult<WorkItemView>>($"/api/v1/orgs/work-items/projects/{Project.Key}/items/?filter=remaining:>=0", ApiTestContext.Json, CancellationToken);
        Assert.Contains(remaining!.Items, item => item.Key == task.Key);
    }

    [Fact]
    public async Task concurrent_creates_receive_each_project_number_once_without_gaps()
    {
        var creates = Enumerable.Range(1, 50)
            .Select(i => CreateAsync(WorkItemType.Bug, $"Concurrent item {i}"));

        var items = await Task.WhenAll(creates);

        Assert.Equal(50, items.Select(item => item.Id).Distinct().Count());
        Assert.Equal(Enumerable.Range(1, 50), items.Select(item => int.Parse(item.Key[(item.Key.LastIndexOf('-') + 1)..])).Order());
    }

    [Fact]
    public async Task transition_whitelist_and_compare_and_swap_allow_only_one_racer_to_move_an_item()
    {
        var workflow = Assert.Single(await WorkflowsAsync());
        var proposed = workflow.States.Single(s => s.Category == WorkflowStateCategory.Proposed);
        var active = workflow.States.Single(s => s.Name == "Active");
        var closed = workflow.States.Single(s => s.Category == WorkflowStateCategory.Completed);
        var configured = await Client.PutAsJsonAsync(WorkflowPath(workflow.Id),
            new ReplaceWorkflowRequest(workflow.Name, workflow.States.Select(ToInput).ToList(),
                [new WorkflowTransitionInput(proposed.Id, active.Id), new WorkflowTransitionInput(active.Id, closed.Id)], workflow.Version),
            ApiTestContext.Json, CancellationToken);
        configured.EnsureSuccessStatusCode();

        var item = await CreateAsync(WorkItemType.Bug, "CAS transition");
        var blocked = await Client.PostAsJsonAsync(ItemPath(item.Key, "transition"),
            new TransitionRequest(closed.Id, item.Version), ApiTestContext.Json, CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);

        var enteredActive = await Client.PostAsJsonAsync(ItemPath(item.Key, "transition"),
            new TransitionRequest(active.Id, item.Version), ApiTestContext.Json, CancellationToken);
        enteredActive.EnsureSuccessStatusCode();
        var activeItem = (await enteredActive.Content.ReadFromJsonAsync<WorkItemView>(ApiTestContext.Json, CancellationToken))!;

        var racers = await Task.WhenAll(
            Client.PostAsJsonAsync(ItemPath(item.Key, "transition"), new TransitionRequest(closed.Id, activeItem.Version), ApiTestContext.Json, CancellationToken),
            Client.PostAsJsonAsync(ItemPath(item.Key, "transition"), new TransitionRequest(closed.Id, activeItem.Version), ApiTestContext.Json, CancellationToken));

        Assert.Equal(1, racers.Count(response => response.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, racers.Count(response => response.StatusCode == HttpStatusCode.Conflict));
    }

    [Fact]
    public async Task list_filter_is_applied_within_the_requested_project()
    {
        await CreateAsync(WorkItemType.Epic, "Roadmap");
        var bug = await CreateAsync(WorkItemType.Bug, "Visible bug");

        var page = await Client.GetFromJsonAsync<PagedResult<WorkItemView>>(
            $"/api/v1/orgs/work-items/projects/{Project.Key}/items/?filter=type:bug", ApiTestContext.Json, CancellationToken);

        Assert.Equal(1, page!.TotalCount);
        Assert.Equal(bug.Key, Assert.Single(page.Items).Key);
    }

    [Theory]
    [InlineData("type:bug", WorkItemType.Bug)]
    [InlineData("type:Story", WorkItemType.Story)]
    public void filter_parser_accepts_item_type_case_insensitively(string text, WorkItemType expected)
    {
        var filter = ItemFilter.Parse(text);

        Assert.Null(filter.Error);
        Assert.Equal(expected, filter.Type);
    }

    [Theory]
    [InlineData("wrong:thing")]
    [InlineData("type:not-a-type")]
    [InlineData("state:not-a-category")]
    public void filter_parser_returns_a_client_error_for_invalid_terms(string text)
    {
        Assert.NotNull(ItemFilter.Parse(text).Error);
    }

    private static WorkflowStateInput ToInput(WorkflowStateView state) =>
        new(state.Id, state.Name, state.Category, state.Position, state.Color, state.IsInitial, null);
}

[Trait("Category", "WorkItems")]
[Collection("postgres")]
public sealed class WorkItemHierarchyTests(PostgresFixture postgres, GarageFixture garage) : WorkItemsTestBase(postgres, garage)
{
    [Fact]
    public async Task hierarchy_endpoints_enforce_the_matrix_prevent_cycles_and_compute_direct_child_rollups()
    {
        var epic = await CreateAsync(WorkItemType.Epic, "Epic");
        var feature = await CreateAsync(WorkItemType.Feature, "Feature", parentId: epic.Id);
        var story = await CreateAsync(WorkItemType.Story, "Story", parentId: feature.Id, points: 8);
        var task = await CreateAsync(WorkItemType.Task, "Task", parentId: story.Id, remainingHours: 13);

        var badParent = await Client.PostAsJsonAsync(ItemPath(feature.Key, "reparent"),
            new ReparentRequest(story.Key, feature.Version), ApiTestContext.Json, CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, badParent.StatusCode);

        var cycle = await Client.PostAsJsonAsync(ItemPath(epic.Key, "reparent"),
            new ReparentRequest(story.Key, epic.Version), ApiTestContext.Json, CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, cycle.StatusCode);

        var children = await Client.GetFromJsonAsync<List<WorkItemView>>(ItemPath(story.Key, "children"), ApiTestContext.Json, CancellationToken);
        Assert.Equal(task.Key, Assert.Single(children!).Key);
        var ancestors = await Client.GetFromJsonAsync<List<WorkItemView>>(ItemPath(task.Key, "ancestors"), ApiTestContext.Json, CancellationToken);
        Assert.Equal([story.Key, feature.Key, epic.Key], ancestors!.Select(item => item.Key));

        var storyAfterChildren = await Client.GetFromJsonAsync<WorkItemView>(ItemPath(story.Key), ApiTestContext.Json, CancellationToken);
        Assert.Equal(1, storyAfterChildren!.Rollup.TotalCount);
        Assert.Equal(13, storyAfterChildren.Rollup.RemainingHours);
    }

    [Fact]
    public async Task stories_and_bugs_may_stand_alone_or_sit_directly_under_an_epic()
    {
        var epic = await CreateAsync(WorkItemType.Epic, "Epic");
        var looseStory = await CreateAsync(WorkItemType.Story, "Loose story");
        var looseBug = await CreateAsync(WorkItemType.Bug, "Loose bug");
        var epicStory = await CreateAsync(WorkItemType.Story, "Epic story", parentId: epic.Id);
        var epicBug = await CreateAsync(WorkItemType.Bug, "Epic bug", parentId: epic.Id);
        var bugTask = await CreateAsync(WorkItemType.Task, "Bug task", parentId: looseBug.Id);

        Assert.Null(looseStory.ParentId);
        Assert.Equal(epic.Id, epicStory.ParentId);
        Assert.Equal(epic.Id, epicBug.ParentId);
        Assert.Equal(looseBug.Id, bugTask.ParentId);

        foreach (var type in new[] { WorkItemType.Feature, WorkItemType.Task })
        {
            var orphan = await Client.PostAsJsonAsync($"/api/v1/orgs/work-items/projects/{Project.Key}/items/",
                new CreateWorkItemRequest(type, "Orphan", null, null, null, null, null, null, null, null, null, null, null, null),
                ApiTestContext.Json, CancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, orphan.StatusCode);
        }

        var storyUnderStory = await Client.PostAsJsonAsync($"/api/v1/orgs/work-items/projects/{Project.Key}/items/",
            new CreateWorkItemRequest(WorkItemType.Story, "Nested story", null, null, null, null, null, looseStory.Id, null, null, null, null, null, null),
            ApiTestContext.Json, CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, storyUnderStory.StatusCode);
    }

    [Fact]
    public async Task database_trigger_rejects_a_raw_insert_that_violates_the_parent_type_matrix()
    {
        var epic = await CreateAsync(WorkItemType.Epic, "Epic");
        var feature = await CreateAsync(WorkItemType.Feature, "Feature", parentId: epic.Id);
        var story = await CreateAsync(WorkItemType.Story, "Story", parentId: feature.Id);
        var state = (await WorkflowsAsync()).Single().States.Single(s => s.IsInitial);

        await using var connection = new NpgsqlConnection(Context.ConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO work.items (id, organization_id, project_id, project_key, number, type, title,
                description_markdown, description_html, state_id, priority, parent_id, created_by, created_at, updated_at)
            VALUES (@id, @organizationId, @projectId, @projectKey, 999, @type, 'Illegal feature', '', '',
                @stateId, 0, @parentId, @createdBy, now(), now())
            """, connection);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("organizationId", Organization.Id);
        command.Parameters.AddWithValue("projectId", Project.Id);
        command.Parameters.AddWithValue("projectKey", Project.Key);
        command.Parameters.AddWithValue("type", (short)WorkItemType.Feature);
        command.Parameters.AddWithValue("stateId", state.Id);
        command.Parameters.AddWithValue("parentId", story.Id);
        command.Parameters.AddWithValue("createdBy", UserId);

        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync(CancellationToken));
        Assert.Equal("P0001", error.SqlState);
        Assert.Equal("invalid_parent", error.MessageText);
    }
}

public abstract class WorkItemsTestBase : IAsyncLifetime
{
    protected WorkItemsTestBase(PostgresFixture postgres, GarageFixture garage)
    {
        Postgres = postgres;
        Garage = garage;
    }

    private PostgresFixture Postgres { get; }
    private GarageFixture Garage { get; }
    protected ApiTestContext Context { get; private set; } = null!;
    protected HttpClient Client { get; private set; } = null!;
    protected OrganizationView Organization { get; private set; } = null!;
    protected ProjectView Project { get; private set; } = null!;
    protected string UserId { get; private set; } = null!;
    protected CancellationToken CancellationToken => TestContext.Current.CancellationToken;
    /// <summary>Serve requests as the RLS-bound app role (see <see cref="ApiTestContext.CreateAsync"/>).</summary>
    protected virtual bool AppRole => false;

    public async ValueTask InitializeAsync()
    {
        Context = await ApiTestContext.CreateAsync(Postgres, Garage, "work_items", appRole: AppRole);
        var auth = await Context.RegisterAsync($"work-items-{Guid.NewGuid():N}@test.local");
        UserId = auth.User.Id;
        Client = Context.ClientFor(auth);

        var organization = await Client.PostAsJsonAsync("/api/v1/orgs",
            new CreateOrganizationRequest("Work item tests", "work-items", null, null), ApiTestContext.Json, CancellationToken);
        organization.EnsureSuccessStatusCode();
        Organization = (await organization.Content.ReadFromJsonAsync<OrganizationView>(ApiTestContext.Json, CancellationToken))!;

        var project = await Client.PostAsJsonAsync("/api/v1/orgs/work-items/projects",
            new CreateProjectRequest("Work item project", "WIP", null, ProjectVisibility.Organization, null, null), ApiTestContext.Json, CancellationToken);
        project.EnsureSuccessStatusCode();
        Project = (await project.Content.ReadFromJsonAsync<ProjectView>(ApiTestContext.Json, CancellationToken))!;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await Context.DisposeAsync();
    }

    protected async Task<List<WorkflowView>> WorkflowsAsync() =>
        (await Client.GetFromJsonAsync<List<WorkflowView>>($"/api/v1/orgs/work-items/projects/{Project.Key}/workflows/", ApiTestContext.Json, CancellationToken))!;

    protected async Task<WorkItemView> CreateAsync(WorkItemType type, string title, Guid? parentId = null,
        decimal? points = null, decimal? remainingHours = null, decimal? estimateHours = null)
    {
        var response = await Client.PostAsJsonAsync($"/api/v1/orgs/work-items/projects/{Project.Key}/items/",
            new CreateWorkItemRequest(type, title, null, null, null, null, null, parentId, points, estimateHours, remainingHours, null, null, null),
            ApiTestContext.Json, CancellationToken);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(CancellationToken));
        return (await response.Content.ReadFromJsonAsync<WorkItemView>(ApiTestContext.Json, CancellationToken))!;
    }

    protected string WorkflowPath(Guid workflowId) =>
        $"/api/v1/orgs/work-items/projects/{Project.Key}/workflows/{workflowId}";

    protected static string ItemPath(string key, string? suffix = null) =>
        $"/api/v1/orgs/work-items/items/{key}{(suffix is null ? "" : $"/{suffix}")}";
}

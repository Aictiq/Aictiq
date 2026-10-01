using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.WorkItems.Domain;
using Aictiq.Modules.WorkItems.Endpoints;
using Aictiq.SharedKernel.Paging;

namespace Aictiq.IntegrationTests.WorkItems;

[Trait("Category", "WorkItems")]
public sealed class TemplatesAndBulkEditTests(PostgresFixture postgres, GarageFixture garage) : WorkItemsTestBase(postgres, garage)
{
    protected override bool AppRole => true;

    [Fact]
    public async Task templates_include_one_default_per_seeded_type()
    {
        var templates = await Client.GetFromJsonAsync<List<ItemTemplateView>>(TemplatesPath(), ApiTestContext.Json, CancellationToken);

        Assert.Contains(templates!, template => template.Type == WorkItemType.Bug && template.IsDefault && template.DescriptionMarkdown.Contains("Steps to reproduce"));
        Assert.Contains(templates!, template => template.Type == WorkItemType.Story && template.IsDefault && template.DescriptionMarkdown.Contains("Acceptance criteria"));
    }

    [Fact]
    public async Task renaming_a_seeded_template_persists_without_recreating_the_original()
    {
        var templates = await TemplatesAsync();
        var story = Assert.Single(templates, template => template.Type == WorkItemType.Story);
        var response = await Client.PatchAsJsonAsync($"{TemplatesPath()}{story.Id}",
            new UpdateItemTemplateRequest("Team story", "## Team acceptance criteria", [], WorkItemPriority.High, true, story.Version),
            ApiTestContext.Json, CancellationToken);
        response.EnsureSuccessStatusCode();

        var reloaded = await TemplatesAsync();
        Assert.Equal(templates.Count, reloaded.Count);
        Assert.DoesNotContain(reloaded, template => template.Name == "User story");
        var renamed = Assert.Single(reloaded, template => template.Id == story.Id);
        Assert.Equal("Team story", renamed.Name);
        Assert.Equal("## Team acceptance criteria", renamed.DescriptionMarkdown);
        Assert.Equal(WorkItemPriority.High, renamed.DefaultPriority);
    }

    [Fact]
    public async Task deleting_a_seeded_template_persists_after_reloading()
    {
        var templates = await TemplatesAsync();
        var bug = Assert.Single(templates, template => template.Type == WorkItemType.Bug);
        var response = await Client.DeleteAsync($"{TemplatesPath()}{bug.Id}", CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var reloaded = await TemplatesAsync();
        Assert.Equal(templates.Count - 1, reloaded.Count);
        Assert.DoesNotContain(reloaded, template => template.Type == WorkItemType.Bug);
    }

    [Fact]
    public async Task deleting_every_template_leaves_the_project_without_templates()
    {
        foreach (var template in await TemplatesAsync())
        {
            var response = await Client.DeleteAsync($"{TemplatesPath()}{template.Id}", CancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        Assert.Empty(await TemplatesAsync());
        Assert.Empty(await TemplatesAsync());
        // Initializing templates must not consume or reset an item number.
        Assert.EndsWith("-1", (await CreateAsync(WorkItemType.Bug, "No template")).Key);
    }

    [Fact]
    public async Task concurrent_initial_reads_seed_only_one_set_of_templates()
    {
        var lists = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => TemplatesAsync()));
        foreach (var list in lists)
        {
            Assert.Equal(2, list.Count);
            Assert.Single(list, template => template.Type == WorkItemType.Bug && template.IsDefault);
            Assert.Single(list, template => template.Type == WorkItemType.Story && template.IsDefault);
        }
    }

    [Fact]
    public async Task bulk_update_keeps_successes_when_a_selected_item_has_a_stale_version_and_writes_history()
    {
        var first = await CreateAsync(WorkItemType.Bug, "First");
        var stale = await CreateAsync(WorkItemType.Bug, "Stale");
        var changed = await Client.PatchAsJsonAsync(ItemPath(stale.Key),
            new UpdateWorkItemRequest(null, "New title", null, null, null, null, null, null, null, null, null, null, null, null, stale.Version), ApiTestContext.Json, CancellationToken);
        changed.EnsureSuccessStatusCode();

        var response = await Client.PostAsJsonAsync(BulkPath(),
            new BulkUpdateItemsRequest([first.Key, stale.Key], new BulkItemSet(null, null, WorkItemPriority.High, null, null, null, null),
                new Dictionary<string, uint> { [first.Key] = first.Version, [stale.Key] = stale.Version }), ApiTestContext.Json, CancellationToken);
        response.EnsureSuccessStatusCode();
        var result = (await response.Content.ReadFromJsonAsync<BulkUpdateItemsResponse>(ApiTestContext.Json, CancellationToken))!;

        Assert.Equal(HttpStatusCode.OK, (HttpStatusCode)result.Results.Single(x => x.Key == first.Key).Status);
        Assert.Equal(HttpStatusCode.Conflict, (HttpStatusCode)result.Results.Single(x => x.Key == stale.Key).Status);
        Assert.Equal(WorkItemPriority.High, (await GetItemAsync(first.Key)).Priority);
        var history = await Client.GetFromJsonAsync<PagedResult<ItemHistoryEventView>>(ItemPath(first.Key, "history"), ApiTestContext.Json, CancellationToken);
        Assert.Contains(history!.Items.SelectMany(x => x.Changes), change => change.Field == "priority");
    }

    [Fact]
    public async Task bulk_state_changes_apply_the_transition_whitelist_per_item()
    {
        var workflow = Assert.Single(await WorkflowsAsync());
        var proposed = workflow.States.Single(x => x.IsInitial);
        var active = workflow.States.Single(x => x.Name == "Active");
        var closed = workflow.States.Single(x => x.Category == WorkflowStateCategory.Completed);
        var configured = await Client.PutAsJsonAsync(WorkflowPath(workflow.Id),
            new ReplaceWorkflowRequest(workflow.Name, workflow.States.Select(ToInput).ToList(),
                [new WorkflowTransitionInput(proposed.Id, active.Id), new WorkflowTransitionInput(active.Id, closed.Id)], workflow.Version), ApiTestContext.Json, CancellationToken);
        configured.EnsureSuccessStatusCode();
        var activeItem = await CreateAsync(WorkItemType.Bug, "Active");
        var proposedItem = await CreateAsync(WorkItemType.Bug, "Proposed");
        var enteredActive = await Client.PostAsJsonAsync(ItemPath(activeItem.Key, "transition"), new TransitionRequest(active.Id, activeItem.Version), ApiTestContext.Json, CancellationToken);
        activeItem = (await enteredActive.Content.ReadFromJsonAsync<WorkItemView>(ApiTestContext.Json, CancellationToken))!;

        var response = await Client.PostAsJsonAsync(BulkPath(),
            new BulkUpdateItemsRequest([activeItem.Key, proposedItem.Key], new BulkItemSet(closed.Id, null, null, null, null, null, null),
                new Dictionary<string, uint> { [activeItem.Key] = activeItem.Version, [proposedItem.Key] = proposedItem.Version }), ApiTestContext.Json, CancellationToken);
        var result = (await response.Content.ReadFromJsonAsync<BulkUpdateItemsResponse>(ApiTestContext.Json, CancellationToken))!;

        Assert.Equal(HttpStatusCode.OK, (HttpStatusCode)result.Results.Single(x => x.Key == activeItem.Key).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (HttpStatusCode)result.Results.Single(x => x.Key == proposedItem.Key).Status);
        Assert.Equal(closed.Id, (await GetItemAsync(activeItem.Key)).StateId);
        Assert.Equal(proposed.Id, (await GetItemAsync(proposedItem.Key)).StateId);
    }

    private string TemplatesPath() => $"/api/v1/orgs/work-items/projects/{Project.Key}/templates/";
    private async Task<List<ItemTemplateView>> TemplatesAsync() =>
        (await Client.GetFromJsonAsync<List<ItemTemplateView>>(TemplatesPath(), ApiTestContext.Json, CancellationToken))!;
    private string BulkPath() => $"/api/v1/orgs/work-items/projects/{Project.Key}/items/bulk";
    private async Task<WorkItemView> GetItemAsync(string key) =>
        (await Client.GetFromJsonAsync<WorkItemView>(ItemPath(key), ApiTestContext.Json, CancellationToken))!;
    private static WorkflowStateInput ToInput(WorkflowStateView state) =>
        new(state.Id, state.Name, state.Category, state.Position, state.Color, state.IsInitial, null);
}

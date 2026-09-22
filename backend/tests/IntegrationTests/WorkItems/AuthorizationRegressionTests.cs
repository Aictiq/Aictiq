using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.Tenancy.Domain;
using Aictiq.Modules.Tenancy.Endpoints;
using Aictiq.Modules.WorkItems.Domain;
using Aictiq.Modules.WorkItems.Endpoints;
using Aictiq.SharedKernel;

namespace Aictiq.IntegrationTests.WorkItems;

/// <summary>
/// Object-level checks the security review found missing: ids that arrive in a request body
/// (assignee, team, sprint, workflow state) are requests, not grants, and have to be
/// resolved against the project the item belongs to.
/// </summary>
[Trait("Category", "WorkItems")]
[Collection("postgres")]
public sealed class AuthorizationRegressionTests(PostgresFixture postgres, GarageFixture garage) : WorkItemsTestBase(postgres, garage)
{
    [Fact]
    public async Task an_item_cannot_be_assigned_to_an_account_outside_the_project()
    {
        var stranger = await Context.RegisterAsync($"stranger-{Guid.NewGuid():N}@test.local", "Stranger");

        var refused = await Client.PostAsJsonAsync($"/api/v1/orgs/work-items/projects/{Project.Key}/items/",
            new CreateWorkItemRequest(WorkItemType.Bug, "Assigned to nobody here", null, null, null, stranger.User.Id, null, null, null, null, null, null, null, null),
            ApiTestContext.Json, CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var problem = await refused.Content.ReadFromJsonAsync<ValidationProblemDetails>(ApiTestContext.Json, CancellationToken);
        Assert.Contains("assigneeId", problem!.Errors.Keys);

        var item = await CreateAsync(WorkItemType.Bug, "Reassigned later");
        var patched = await Client.PatchAsJsonAsync(ItemPath(item.Key),
            new UpdateWorkItemRequest(null, null, null, null, null, stranger.User.Id, null, null, null, null, null, null, null, null, item.Version),
            ApiTestContext.Json, CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, patched.StatusCode);

        var bulk = await Client.PostAsJsonAsync($"/api/v1/orgs/work-items/projects/{Project.Key}/items/bulk",
            new BulkUpdateItemsRequest([item.Key], new BulkItemSet(null, stranger.User.Id, null, null, null, null, null),
                new Dictionary<string, uint> { [item.Key] = item.Version }), ApiTestContext.Json, CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, bulk.StatusCode);

        // The positive control: a member of the project is assignable.
        var self = await Client.PatchAsJsonAsync(ItemPath(item.Key),
            new UpdateWorkItemRequest(null, null, null, null, null, UserId, null, null, null, null, null, null, null, null, item.Version),
            ApiTestContext.Json, CancellationToken);
        Assert.True(self.IsSuccessStatusCode, await self.Content.ReadAsStringAsync(CancellationToken));
    }

    [Fact]
    public async Task a_team_or_sprint_from_another_project_is_refused()
    {
        var other = await CreateProjectAsync("OTH");
        var foreignTeam = (await Client.GetFromJsonAsync<List<TeamView>>($"/api/v1/orgs/work-items/projects/{other.Key}/teams/", ApiTestContext.Json, CancellationToken))![0];
        var foreignSprint = await Client.PostAsJsonAsync($"/api/v1/orgs/work-items/teams/{foreignTeam.Id}/sprints",
            new CreateSprintRequest("Foreign", null, new DateOnly(2030, 1, 1), new DateOnly(2030, 1, 15)), ApiTestContext.Json, CancellationToken);
        foreignSprint.EnsureSuccessStatusCode();
        var sprint = (await foreignSprint.Content.ReadFromJsonAsync<SprintView>(ApiTestContext.Json, CancellationToken))!;

        var create = await Client.PostAsJsonAsync($"/api/v1/orgs/work-items/projects/{Project.Key}/items/",
            new CreateWorkItemRequest(WorkItemType.Bug, "Wrong team", null, null, null, null, foreignTeam.Id, null, null, null, null, null, null, null),
            ApiTestContext.Json, CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);

        var item = await CreateAsync(WorkItemType.Bug, "Moved to the wrong team");
        var patched = await Client.PatchAsJsonAsync(ItemPath(item.Key),
            new UpdateWorkItemRequest(null, null, null, null, null, null, foreignTeam.Id, null, null, null, null, null, null, null, item.Version),
            ApiTestContext.Json, CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, patched.StatusCode);

        var moved = await Client.PostAsJsonAsync(ItemPath(item.Key, "move"),
            new MoveItemRequest(null, null, sprint.Id, foreignTeam.Id, null, item.Version), ApiTestContext.Json, CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, moved.StatusCode);

        var bulk = await Client.PostAsJsonAsync($"/api/v1/orgs/work-items/projects/{Project.Key}/items/bulk",
            new BulkUpdateItemsRequest([item.Key], new BulkItemSet(null, null, null, foreignTeam.Id, null, null, null),
                new Dictionary<string, uint> { [item.Key] = item.Version }), ApiTestContext.Json, CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, bulk.StatusCode);
    }

    [Fact]
    public async Task item_scoped_writes_on_an_archived_project_answer_409()
    {
        var archived = await CreateProjectAsync("ARC");
        var created = await Client.PostAsJsonAsync($"/api/v1/orgs/work-items/projects/{archived.Key}/items/",
            new CreateWorkItemRequest(WorkItemType.Bug, "Frozen", null, null, null, null, null, null, null, null, null, null, null, null),
            ApiTestContext.Json, CancellationToken);
        created.EnsureSuccessStatusCode();
        var item = (await created.Content.ReadFromJsonAsync<WorkItemView>(ApiTestContext.Json, CancellationToken))!;
        (await Client.PostAsync($"/api/v1/orgs/work-items/projects/{archived.Key}/archive", null, CancellationToken)).EnsureSuccessStatusCode();

        var patched = await Client.PatchAsJsonAsync(ItemPath(item.Key),
            new UpdateWorkItemRequest(null, "Renamed anyway", null, null, null, null, null, null, null, null, null, null, null, null, item.Version),
            ApiTestContext.Json, CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, patched.StatusCode);
        var problem = await patched.Content.ReadFromJsonAsync<ProblemDetails>(ApiTestContext.Json, CancellationToken);
        Assert.Equal(ProblemTypes.ProjectArchived, problem!.Type);

        var related = await Client.PutAsJsonAsync(ItemPath(item.Key, "relations"),
            new PutItemRelationRequest(item.Key, ItemRelationKind.Related), ApiTestContext.Json, CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, related.StatusCode);

        // Reads keep working: archiving retires a project without hiding it.
        var read = await Client.GetAsync(ItemPath(item.Key), CancellationToken);
        Assert.True(read.IsSuccessStatusCode);
    }

    [Fact]
    public async Task raw_html_in_a_description_is_rendered_as_text()
    {
        const string attachmentImage = "<img src=\"/api/v1/attachments/00000000-0000-0000-0000-000000000000/download\" onerror=alert(1)>";
        var created = await Client.PostAsJsonAsync($"/api/v1/orgs/work-items/projects/{Project.Key}/items/",
            new CreateWorkItemRequest(WorkItemType.Bug, "Markup", $"# Title\n\n<script>alert(1)</script>\n\n{attachmentImage}\n\n[link](javascript:alert(1))", null, null, null, null, null, null, null, null, null, null, null),
            ApiTestContext.Json, CancellationToken);
        created.EnsureSuccessStatusCode();
        var item = (await created.Content.ReadFromJsonAsync<WorkItemView>(ApiTestContext.Json, CancellationToken))!;

        // The markup survives only as escaped text: no tag, no attribute, no scheme.
        Assert.DoesNotContain("<script", item.DescriptionHtml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", item.DescriptionHtml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;script&gt;", item.DescriptionHtml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;img", item.DescriptionHtml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("href=\"javascript:", item.DescriptionHtml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<h1", item.DescriptionHtml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task csv_export_neutralises_spreadsheet_formulas()
    {
        await CreateAsync(WorkItemType.Bug, "=HYPERLINK(\"http://evil.example\",\"click\")");

        var csv = await Client.GetStringAsync($"/api/v1/orgs/work-items/projects/{Project.Key}/export/csv", CancellationToken);

        Assert.Contains("\"'=HYPERLINK(", csv);
        Assert.DoesNotContain("\"=HYPERLINK(", csv);
    }

    [Fact]
    public async Task a_workflow_replacement_cannot_point_at_states_from_another_workflow()
    {
        var workflow = Assert.Single(await WorkflowsAsync());
        var states = workflow.States.Select(s => new WorkflowStateInput(s.Id, s.Name, s.Category, s.Position, s.Color, s.IsInitial, null)).ToList();
        var foreign = Guid.NewGuid();

        var foreignState = await Client.PutAsJsonAsync(WorkflowPath(workflow.Id),
            new ReplaceWorkflowRequest(workflow.Name, [.. states, new WorkflowStateInput(foreign, "Smuggled", WorkflowStateCategory.Active, 9, null, false, null)], [], workflow.Version),
            ApiTestContext.Json, CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, foreignState.StatusCode);

        var removed = states.Single(s => s.Category == WorkflowStateCategory.Removed);
        var foreignReplacement = await Client.PutAsJsonAsync(WorkflowPath(workflow.Id),
            new ReplaceWorkflowRequest(workflow.Name,
                states.Where(s => s.Id != removed.Id).Select(s => s.Category == WorkflowStateCategory.Active ? s with { ReplacementStateId = foreign } : s).ToList(),
                [], workflow.Version),
            ApiTestContext.Json, CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, foreignReplacement.StatusCode);
    }

    private async Task<ProjectView> CreateProjectAsync(string key)
    {
        var response = await Client.PostAsJsonAsync("/api/v1/orgs/work-items/projects",
            new CreateProjectRequest($"Project {key}", key, null, ProjectVisibility.Organization, null, null), ApiTestContext.Json, CancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProjectView>(ApiTestContext.Json, CancellationToken))!;
    }
}

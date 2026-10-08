using System.Net.Http.Json;
using System.Text.Json;
using Aictiq.IntegrationTests.Storage;
using Aictiq.IntegrationTests.WorkItems;
using Aictiq.Modules.Analytics.Endpoints;
using Aictiq.Modules.Tenancy.Endpoints;
using Aictiq.Modules.WorkItems.Domain;
using Aictiq.Modules.WorkItems.Endpoints;

namespace Aictiq.IntegrationTests.Analytics;

[Trait("Category", "Analytics")]
public sealed class DashboardTests(PostgresFixture postgres, GarageFixture garage) : WorkItemsTestBase(postgres, garage)
{
    [Fact]
    public async Task default_dashboard_reports_the_active_sprint_and_live_work_figures()
    {
        var team = (await Client.GetFromJsonAsync<List<TeamView>>($"/api/v1/orgs/work-items/projects/{Project.Key}/teams/", ApiTestContext.Json, CancellationToken))![0];
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var sprint = await CreateSprintAsync(team.Id, today.AddDays(-2), today.AddDays(5));
        var done = await AssignAsync(await CreateAsync(WorkItemType.Bug, "Done today", points: 5), team.Id, sprint.Id);
        await AssignAsync(await CreateAsync(WorkItemType.Bug, "Still open", points: 3), team.Id, sprint.Id);
        await AssignAsync(await CreateAsync(WorkItemType.Bug, "Also open", points: 2), team.Id, sprint.Id);
        var dropped = await CreateAsync(WorkItemType.Bug, "Dropped");
        (await Client.PostAsync($"/api/v1/orgs/work-items/sprints/{sprint.Id}/start", null, CancellationToken)).EnsureSuccessStatusCode();
        var states = (await WorkflowsAsync()).Single().States;
        await TransitionAsync(done, states.Single(s => s.Category == WorkflowStateCategory.Completed).Id);
        await TransitionAsync(dropped, states.Single(s => s.Category == WorkflowStateCategory.Removed).Id);

        var dashboard = (await Client.GetFromJsonAsync<List<DashboardView>>($"/api/v1/orgs/work-items/projects/{Project.Key}/dashboards/", ApiTestContext.Json, CancellationToken))!.Single(x => x.IsDefault);
        var data = await Client.GetFromJsonAsync<JsonElement>($"/api/v1/orgs/work-items/projects/{Project.Key}/dashboards/{dashboard.Id}/data", ApiTestContext.Json, CancellationToken);
        var widgets = data.GetProperty("widgets").EnumerateArray().ToDictionary(x => x.GetProperty("type").GetString()!);
        Assert.All(widgets.Values, widget => Assert.Equal(JsonValueKind.Null, widget.GetProperty("error").ValueKind));
        JsonElement Data(string type) => widgets[type].GetProperty("data");

        // Removed work is not live work.
        Assert.Equal(3, Data("items-by-state").EnumerateArray().Sum(row => row.GetProperty("count").GetInt32()));
        var assignee = Assert.Single(Data("items-by-assignee").EnumerateArray());
        Assert.Equal(UserId, assignee.GetProperty("assigneeId").GetString());
        Assert.False(string.IsNullOrWhiteSpace(assignee.GetProperty("displayName").GetString()));
        Assert.Equal(3, assignee.GetProperty("count").GetInt32());
        Assert.Contains(Data("recent-activity").EnumerateArray(), row => row.GetProperty("itemKey").GetString() == done.Key && row.GetProperty("field").GetString() == "state");

        var health = Data("sprint-health");
        Assert.Equal(sprint.Id, health.GetProperty("sprint").GetProperty("id").GetGuid());
        Assert.Equal(33.3m, health.GetProperty("health").GetProperty("percentDone").GetDecimal());

        // No daily sample exists yet: today is read live, and the days still ahead stay empty.
        var days = Data("burndown").GetProperty("days").EnumerateArray().ToList();
        Assert.Equal(7, days.Count);
        Assert.Equal(5m, days.Single(day => day.GetProperty("day").GetString() == today.ToString("yyyy-MM-dd")).GetProperty("remaining").GetDecimal());
        Assert.All(days.Where(day => DateOnly.Parse(day.GetProperty("day").GetString()!) > today),
            day => Assert.Equal(JsonValueKind.Null, day.GetProperty("remaining").ValueKind));

        Assert.Equal(team.Name, Data("velocity").GetProperty("teamName").GetString());
        Assert.Equal(30, Data("cfd").GetProperty("days").GetArrayLength());
        Assert.True(Data("cycle-time").TryGetProperty("cycleTime", out _));
    }

    private async Task<SprintView> CreateSprintAsync(Guid teamId, DateOnly startsOn, DateOnly endsOn)
    {
        var response = await Client.PostAsJsonAsync($"/api/v1/orgs/work-items/teams/{teamId}/sprints",
            new CreateSprintRequest("Dashboard sprint", null, startsOn, endsOn), ApiTestContext.Json, CancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<SprintView>(ApiTestContext.Json, CancellationToken))!;
    }

    private async Task<WorkItemView> AssignAsync(WorkItemView item, Guid teamId, Guid sprintId)
    {
        var assigned = await Client.PostAsJsonAsync($"/api/v1/orgs/work-items/projects/{Project.Key}/items/bulk",
            new BulkUpdateItemsRequest([item.Key], new BulkItemSet(null, UserId, null, teamId, sprintId, null, null),
                new Dictionary<string, uint> { [item.Key] = item.Version }), ApiTestContext.Json, CancellationToken);
        assigned.EnsureSuccessStatusCode();
        return (await assigned.Content.ReadFromJsonAsync<BulkUpdateItemsResponse>(ApiTestContext.Json, CancellationToken))!.Results.Single().Item!;
    }

    private async Task TransitionAsync(WorkItemView item, Guid stateId)
    {
        var response = await Client.PostAsJsonAsync(ItemPath(item.Key, "transition"), new TransitionRequest(stateId, item.Version), ApiTestContext.Json, CancellationToken);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(CancellationToken));
    }
}

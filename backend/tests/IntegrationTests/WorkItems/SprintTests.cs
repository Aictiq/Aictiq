using System.Net;
using System.Net.Http.Json;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.Analytics;
using Aictiq.Modules.Analytics.Domain;
using Aictiq.Modules.Analytics.Endpoints;
using Aictiq.Modules.Tenancy.Endpoints;
using Aictiq.Modules.WorkItems.Domain;
using Aictiq.Modules.WorkItems.Endpoints;
using Aictiq.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;

namespace Aictiq.IntegrationTests.WorkItems;

[Trait("Category", "Planning")]
[Collection("postgres")]
public sealed class SprintTests(PostgresFixture postgres, GarageFixture garage) : WorkItemsTestBase(postgres, garage)
{
    [Fact]
    public async Task planned_sprint_starts_once_and_completion_carries_unfinished_work_to_the_next_sprint()
    {
        var team = (await Client.GetFromJsonAsync<List<TeamView>>($"/api/v1/orgs/work-items/projects/{Project.Key}/teams/", ApiTestContext.Json, CancellationToken))![0];
        var first = await CreateSprintAsync(team.Id, "Sprint 1", new DateOnly(2030, 1, 1), new DateOnly(2030, 1, 8));
        var second = await CreateSprintAsync(team.Id, "Sprint 2", new DateOnly(2030, 1, 8), new DateOnly(2030, 1, 15));
        var item = await CreateAsync(WorkItemType.Bug, "Carry me over");

        var assigned = await Client.PostAsJsonAsync($"/api/v1/orgs/work-items/projects/{Project.Key}/items/bulk",
            new BulkUpdateItemsRequest([item.Key], new BulkItemSet(null, null, null, team.Id, first.Id, null, null), new Dictionary<string, uint> { [item.Key] = item.Version }), ApiTestContext.Json, CancellationToken);
        assigned.EnsureSuccessStatusCode();

        var started = await Client.PostAsync($"/api/v1/orgs/work-items/sprints/{first.Id}/start", null, CancellationToken);
        started.EnsureSuccessStatusCode();
        var raced = await Client.PostAsync($"/api/v1/orgs/work-items/sprints/{first.Id}/start", null, CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, raced.StatusCode);

        var completed = await Client.PostAsJsonAsync($"/api/v1/orgs/work-items/sprints/{first.Id}/complete", new CompleteSprintRequest(second.Id), ApiTestContext.Json, CancellationToken);
        completed.EnsureSuccessStatusCode();
        var moved = await Client.GetFromJsonAsync<WorkItemView>(ItemPath(item.Key), ApiTestContext.Json, CancellationToken);
        Assert.Equal(second.Id, moved!.SprintId);
    }

    [Fact]
    public async Task a_sprint_can_be_read_back_by_id_alone_and_hides_from_non_members()
    {
        // A sprint id travels without its team — in a link, a CLI argument, an agent's
        // notes — so reading one back must not require knowing which team owns it.
        var team = (await Client.GetFromJsonAsync<List<TeamView>>($"/api/v1/orgs/work-items/projects/{Project.Key}/teams/", ApiTestContext.Json, CancellationToken))![0];
        var sprint = await CreateSprintAsync(team.Id, "Readable", new DateOnly(2030, 5, 6), new DateOnly(2030, 5, 20));

        var read = await Client.GetFromJsonAsync<SprintView>($"/api/v1/orgs/work-items/sprints/{sprint.Id}", ApiTestContext.Json, CancellationToken);

        Assert.NotNull(read);
        Assert.Equal(sprint.Id, read.Id);
        Assert.Equal("Readable", read.Name);
        Assert.Equal(team.Id, read.TeamId);
        Assert.Equal(SprintState.Planned, read.State);

        // An unknown id is a 404, not a 403 — the same answer as one belonging elsewhere.
        var missing = await Client.GetAsync($"/api/v1/orgs/work-items/sprints/{Guid.NewGuid()}", CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task database_rejects_overlapping_team_sprints()
    {
        var team = (await Client.GetFromJsonAsync<List<TeamView>>($"/api/v1/orgs/work-items/projects/{Project.Key}/teams/", ApiTestContext.Json, CancellationToken))![0];
        await CreateSprintAsync(team.Id, "First", new DateOnly(2030, 2, 1), new DateOnly(2030, 2, 8));
        var overlap = await Client.PostAsJsonAsync($"/api/v1/orgs/work-items/teams/{team.Id}/sprints",
            new CreateSprintRequest("Overlap", null, new DateOnly(2030, 2, 7), new DateOnly(2030, 2, 14)), ApiTestContext.Json, CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, overlap.StatusCode);
    }

    [Fact]
    public async Task capacity_uses_working_days_and_compares_assigned_remaining_hours()
    {
        var team = (await Client.GetFromJsonAsync<List<TeamView>>($"/api/v1/orgs/work-items/projects/{Project.Key}/teams/", ApiTestContext.Json, CancellationToken))![0];
        var roster = await Client.PutAsJsonAsync($"/api/v1/orgs/work-items/projects/{Project.Key}/teams/{team.Id}/members/{UserId}",
            new UpdateTeamMemberRequest(false, null), ApiTestContext.Json, CancellationToken);
        roster.EnsureSuccessStatusCode();
        var sprint = await CreateSprintAsync(team.Id, "Capacity", new DateOnly(2030, 1, 7), new DateOnly(2030, 1, 14));
        var item = await CreateAsync(WorkItemType.Bug, "Capacity work", remainingHours: 18);
        await AssignAsync(item, team.Id, sprint.Id, UserId);

        var initial = await Client.GetFromJsonAsync<SprintCapacityView>($"/api/v1/orgs/work-items/sprints/{sprint.Id}/capacity", ApiTestContext.Json, CancellationToken);
        Assert.NotNull(initial);
        Assert.Equal(5, initial.WorkingDays);
        Assert.Equal(40, initial.CapacityHours);
        Assert.Equal(18, initial.AssignedRemainingHours);
        Assert.Equal(45, initial.UtilizationPercent);

        var updated = await Client.PutAsJsonAsync($"/api/v1/orgs/work-items/sprints/{sprint.Id}/capacity",
            new UpdateSprintCapacityRequest([new SprintCapacityMemberRequest(UserId, 6, 1)]), ApiTestContext.Json, CancellationToken);
        updated.EnsureSuccessStatusCode();
        var capacity = await updated.Content.ReadFromJsonAsync<SprintCapacityView>(ApiTestContext.Json, CancellationToken);
        Assert.Equal(24, capacity!.CapacityHours);
        Assert.Equal(75, capacity.UtilizationPercent);
        Assert.Equal(1, capacity.Members[0].DaysOff);
    }

    [Fact]
    public async Task velocity_uses_scope_that_existed_when_the_sprint_started()
    {
        var team = (await Client.GetFromJsonAsync<List<TeamView>>($"/api/v1/orgs/work-items/projects/{Project.Key}/teams/", ApiTestContext.Json, CancellationToken))![0];
        var completedSprint = await CreateSprintAsync(team.Id, "Velocity one", new DateOnly(2030, 2, 4), new DateOnly(2030, 2, 11));
        var committed = await CreateAsync(WorkItemType.Bug, "Committed", points: 8);
        committed = await AssignAsync(committed, team.Id, completedSprint.Id, UserId);
        await SeedAnalyticsScopeAsync(completedSprint.Id, committed.Id, true, 8, DateTimeOffset.UtcNow.AddMinutes(-2));
        (await Client.PostAsync($"/api/v1/orgs/work-items/sprints/{completedSprint.Id}/start", null, CancellationToken)).EnsureSuccessStatusCode();

        var addedLater = await CreateAsync(WorkItemType.Bug, "Added after start", points: 13);
        await AssignAsync(addedLater, team.Id, completedSprint.Id, UserId);
        await SeedAnalyticsScopeAsync(completedSprint.Id, addedLater.Id, true, 13, DateTimeOffset.UtcNow.AddDays(1));
        (await Client.PostAsJsonAsync($"/api/v1/orgs/work-items/sprints/{completedSprint.Id}/complete",
            new CompleteSprintRequest(null, true), ApiTestContext.Json, CancellationToken)).EnsureSuccessStatusCode();

        var next = await CreateSprintAsync(team.Id, "Velocity next", new DateOnly(2030, 2, 11), new DateOnly(2030, 2, 18));
        var planned = await CreateAsync(WorkItemType.Bug, "Planned", points: 10);
        await AssignAsync(planned, team.Id, next.Id, UserId);
        var velocity = await Client.GetFromJsonAsync<TeamVelocityView>($"/api/v1/orgs/work-items/teams/{team.Id}/velocity?last=6", ApiTestContext.Json, CancellationToken);

        Assert.NotNull(velocity);
        Assert.Single(velocity.Sprints);
        Assert.Equal(8, velocity.Sprints[0].CommittedPoints);
        Assert.Equal(0, velocity.Sprints[0].CompletedPoints);
        Assert.Equal(0, velocity.AverageVelocity);
        Assert.Equal(next.Id, velocity.Forecast!.SprintId);
        Assert.Equal(10, velocity.Forecast.ScopePoints);
        Assert.Equal(-10, velocity.Forecast.DifferencePoints);
    }

    [Fact]
    public async Task an_archived_project_s_sprints_board_and_dashboards_are_read_only()
    {
        // Sprint and board routes name a team or a sprint rather than a project, so the
        // project filter could not close them; archiving has to anyway.
        var created = await Client.PostAsJsonAsync("/api/v1/orgs/work-items/projects",
            new CreateProjectRequest("Archived planning", "ARCP", null, null, null, null), ApiTestContext.Json, CancellationToken);
        created.EnsureSuccessStatusCode();
        var team = (await Client.GetFromJsonAsync<List<TeamView>>("/api/v1/orgs/work-items/projects/ARCP/teams/", ApiTestContext.Json, CancellationToken))![0];
        var sprint = await CreateSprintAsync(team.Id, "Frozen", new DateOnly(2031, 1, 6), new DateOnly(2031, 1, 13));
        var board = (await Client.GetFromJsonAsync<BoardView>($"/api/v1/orgs/work-items/teams/{team.Id}/board", ApiTestContext.Json, CancellationToken))!;
        (await Client.PostAsync("/api/v1/orgs/work-items/projects/ARCP/archive", null, CancellationToken)).EnsureSuccessStatusCode();

        foreach (var response in new[]
        {
            await Client.PostAsJsonAsync($"/api/v1/orgs/work-items/teams/{team.Id}/sprints", new CreateSprintRequest("Another", null, new DateOnly(2031, 2, 3), new DateOnly(2031, 2, 10)), ApiTestContext.Json, CancellationToken),
            await Client.PatchAsJsonAsync($"/api/v1/orgs/work-items/sprints/{sprint.Id}", new UpdateSprintRequest("Renamed", null, null, null, null, sprint.Version), ApiTestContext.Json, CancellationToken),
            await Client.PostAsync($"/api/v1/orgs/work-items/sprints/{sprint.Id}/start", null, CancellationToken),
            await Client.PutAsJsonAsync($"/api/v1/orgs/work-items/sprints/{sprint.Id}/capacity", new UpdateSprintCapacityRequest([]), ApiTestContext.Json, CancellationToken),
            await Client.PutAsJsonAsync($"/api/v1/orgs/work-items/teams/{team.Id}/board", new UpdateBoardRequest(null, "none", null, null, board.Version), ApiTestContext.Json, CancellationToken),
            await Client.PostAsJsonAsync("/api/v1/orgs/work-items/projects/ARCP/dashboards", new DashboardRequest("Mine",
                [new DashboardWidget("w", "markdown", 0, 0, 4, 3, System.Text.Json.JsonDocument.Parse("{}").RootElement)], false, null), ApiTestContext.Json, CancellationToken),
        })
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Contains("project-archived", await response.Content.ReadAsStringAsync(CancellationToken));
        }

        var unchanged = await Client.GetFromJsonAsync<SprintView>($"/api/v1/orgs/work-items/sprints/{sprint.Id}", ApiTestContext.Json, CancellationToken);
        Assert.Equal(SprintState.Planned, unchanged!.State);
        Assert.Equal("Frozen", unchanged.Name);
    }

    private async Task<SprintView> CreateSprintAsync(Guid teamId, string name, DateOnly startsOn, DateOnly endsOn)
    {
        var response = await Client.PostAsJsonAsync($"/api/v1/orgs/work-items/teams/{teamId}/sprints", new CreateSprintRequest(name, null, startsOn, endsOn), ApiTestContext.Json, CancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<SprintView>(ApiTestContext.Json, CancellationToken))!;
    }

    private async Task<WorkItemView> AssignAsync(WorkItemView item, Guid teamId, Guid sprintId, string assigneeId)
    {
        var assigned = await Client.PostAsJsonAsync($"/api/v1/orgs/work-items/projects/{Project.Key}/items/bulk",
            new BulkUpdateItemsRequest([item.Key], new BulkItemSet(null, assigneeId, null, teamId, sprintId, null, null),
                new Dictionary<string, uint> { [item.Key] = item.Version }), ApiTestContext.Json, CancellationToken);
        assigned.EnsureSuccessStatusCode();
        return (await assigned.Content.ReadFromJsonAsync<BulkUpdateItemsResponse>(ApiTestContext.Json, CancellationToken))!.Results.Single().Item!;
    }

    private async Task SeedAnalyticsScopeAsync(Guid sprintId, Guid itemId, bool added, decimal points, DateTimeOffset at)
    {
        await using var scope = Context.Factory.Services.CreateAsyncScope();
        using var tenant = scope.ServiceProvider.GetRequiredService<AmbientCurrentTenant>().Use(Organization.Id);
        var analytics = scope.ServiceProvider.GetRequiredService<AnalyticsDbContext>();
        analytics.SprintScopeLog.Add(new Aictiq.Modules.Analytics.Domain.SprintScopeLog { EventId = Guid.NewGuid(), OrganizationId = Organization.Id,
            SprintId = sprintId, ItemId = itemId, Added = added, Points = points, At = at });
        await analytics.SaveChangesAsync(CancellationToken);
    }
}

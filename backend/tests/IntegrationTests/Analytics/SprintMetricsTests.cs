using System.Net.Http.Json;
using Aictiq.IntegrationTests.Storage;
using Aictiq.IntegrationTests.WorkItems;
using Aictiq.Modules.Analytics;
using Aictiq.Modules.Analytics.Domain;
using Aictiq.Modules.Analytics.Endpoints;
using Aictiq.Modules.Tenancy.Endpoints;
using Aictiq.Modules.WorkItems.Domain;
using Aictiq.Modules.WorkItems.Endpoints;
using Aictiq.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;

namespace Aictiq.IntegrationTests.Analytics;

[Trait("Category", "Analytics")]
[Collection("postgres")]
public sealed class SprintMetricsTests(PostgresFixture postgres, GarageFixture garage) : WorkItemsTestBase(postgres, garage)
{
    [Fact]
    public async Task burndown_matches_the_daily_snapshot_and_scope_fixture_for_points_and_hours()
    {
        var team = (await Client.GetFromJsonAsync<List<TeamView>>($"/api/v1/orgs/work-items/projects/{Project.Key}/teams/", ApiTestContext.Json, CancellationToken))![0];
        var sprint = await CreateSprintAsync(team.Id, new DateOnly(2030, 1, 7), new DateOnly(2030, 1, 12));
        var first = await CreateAsync(WorkItemType.Bug, "First", points: 5, estimateHours: 10, remainingHours: 10);
        var removed = await CreateAsync(WorkItemType.Bug, "Removed", points: 3, estimateHours: 6, remainingHours: 6);
        var added = await CreateAsync(WorkItemType.Bug, "Added", points: 2, estimateHours: 4, remainingHours: 4);

        await SeedFixtureAsync(sprint.Id, first, removed, added);

        var points = await Client.GetFromJsonAsync<SprintBurndownView>($"/api/v1/orgs/work-items/sprints/{sprint.Id}/burndown?unit=points", ApiTestContext.Json, CancellationToken);
        var hours = await Client.GetFromJsonAsync<SprintBurndownView>($"/api/v1/orgs/work-items/sprints/{sprint.Id}/burndown?unit=hours", ApiTestContext.Json, CancellationToken);

        Assert.NotNull(points); Assert.NotNull(hours);
        Assert.Equal(BurndownUnit.Points, points.Unit);
        Assert.Equal(BurndownUnit.Hours, hours.Unit);
        Assert.Equal([8m, 10m, 7m, 7m, 7m], points.Days.Select(day => day.Scope));
        Assert.Equal([8m, 10m, 7m, 7m, 7m], points.Days.Select(day => day.Remaining));
        Assert.Equal([16m, 20m, 14m, 14m, 14m], hours.Days.Select(day => day.Scope));
        Assert.Equal([16m, 20m, 12m, 12m, 12m], hours.Days.Select(day => day.Remaining));
        Assert.Equal([0m, 0m, 2m, 2m, 2m], hours.Days.Select(day => day.Completed));
        Assert.Equal(8m, points.Days[0].IdealRemaining);
        Assert.Equal(6.4m, points.Days[1].IdealRemaining);
        var change = Assert.Single(points.Days[2].ScopeChanges);
        Assert.False(change.Added); Assert.Equal(3m, change.Value); Assert.Equal(removed.Id, change.ItemId);
    }

    private async Task<SprintView> CreateSprintAsync(Guid teamId, DateOnly startsOn, DateOnly endsOn)
    {
        var response = await Client.PostAsJsonAsync($"/api/v1/orgs/work-items/teams/{teamId}/sprints",
            new CreateSprintRequest("Metrics", null, startsOn, endsOn), ApiTestContext.Json, CancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<SprintView>(ApiTestContext.Json, CancellationToken))!;
    }

    private async Task SeedFixtureAsync(Guid sprintId, WorkItemView first, WorkItemView removed, WorkItemView added)
    {
        await using var scope = Context.Factory.Services.CreateAsyncScope();
        using var tenant = scope.ServiceProvider.GetRequiredService<AmbientCurrentTenant>().Use(Organization.Id);
        var analytics = scope.ServiceProvider.GetRequiredService<AnalyticsDbContext>();
        var day = new DateOnly(2030, 1, 7);
        void Scope(WorkItemView item, bool isAdded, decimal points, decimal hours, int offset) => analytics.SprintScopeLog.Add(new Aictiq.Modules.Analytics.Domain.SprintScopeLog
        {
            EventId = Guid.NewGuid(), OrganizationId = Organization.Id, SprintId = sprintId, ItemId = item.Id,
            Added = isAdded, Points = points, RemainingHours = hours, At = new DateTimeOffset(day.AddDays(offset).ToDateTime(TimeOnly.FromTimeSpan(TimeSpan.FromHours(12))), TimeSpan.Zero)
        });
        void Snapshot(WorkItemView item, int offset, decimal remainingHours) => analytics.ItemStateDaily.Add(new ItemStateDaily
        {
            OrganizationId = Organization.Id, ItemId = item.Id, Day = day.AddDays(offset), ProjectId = Project.Id,
            StateId = item.StateId, SprintId = sprintId, Points = item.Points, EstimateHours = item.EstimateHours,
            RemainingHours = remainingHours, CompletedHours = (item.EstimateHours ?? 0) - remainingHours,
            CapturedAt = DateTimeOffset.UtcNow
        });

        Scope(first, true, 5, 10, 0); Scope(removed, true, 3, 6, 0); Scope(added, true, 2, 4, 1); Scope(removed, false, 3, 6, 2);
        Snapshot(first, 0, 10); Snapshot(removed, 0, 6);
        Snapshot(first, 1, 10); Snapshot(removed, 1, 6); Snapshot(added, 1, 4);
        for (var offset = 2; offset < 5; offset++) { Snapshot(first, offset, 8); Snapshot(added, offset, 4); }
        await analytics.SaveChangesAsync(CancellationToken);
    }
}

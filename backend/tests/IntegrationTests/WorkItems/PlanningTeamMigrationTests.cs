using System.Net.Http.Json;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.Tenancy.Endpoints;
using Aictiq.Modules.WorkItems;
using Aictiq.Modules.WorkItems.Domain;
using Aictiq.Modules.WorkItems.Endpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Aictiq.IntegrationTests.WorkItems;

[Trait("Category", "Planning")]
public sealed class PlanningTeamMigrationTests(PostgresFixture postgres, GarageFixture garage) : WorkItemsTestBase(postgres, garage)
{
    [Fact]
    public async Task upgrade_repairs_unteamed_epics_before_features_and_preserves_explicit_teams_and_children()
    {
        var teamPath = $"/api/v1/orgs/work-items/projects/{Project.Key}/teams";
        var defaultTeam = (await Client.GetFromJsonAsync<List<TeamView>>(teamPath, ApiTestContext.Json, CancellationToken))!.Single(x => x.IsDefault);
        var response = await Client.PostAsJsonAsync(teamPath, new CreateTeamRequest("Portfolio", "PORT"), ApiTestContext.Json, CancellationToken);
        response.EnsureSuccessStatusCode();
        var otherTeam = (await response.Content.ReadFromJsonAsync<TeamView>(ApiTestContext.Json, CancellationToken))!;

        var epic = await CreateAsync(WorkItemType.Epic, "Legacy epic");
        var feature = await CreateAsync(WorkItemType.Feature, "Legacy feature", epic.Id);
        var story = await CreateAsync(WorkItemType.Story, "Nested story", feature.Id);
        var unteamedStory = await CreateAsync(WorkItemType.Story, "Intentionally unteamed story");
        var otherEpic = await CreateAsync(WorkItemType.Epic, "Assigned epic");
        var otherFeature = await CreateAsync(WorkItemType.Feature, "Legacy feature under assigned epic", otherEpic.Id);
        var assignedFeature = await CreateAsync(WorkItemType.Feature, "Assigned feature", epic.Id);

        var options = new DbContextOptionsBuilder<WorkItemsDbContext>()
            .UseNpgsql(Context.ConnectionString, provider => provider.MigrationsHistoryTable("__ef_migrations_history", "work"))
            .UseSnakeCaseNamingConvention().Options;
        await using var db = new WorkItemsDbContext(options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261031115621_JiraImport", CancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE work.items SET team_id = NULL
            WHERE id IN ({epic.Id}, {feature.Id}, {otherFeature.Id}, {unteamedStory.Id});
            UPDATE work.items SET team_id = {otherTeam.Id}
            WHERE id IN ({otherEpic.Id}, {assignedFeature.Id});
            """, CancellationToken);

        await migrator.MigrateAsync(cancellationToken: CancellationToken);

        async Task<WorkItemView> ReadAsync(WorkItemView item) =>
            (await Client.GetFromJsonAsync<WorkItemView>(ItemPath(item.Key), ApiTestContext.Json, CancellationToken))!;
        Assert.Equal(defaultTeam.Id, (await ReadAsync(epic)).TeamId);
        Assert.Equal(defaultTeam.Id, (await ReadAsync(feature)).TeamId);
        Assert.Equal(defaultTeam.Id, (await ReadAsync(story)).TeamId);
        Assert.Equal(otherTeam.Id, (await ReadAsync(otherEpic)).TeamId);
        Assert.Equal(otherTeam.Id, (await ReadAsync(otherFeature)).TeamId);
        Assert.Equal(otherTeam.Id, (await ReadAsync(assignedFeature)).TeamId);
        Assert.Null((await ReadAsync(unteamedStory)).TeamId);

        var backlog = (await Client.GetFromJsonAsync<TeamBacklogView>($"/api/v1/orgs/work-items/teams/{defaultTeam.Id}/backlog", ApiTestContext.Json, CancellationToken))!;
        Assert.Equal([epic.Key, feature.Key, story.Key], backlog.Backlog.Select(x => x.Key));
        Assert.Equal(epic.Id, backlog.Backlog.Single(x => x.Key == feature.Key).ParentId);
        Assert.Equal(feature.Id, backlog.Backlog.Single(x => x.Key == story.Key).ParentId);

        // Once applied, an intentional clear must survive future application startups.
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE work.items SET team_id = NULL WHERE id = {epic.Id}", CancellationToken);
        await migrator.MigrateAsync(cancellationToken: CancellationToken);
        Assert.Null((await ReadAsync(epic)).TeamId);
    }
}

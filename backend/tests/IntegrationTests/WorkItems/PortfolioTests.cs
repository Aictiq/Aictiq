using System.Net.Http.Json;
using Npgsql;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.Tenancy.Domain;
using Aictiq.Modules.Tenancy.Endpoints;
using Aictiq.Modules.WorkItems.Domain;
using Aictiq.Modules.WorkItems.Endpoints;
using Aictiq.SharedKernel.Authorization;

namespace Aictiq.IntegrationTests.WorkItems;

[Trait("Category", "Planning")]
public sealed class PortfolioTests(PostgresFixture postgres, GarageFixture garage) : WorkItemsTestBase(postgres, garage)
{
    [Fact]
    public async Task organization_rollup_includes_descendant_progress_but_excludes_private_projects()
    {
        var epic = await CreateAsync(WorkItemType.Epic, "Visible epic");
        var feature = await CreateAsync(WorkItemType.Feature, "Visible feature", epic.Id);
        var story = await CreateAsync(WorkItemType.Story, "Completed story", feature.Id, points: 8);
        var completed = (await WorkflowsAsync()).Single().States.Single(state => state.Category == WorkflowStateCategory.Completed);
        var transition = await Client.PostAsJsonAsync(ItemPath(story.Key, "transition"),
            new TransitionRequest(completed.Id, story.Version), ApiTestContext.Json, CancellationToken);
        transition.EnsureSuccessStatusCode();

        var secret = await Client.PostAsJsonAsync("/api/v1/orgs/work-items/projects",
            new CreateProjectRequest("Secret", "SEC", null, ProjectVisibility.Private, null, null),
            ApiTestContext.Json, CancellationToken);
        secret.EnsureSuccessStatusCode();
        var privateProject = (await secret.Content.ReadFromJsonAsync<ProjectView>(ApiTestContext.Json, CancellationToken))!;
        var hidden = await Client.PostAsJsonAsync($"/api/v1/orgs/work-items/projects/{privateProject.Key}/items/",
            new CreateWorkItemRequest(WorkItemType.Epic, "Hidden epic", null, null, null, null, null, null,
                null, null, null, null, null, null), ApiTestContext.Json, CancellationToken);
        hidden.EnsureSuccessStatusCode();

        var member = await Context.RegisterAsync($"portfolio-member-{Guid.NewGuid():N}@test.local");
        await AddMemberAsync(member.User.Id);
        using var memberClient = Context.ClientFor(member);

        var portfolio = await memberClient.GetFromJsonAsync<List<PortfolioItemView>>(
            "/api/v1/orgs/work-items/portfolio", ApiTestContext.Json, CancellationToken);

        Assert.Equal(2, portfolio!.Count);
        Assert.DoesNotContain(portfolio, item => item.ProjectKey == privateProject.Key);
        var visible = Assert.Single(portfolio, item => item.Key == epic.Key);
        Assert.Equal(epic.Key, visible.Key);
        Assert.Equal(2, visible.Rollup.TotalCount);
        Assert.Equal(1, visible.Rollup.CompletedCount);
        Assert.Equal(8, visible.Rollup.PointsTotal);
        Assert.Equal(8, visible.Rollup.PointsCompleted);
        Assert.Equal(50, visible.Rollup.PercentDoneByCount);
        Assert.Equal(100, visible.Rollup.PercentDoneByPoints);
    }

    private async Task AddMemberAsync(string userId)
    {
        await using var connection = new NpgsqlConnection(Context.ConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand(
            "INSERT INTO tenancy.organization_members (organization_id, user_id, role, joined_at, can_operate_factory) VALUES (@org, @user, @role, now(), @role <> 3)", connection);
        command.Parameters.AddWithValue("org", Organization.Id);
        command.Parameters.AddWithValue("user", userId);
        command.Parameters.AddWithValue("role", (short)OrgRole.Member);
        await command.ExecuteNonQueryAsync(CancellationToken);
    }
}

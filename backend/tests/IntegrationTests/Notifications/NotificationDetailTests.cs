using System.Net.Http.Json;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.Notifications;
using Aictiq.Modules.Notifications.Domain;
using Aictiq.Modules.Notifications.Endpoints;
using Aictiq.Modules.Tenancy.Domain;
using Aictiq.Modules.Tenancy.Endpoints;
using Aictiq.Modules.WorkItems.Domain;
using Aictiq.Modules.WorkItems.Endpoints;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Aictiq.IntegrationTests.Notifications;

/// <summary>An inbox row says who did what on which item, without saying more than the reader may see.</summary>
[Trait("Category", "Notifications")]
public sealed class NotificationDetailTests(PostgresFixture postgres, GarageFixture garage)
{
    [Fact]
    public async Task inbox_names_the_actor_and_the_item_as_it_is_now()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = await ApiTestContext.CreateAsync(postgres, garage, "notification_detail", appRole: true);
        var organization = await PostAsync<OrganizationView>(context, "/api/v1/orgs",
            new CreateOrganizationRequest("Detail", "detail", null, null), ct);
        await PostAsync<object>(context, "/api/v1/orgs/detail/projects",
            new CreateProjectRequest("Web", "WEB", null, ProjectVisibility.Organization, null, null), ct);
        await PostAsync<object>(context, "/api/v1/orgs/detail/projects",
            new CreateProjectRequest("Secret", "SEC", null, ProjectVisibility.Private, null, null), ct);
        var item = await PostAsync<WorkItemView>(context, "/api/v1/orgs/detail/projects/WEB/items/",
            new CreateWorkItemRequest(WorkItemType.Bug, "Fix the login", null, null, null, null, null, null, null, null, null, null, null, null), ct);
        var hidden = await PostAsync<WorkItemView>(context, "/api/v1/orgs/detail/projects/SEC/items/",
            new CreateWorkItemRequest(WorkItemType.Story, "Private plans", null, null, null, null, null, null, null, null, null, null, null, null), ct);
        var agent = await PostAsync<AgentView>(context, "/api/v1/orgs/detail/agents", new { displayName = "Builder bot" }, ct);
        var state = (await context.Admin.GetFromJsonAsync<List<WorkflowView>>("/api/v1/orgs/detail/projects/WEB/workflows/", ApiTestContext.Json, ct))!
            .Single().States.Single(s => s.Id == item.StateId).Name;
        var admin = await context.LoginAsync(ApiTestContext.AdminEmail, ApiTestContext.AdminPassword);

        var member = await context.RegisterAsync("detail-member@test.local", "Mia", "Member");
        await AddMemberAsync(context, organization.Id, member.User.Id, ct);
        using var client = context.ClientFor(member);

        var now = DateTimeOffset.UtcNow;
        var moved = new Notification
        {
            OrganizationId = organization.Id, UserId = member.User.Id, EventId = Guid.NewGuid(), Kind = NotificationKind.Transitioned,
            ProjectId = await ProjectIdAsync(context, item.Id, ct), ItemId = item.Id, ItemKey = item.Key,
            Message = "A watched item moved to a new workflow state.", CreatedAt = now,
            ActorId = admin.User.Id, FromState = "In Progress", ToState = "Review", Subject = "Old title"
        };
        var byAgent = new Notification
        {
            OrganizationId = organization.Id, UserId = member.User.Id, EventId = Guid.NewGuid(), Kind = NotificationKind.Transitioned,
            ProjectId = moved.ProjectId, ItemId = item.Id, ItemKey = item.Key, Message = "A watched item moved to a new workflow state.",
            CreatedAt = now.AddMinutes(-1), ActorId = agent.UserId, FromState = "Review", ToState = "Done"
        };
        var secret = new Notification
        {
            OrganizationId = organization.Id, UserId = member.User.Id, EventId = Guid.NewGuid(), Kind = NotificationKind.Commented,
            ProjectId = await ProjectIdAsync(context, hidden.Id, ct), ItemId = hidden.Id, ItemKey = hidden.Key,
            Message = "A watched item has a new comment.", CreatedAt = now.AddMinutes(-2), ActorId = admin.User.Id, Subject = "Private plans"
        };
        var legacy = new Notification
        {
            OrganizationId = organization.Id, UserId = member.User.Id, EventId = Guid.NewGuid(), Kind = NotificationKind.Commented,
            Message = "A watched item has a new comment.", CreatedAt = now.AddMinutes(-3)
        };
        var sprint = new Notification
        {
            OrganizationId = organization.Id, UserId = member.User.Id, EventId = Guid.NewGuid(), Kind = NotificationKind.SprintStarted,
            Message = "Sprint 4 started.", CreatedAt = now.AddMinutes(-4), ActorId = admin.User.Id, Subject = "Sprint 4"
        };
        await using (var scope = context.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
            using var tenant = scope.ServiceProvider.GetRequiredService<AmbientCurrentTenant>().Use(organization.Id);
            db.Notifications.AddRange(moved, byAgent, secret, legacy, sprint);
            await db.SaveChangesAsync(ct);
        }

        var inbox = (await client.GetFromJsonAsync<List<NotificationView>>("/api/v1/me/notifications", ApiTestContext.Json, ct))!;
        var row = inbox.Single(x => x.Id == moved.Id);
        Assert.Equal(admin.User.Id, row.Actor!.Id);
        Assert.Equal(admin.User.FullName, row.Actor.DisplayName);
        Assert.False(row.Actor.IsAgent);
        Assert.Equal("Fix the login", row.ItemTitle);
        Assert.Equal("bug", row.ItemType);
        Assert.Equal(state, row.ItemState);
        Assert.Equal(("In Progress", "Review"), (row.FromState, row.ToState));

        // A stakeholder may not learn which agent works here, so its row has no actor.
        var agentRow = inbox.Single(x => x.Id == byAgent.Id);
        Assert.Null(agentRow.Actor);
        Assert.Equal("Fix the login", agentRow.ItemTitle);

        // A private project the reader is not in: neither the live nor the saved title.
        var secretRow = inbox.Single(x => x.Id == secret.Id);
        Assert.Null(secretRow.ItemTitle);
        Assert.Null(secretRow.ItemState);

        var legacyRow = inbox.Single(x => x.Id == legacy.Id);
        Assert.Null(legacyRow.Actor);
        Assert.Equal("A watched item has a new comment.", legacyRow.Message);

        Assert.Equal("Sprint 4", inbox.Single(x => x.Id == sprint.Id).ItemTitle);

        // Once they may operate the factory, the agent is named.
        var grant = await context.Admin.PutAsJsonAsync($"/api/v1/orgs/detail/members/{member.User.Id}",
            new UpdateMemberRequest(null, true), ApiTestContext.Json, ct);
        grant.EnsureSuccessStatusCode();
        inbox = (await client.GetFromJsonAsync<List<NotificationView>>("/api/v1/me/notifications", ApiTestContext.Json, ct))!;
        agentRow = inbox.Single(x => x.Id == byAgent.Id);
        Assert.Equal("Builder bot", agentRow.Actor!.DisplayName);
        Assert.True(agentRow.Actor.IsAgent);
    }

    private static async Task<T> PostAsync<T>(ApiTestContext context, string path, object body, CancellationToken ct)
    {
        var response = await context.Admin.PostAsJsonAsync(path, body, ApiTestContext.Json, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>(ApiTestContext.Json, ct))!;
    }

    private static async Task<Guid> ProjectIdAsync(ApiTestContext context, Guid itemId, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(context.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("SELECT project_id FROM work.items WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", itemId);
        return (Guid)(await command.ExecuteScalarAsync(ct))!;
    }

    private static async Task AddMemberAsync(ApiTestContext context, Guid organizationId, string userId, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(context.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("""
            INSERT INTO tenancy.organization_members (organization_id, user_id, role, joined_at, can_operate_factory)
            VALUES (@org, @user, @role, now(), false)
            """, connection);
        command.Parameters.AddWithValue("org", organizationId);
        command.Parameters.AddWithValue("user", userId);
        command.Parameters.AddWithValue("role", (int)OrgRole.Member);
        await command.ExecuteNonQueryAsync(ct);
    }
}

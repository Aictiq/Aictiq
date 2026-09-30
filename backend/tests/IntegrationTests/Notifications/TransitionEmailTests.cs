using System.Net.Http.Json;
using System.Text.Json;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.Notifications;
using Aictiq.Modules.Notifications.Delivery;
using Aictiq.Modules.Notifications.Domain;
using Aictiq.Modules.Notifications.Events;
using Aictiq.Modules.Notifications.Templates;
using Aictiq.Modules.Tenancy.Domain;
using Aictiq.Modules.Tenancy.Endpoints;
using Aictiq.Modules.WorkItems.Contracts;
using Aictiq.Modules.WorkItems.Domain;
using Aictiq.Modules.WorkItems.Endpoints;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Aictiq.IntegrationTests.Notifications;

[Trait("Category", "Notifications")]
public sealed class TransitionEmailTests(PostgresFixture postgres, GarageFixture garage)
{
    [Fact]
    public async Task a_state_change_email_opens_the_project_board_with_the_watched_item()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = await ApiTestContext.CreateAsync(postgres, garage, "transition_email",
            configure: settings => settings["Email:BaseUrl"] = "https://aictiq.test/",
            configureServices: services =>
            {
                services.AddOptions<NotificationEmailOptions>();
                services.AddSingleton<EmailTemplateRenderer>();
                services.AddScoped<NotificationEmailService>();
            });
        var organizationResponse = await context.Admin.PostAsJsonAsync("/api/v1/orgs",
            new CreateOrganizationRequest("Email links", "email-links", null, null), ApiTestContext.Json, ct);
        organizationResponse.EnsureSuccessStatusCode();
        var organization = (await organizationResponse.Content.ReadFromJsonAsync<OrganizationView>(ApiTestContext.Json, ct))!;
        var projectResponse = await context.Admin.PostAsJsonAsync("/api/v1/orgs/email-links/projects",
            new CreateProjectRequest("Web", "WEB", null, ProjectVisibility.Organization, null, null), ApiTestContext.Json, ct);
        projectResponse.EnsureSuccessStatusCode();
        var itemResponse = await context.Admin.PostAsJsonAsync("/api/v1/orgs/email-links/projects/WEB/items/",
            new CreateWorkItemRequest(WorkItemType.Bug, "Follow the email", null, null, null, null, null, null, null, null, null, null, null, null),
            ApiTestContext.Json, ct);
        itemResponse.EnsureSuccessStatusCode();
        var item = (await itemResponse.Content.ReadFromJsonAsync<WorkItemView>(ApiTestContext.Json, ct))!;

        // The creator watches automatically; a different member changes the state.
        var actor = await context.RegisterAsync($"actor-{Guid.NewGuid():N}@test.local");
        await using var connection = new NpgsqlConnection(context.ConnectionString);
        await connection.OpenAsync(ct);
        await using (var member = new NpgsqlCommand("INSERT INTO tenancy.organization_members (organization_id, user_id, role, joined_at, can_operate_factory) VALUES (@org, @user, @role, now(), false)", connection))
        {
            member.Parameters.AddWithValue("org", organization.Id);
            member.Parameters.AddWithValue("user", actor.User.Id);
            member.Parameters.AddWithValue("role", (int)OrgRole.Member);
            await member.ExecuteNonQueryAsync(ct);
        }
        using var actorClient = context.ClientFor(actor);
        var workflows = await actorClient.GetFromJsonAsync<List<WorkflowView>>("/api/v1/orgs/email-links/projects/WEB/workflows/", ApiTestContext.Json, ct);
        var active = Assert.Single(workflows!).States.Single(s => s.Name == "Active");
        var transition = await actorClient.PostAsJsonAsync($"/api/v1/orgs/email-links/items/{item.Key}/transition",
            new TransitionRequest(active.Id, item.Version), ApiTestContext.Json, ct);
        transition.EnsureSuccessStatusCode();

        // Dispatch the durable event as Workers does, then inspect both email parts.
        await using var command = new NpgsqlCommand("SELECT payload::text FROM shared.outbox_messages WHERE type LIKE '%WorkItemTransitioned%' AND payload::text LIKE @item", connection);
        command.Parameters.AddWithValue("item", $"%{item.Id}%");
        var e = JsonSerializer.Deserialize<WorkItemTransitioned>((string)(await command.ExecuteScalarAsync(ct))!)!;
        await using var scope = context.Factory.Services.CreateAsyncScope();
        await ActivatorUtilities.CreateInstance<TransitionNotificationHandler>(scope.ServiceProvider).HandleAsync(e, ct);
        var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
        var queued = await db.EmailOutbox.SingleAsync(ct);
        var expected = $"https://aictiq.test/o/email-links/p/WEB/board?item={item.Key}";
        Assert.Contains($"href=\"{expected}\"", queued.BodyHtml);
        Assert.Contains(expected, queued.BodyText);
        Assert.DoesNotContain($"/items/{item.Id}", queued.BodyText);

        // An older notification with an id but no key cannot name a project: use the inbox.
        using var tenant = scope.ServiceProvider.GetRequiredService<AmbientCurrentTenant>().Use(organization.Id);
        var notification = await db.Notifications.SingleAsync(ct);
        var legacy = new Notification
        {
            OrganizationId = organization.Id, UserId = notification.UserId, EventId = Guid.NewGuid(),
            Kind = NotificationKind.Transitioned, ItemId = item.Id,
            Message = "An older event", CreatedAt = DateTimeOffset.UtcNow
        };
        await scope.ServiceProvider.GetRequiredService<NotificationEmailService>().QueueImmediateAsync([legacy], ct);
        var fallback = await db.EmailOutbox.SingleAsync(m => m.Id == legacy.Id, ct);
        Assert.Contains("href=\"https://aictiq.test/inbox\"", fallback.BodyHtml);
        Assert.Contains("https://aictiq.test/inbox", fallback.BodyText);
    }
}

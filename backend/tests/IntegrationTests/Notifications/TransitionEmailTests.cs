using System.Net.Http.Json;
using System.Text.Json;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.Notifications;
using Aictiq.Modules.Notifications.Delivery;
using Aictiq.Modules.Notifications.Domain;
using Aictiq.Modules.Notifications.Events;
using Aictiq.Modules.Notifications.Endpoints;
using Aictiq.Modules.Notifications.Templates;
using Aictiq.Modules.Tenancy.Domain;
using Aictiq.Modules.Tenancy.Endpoints;
using Aictiq.Modules.WorkItems.Contracts;
using Aictiq.Modules.WorkItems.Domain;
using Aictiq.Modules.WorkItems.Endpoints;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Aictiq.IntegrationTests.Notifications;

[Trait("Category", "Notifications")]
public sealed class TransitionEmailTests(PostgresFixture postgres, GarageFixture garage)
{
    [Theory]
    [InlineData(RunOutcomes.Succeeded)]
    [InlineData(RunOutcomes.Failed)]
    [InlineData(RunOutcomes.Cancelled)]
    [InlineData(RunOutcomes.TimedOut)]
    public async Task state_change_emails_include_item_and_causal_run_details(string outcome)
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
            new CreateWorkItemRequest(WorkItemType.Bug, "Follow <b>the email</b>", "Fix **delivery** today.", null, null, null, null, null, null, null, null, null, null, null),
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
        Assert.Equal("transition", queued.Template);
        Assert.Contains($"{item.Key} moved to Active", queued.Subject);
        Assert.Contains("Follow &lt;b&gt;the email&lt;/b&gt;", queued.BodyHtml);
        Assert.Contains("Fix delivery today.", queued.BodyText);
        Assert.Contains("Priority: None", queued.BodyText);
        Assert.Contains("to Active", queued.BodyText);
        Assert.DoesNotContain("Factory run", queued.BodyHtml);

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

        // WorkItems applies the run result, then Notifications reads its durable transition.
        var resolved = workflows![0].States.Single(s => s.Name == "Resolved");
        var runId = Guid.NewGuid();
        var finished = new RunFinished(organization.Id, e.ProjectId, item.Id, item.Key, runId, actor.User.Id,
            outcome, resolved.Id, resolved.Id, "Shipped <script>the fix</script>.",
            "https://github.com/acme/web/pull/42", "private operator failure reason");
        await using (var runScope = context.Factory.Services.CreateAsyncScope())
            await ActivatorUtilities.CreateInstance<Aictiq.Modules.WorkItems.Events.RunFinishedHandler>(runScope.ServiceProvider)
                .HandleAsync(finished, ct);
        await using var runCommand = new NpgsqlCommand("SELECT payload::text FROM shared.outbox_messages WHERE type LIKE '%WorkItemTransitioned%' AND payload::text LIKE @run", connection);
        runCommand.Parameters.AddWithValue("run", $"%{runId}%");
        var runEvent = JsonSerializer.Deserialize<WorkItemTransitioned>((string)(await runCommand.ExecuteScalarAsync(ct))!)!;
        await ActivatorUtilities.CreateInstance<TransitionNotificationHandler>(scope.ServiceProvider).HandleAsync(runEvent, ct);
        var inbox = await context.Admin.GetFromJsonAsync<List<NotificationView>>("/api/v1/me/notifications", ApiTestContext.Json, ct);
        var runNotification = Assert.Single(inbox!, n => n.RunId == runId);
        Assert.Equal(organization.Id, runNotification.OrganizationId);
        Assert.Equal(item.Key, runNotification.ItemKey);
        var runMail = await db.EmailOutbox.SingleAsync(m => m.Id != queued.Id && m.Id != legacy.Id, ct);
        Assert.Contains(outcome.Replace('_', ' '), runMail.BodyText);
        Assert.Contains(runId.ToString(), runMail.BodyText);
        Assert.Contains("Shipped &lt;script&gt;the fix&lt;/script&gt;.", runMail.BodyHtml);
        Assert.DoesNotContain("private operator failure reason", runMail.BodyText);
        Assert.Contains("https://github.com/acme/web/pull/42", runMail.BodyHtml);
        Assert.Contains($"https://aictiq.test/o/email-links/factory/runs/{runId}", runMail.BodyText);

        // Replaying the outbox event never creates another email.
        await using (var replayScope = context.Factory.Services.CreateAsyncScope())
            await ActivatorUtilities.CreateInstance<TransitionNotificationHandler>(replayScope.ServiceProvider).HandleAsync(runEvent, ct);
        Assert.Equal(3, await db.EmailOutbox.CountAsync(ct));

        // A stakeholder can see the public result but cannot follow an operator-only run link.
        var stakeholder = new Notification
        {
            OrganizationId = organization.Id, UserId = actor.User.Id, EventId = Guid.NewGuid(),
            Kind = NotificationKind.Transitioned, ProjectId = e.ProjectId, ItemId = item.Id, ItemKey = item.Key,
            Message = "A watched item moved to a new workflow state.", CreatedAt = DateTimeOffset.UtcNow
        };
        await scope.ServiceProvider.GetRequiredService<NotificationEmailService>().QueueImmediateAsync([stakeholder], ct, runEvent);
        var stakeholderMail = await db.EmailOutbox.SingleAsync(m => m.Id == stakeholder.Id, ct);
        Assert.Contains(runId.ToString(), stakeholderMail.BodyText);
        Assert.DoesNotContain("/runs/", stakeholderMail.BodyText);

        // A legacy durable event still produces the original email, without new snapshots.
        await ActivatorUtilities.CreateInstance<TransitionNotificationHandler>(scope.ServiceProvider)
            .HandleAsync(new WorkItemTransitioned(organization.Id, e.ProjectId, item.Id, item.Key,
                active.Id, resolved.Id, actor.User.Id), ct);
        Assert.Equal(2, await db.EmailOutbox.CountAsync(m => m.Template == "notification", ct));
    }
}

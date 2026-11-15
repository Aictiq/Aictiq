using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.Identity.Endpoints;
using Aictiq.Modules.Notifications;
using Aictiq.Modules.Notifications.Delivery;
using Aictiq.Modules.Notifications.Domain;
using Aictiq.Modules.Notifications.Endpoints;
using Aictiq.Modules.Notifications.Events;
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
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Aictiq.IntegrationTests.Notifications;

[Trait("Category", "Notifications")]
public sealed class AssignmentNotificationTests(PostgresFixture postgres, GarageFixture garage)
{
    private const string Items = "/api/v1/orgs/assignments/projects/WEB/items/";
    private const string ItemRoute = "/api/v1/orgs/assignments/items/";

    [Fact]
    public async Task create_and_reassignment_notify_only_the_new_assignee_and_replay_is_idempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        var realtime = new RecordingRealtime();
        await using var context = await CreateAsync(realtime);
        var (organization, recipient) = await SeedAsync(context, ct);
        var actor = (await context.Admin.GetFromJsonAsync<SessionResponse>("/api/v1/auth/session", ApiTestContext.Json, ct))!;
        var item = await CreateItemAsync(context, recipient.User.Id, ct);
        var assignment = Assert.Single(await EventsAsync(context, item.Id, ct));
        Assert.Equal(recipient.User.Id, assignment.AssigneeId);
        Assert.Equal(actor.Id, assignment.ActorId);
        await HandleAsync(context, assignment, ct);

        using var recipientClient = context.ClientFor(recipient);
        var inbox = await recipientClient.GetFromJsonAsync<List<NotificationView>>("/api/v1/me/notifications?unread=true", ApiTestContext.Json, ct);
        var row = Assert.Single(inbox!);
        Assert.Equal(NotificationKind.Assigned, row.Kind);
        Assert.Equal(organization.Id, row.OrganizationId);
        Assert.Equal(item.Id, row.ItemId);
        Assert.Equal(item.Key, row.ItemKey);
        Assert.Equal(actor.Id, row.Actor!.Id);
        Assert.Equal(item.Title, row.ItemTitle);
        var session = await recipientClient.GetFromJsonAsync<SessionResponse>("/api/v1/auth/session", ApiTestContext.Json, ct);
        Assert.Equal(1, session!.UnreadCount);
        Assert.Equal((recipient.User.Id, "notification.new"), Assert.Single(realtime.Messages));

        await using (var scope = context.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
            var mail = await db.EmailOutbox.SingleAsync(ct);
            Assert.Equal(recipient.User.Email, mail.ToAddress);
            Assert.Equal("assignment", mail.Template);
            Assert.Contains(actor.FullName, mail.Subject);
            Assert.Contains(item.Key, mail.Subject);
            Assert.Contains("Review &lt;b&gt;assignment&lt;/b&gt;", mail.BodyHtml);
            Assert.Contains("Priority: High", mail.BodyText);
            Assert.Contains("Due: 2026-12-01", mail.BodyText);
            var link = $"https://aictiq.test/o/assignments/p/WEB/board?item={item.Key}";
            Assert.Contains(link, mail.BodyText);
            Assert.Contains($"href=\"{link}\"", mail.BodyHtml);
        }

        await HandleAsync(context, assignment, ct);
        Assert.All(realtime.Messages, message => Assert.Equal((recipient.User.Id, "notification.new"), message));
        var next = await context.RegisterAsync("next-assignee@test.local", "Next", "Assignee");
        await AddMemberAsync(context, organization.Id, next.User.Id, ct);
        var updated = await UpdateAsync(context, item, next.User.Id, ct);
        var reassigned = Assert.Single(await EventsAsync(context, item.Id, ct), e => e.EventId != assignment.EventId);
        Assert.Equal(next.User.Id, reassigned.AssigneeId);
        await HandleAsync(context, reassigned, ct);
        await using var inspection = context.Factory.Services.CreateAsyncScope();
        using var tenant = inspection.ServiceProvider.GetRequiredService<AmbientCurrentTenant>().Use(organization.Id);
        var notifications = inspection.ServiceProvider.GetRequiredService<NotificationsDbContext>();
        var rows = await notifications.Notifications.ToListAsync(ct);
        Assert.Equal(2, rows.Count);
        Assert.Single(rows, n => n.UserId == recipient.User.Id);
        Assert.Single(rows, n => n.UserId == next.User.Id);
        Assert.Equal(2, await notifications.EmailOutbox.CountAsync(ct));
        Assert.Equal(next.User.Id, updated.AssigneeId);
    }

    [Fact]
    public async Task unchanged_unassigned_self_assigned_and_failed_updates_do_not_emit_assignment_events()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = await CreateAsync(new RecordingRealtime());
        var (_, recipient) = await SeedAsync(context, ct);
        var actor = (await context.Admin.GetFromJsonAsync<SessionResponse>("/api/v1/auth/session", ApiTestContext.Json, ct))!;
        var item = await CreateItemAsync(context, null, ct);
        Assert.Empty(await EventsAsync(context, item.Id, ct));
        item = await UpdateAsync(context, item, recipient.User.Id, ct);
        Assert.Single(await EventsAsync(context, item.Id, ct));
        item = await UpdateAsync(context, item, recipient.User.Id, ct, "Unrelated title edit");
        Assert.Single(await EventsAsync(context, item.Id, ct));
        var stale = item.Version;
        item = await UpdateAsync(context, item, null, ct);
        item = await UpdateAsync(context, item, actor.Id, ct);
        Assert.Single(await EventsAsync(context, item.Id, ct));
        var conflict = await context.Admin.PatchAsJsonAsync(ItemRoute + item.Key,
            new { assigneeId = recipient.User.Id, version = stale }, ApiTestContext.Json, ct);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Single(await EventsAsync(context, item.Id, ct));
    }

    [Fact]
    public async Task failed_email_save_rolls_back_the_inbox_and_retry_commits_both_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var realtime = new RecordingRealtime();
        var failure = new FailAssignmentEmailSaveOnce();
        await using var context = await CreateAsync(realtime, interceptor: failure);
        var (organization, recipient) = await SeedAsync(context, ct);
        var item = await CreateItemAsync(context, recipient.User.Id, ct);
        var assignment = Assert.Single(await EventsAsync(context, item.Id, ct));

        await Assert.ThrowsAsync<DbUpdateException>(() => HandleAsync(context, assignment, ct));
        Assert.True(failure.HasFailed);
        Assert.Empty(realtime.Messages);
        await using (var inspection = context.Factory.Services.CreateAsyncScope())
        {
            using var tenant = inspection.ServiceProvider.GetRequiredService<AmbientCurrentTenant>().Use(organization.Id);
            var db = inspection.ServiceProvider.GetRequiredService<NotificationsDbContext>();
            Assert.Empty(await db.Notifications.ToListAsync(ct));
            Assert.Empty(await db.EmailOutbox.ToListAsync(ct));
        }

        await HandleAsync(context, assignment, ct);
        await HandleAsync(context, assignment, ct);
        await using var retryInspection = context.Factory.Services.CreateAsyncScope();
        using var retryTenant = retryInspection.ServiceProvider.GetRequiredService<AmbientCurrentTenant>().Use(organization.Id);
        var notifications = retryInspection.ServiceProvider.GetRequiredService<NotificationsDbContext>();
        var notification = await notifications.Notifications.SingleAsync(ct);
        Assert.Equal(assignment.EventId, notification.EventId);
        Assert.Equal(recipient.User.Id, notification.UserId);
        var mail = await notifications.EmailOutbox.SingleAsync(ct);
        Assert.Equal(notification.Id, mail.Id);
        Assert.Equal("assignment", mail.Template);
    }

    [Theory]
    [InlineData(true, EmailNotificationMode.Immediate, false, 1, 1)]
    [InlineData(true, EmailNotificationMode.Immediate, true, 1, 0)]
    [InlineData(false, EmailNotificationMode.Immediate, false, 0, 0)]
    [InlineData(true, EmailNotificationMode.Off, false, 1, 0)]
    [InlineData(true, EmailNotificationMode.Digest, false, 1, 0)]
    public async Task assignment_respects_in_app_email_and_presence_preferences(
        bool inApp, EmailNotificationMode emailMode, bool active, int expectedInbox, int expectedEmail)
    {
        var ct = TestContext.Current.CancellationToken;
        var realtime = new RecordingRealtime();
        // Digest sweeps use the Workers database role, which can scan across tenants.
        await using var context = await CreateAsync(realtime, appRole: emailMode != EmailNotificationMode.Digest);
        var (organization, recipient) = await SeedAsync(context, ct);
        await using (var scope = context.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
            db.Preferences.Add(new NotificationPreference
            {
                UserId = recipient.User.Id, Kind = NotificationKind.Assigned, InApp = inApp, EmailMode = emailMode
            });
            if (active) db.Presence.Add(new NotificationPresence { UserId = recipient.User.Id, LastSeenAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(ct);
        }
        var item = await CreateItemAsync(context, recipient.User.Id, ct);
        await HandleAsync(context, Assert.Single(await EventsAsync(context, item.Id, ct)), ct);
        await using var inspection = context.Factory.Services.CreateAsyncScope();
        using var tenant = inspection.ServiceProvider.GetRequiredService<AmbientCurrentTenant>().Use(organization.Id);
        var notifications = inspection.ServiceProvider.GetRequiredService<NotificationsDbContext>();
        Assert.Equal(expectedInbox, await notifications.Notifications.CountAsync(ct));
        Assert.Equal(expectedEmail, await notifications.EmailOutbox.CountAsync(ct));
        Assert.Equal(expectedInbox, realtime.Messages.Count);
        if (emailMode == EmailNotificationMode.Digest)
        {
            var at = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(1).AddHours(8).AddMinutes(10), TimeSpan.Zero);
            var digest = new DailyNotificationDigestService(context.Factory.Services.GetRequiredService<IServiceScopeFactory>(),
                new FixedClock(at), NullLogger<DailyNotificationDigestService>.Instance);
            await digest.RunOnceAsync(ct);
            await digest.RunOnceAsync(ct);
            var mail = await notifications.EmailOutbox.SingleAsync(ct);
            Assert.Equal("digest", mail.Template);
            Assert.Contains(item.Key, mail.BodyText);
        }
    }

    [Fact]
    public async Task assignment_uses_organization_email_default_and_user_override()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = await CreateAsync(new RecordingRealtime());
        var (organization, recipient) = await SeedAsync(context, ct);
        await using (var scope = context.Factory.Services.CreateAsyncScope())
        {
            using var tenant = scope.ServiceProvider.GetRequiredService<AmbientCurrentTenant>().Use(organization.Id);
            var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
            db.OrgDefaults.Add(new OrgNotificationDefault
            {
                OrganizationId = organization.Id, Kind = NotificationKind.Assigned, EmailMode = EmailNotificationMode.Off
            });
            await db.SaveChangesAsync(ct);
        }
        var item = await CreateItemAsync(context, recipient.User.Id, ct);
        await HandleAsync(context, Assert.Single(await EventsAsync(context, item.Id, ct)), ct);
        await using (var scope = context.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
            Assert.Empty(await db.EmailOutbox.ToListAsync(ct));
            db.Preferences.Add(new NotificationPreference
            {
                UserId = recipient.User.Id, Kind = NotificationKind.Assigned, EmailMode = EmailNotificationMode.Immediate
            });
            await db.SaveChangesAsync(ct);
        }
        var another = await CreateItemAsync(context, recipient.User.Id, ct);
        await HandleAsync(context, Assert.Single(await EventsAsync(context, another.Id, ct)), ct);
        await using var inspection = context.Factory.Services.CreateAsyncScope();
        Assert.Single(await inspection.ServiceProvider.GetRequiredService<NotificationsDbContext>().EmailOutbox.ToListAsync(ct));
    }

    [Fact]
    public async Task organization_default_digest_includes_assignments_but_rechecks_revoked_access()
    {
        var ct = TestContext.Current.CancellationToken;
        // Match Workers' cross-tenant database access for this explicit digest sweep.
        await using var context = await CreateAsync(new RecordingRealtime(), appRole: false);
        var (organization, recipient) = await SeedAsync(context, ct);
        var retained = await context.RegisterAsync("retained-assignee@test.local");
        await AddMemberAsync(context, organization.Id, retained.User.Id, ct);
        await using (var scope = context.Factory.Services.CreateAsyncScope())
        {
            using var tenant = scope.ServiceProvider.GetRequiredService<AmbientCurrentTenant>().Use(organization.Id);
            var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
            db.OrgDefaults.Add(new OrgNotificationDefault
            {
                OrganizationId = organization.Id, Kind = NotificationKind.Assigned, EmailMode = EmailNotificationMode.Digest
            });
            await db.SaveChangesAsync(ct);
        }
        var revokedItem = await CreateItemAsync(context, recipient.User.Id, ct);
        var retainedItem = await CreateItemAsync(context, retained.User.Id, ct);
        await HandleAsync(context, Assert.Single(await EventsAsync(context, revokedItem.Id, ct)), ct);
        await HandleAsync(context, Assert.Single(await EventsAsync(context, retainedItem.Id, ct)), ct);
        var revoke = await context.Admin.DeleteAsync($"/api/v1/orgs/assignments/members/{recipient.User.Id}", ct);
        revoke.EnsureSuccessStatusCode();
        using var recipientClient = context.ClientFor(recipient);
        Assert.Empty((await recipientClient.GetFromJsonAsync<List<NotificationView>>("/api/v1/me/notifications", ApiTestContext.Json, ct))!);
        var session = await recipientClient.GetFromJsonAsync<SessionResponse>("/api/v1/auth/session", ApiTestContext.Json, ct);
        Assert.Equal(0, session!.UnreadCount);

        var at = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(1).AddHours(8).AddMinutes(10), TimeSpan.Zero);
        var digest = new DailyNotificationDigestService(context.Factory.Services.GetRequiredService<IServiceScopeFactory>(),
            new FixedClock(at), NullLogger<DailyNotificationDigestService>.Instance);
        await digest.RunOnceAsync(ct);
        await digest.RunOnceAsync(ct);
        await using var inspection = context.Factory.Services.CreateAsyncScope();
        var mail = await inspection.ServiceProvider.GetRequiredService<NotificationsDbContext>().EmailOutbox.SingleAsync(ct);
        Assert.Equal("digest", mail.Template);
        Assert.Equal(retained.User.Email, mail.ToAddress);
        Assert.Contains(retainedItem.Key, mail.BodyText);
        Assert.DoesNotContain(revokedItem.Key, mail.BodyText);
    }

    [Fact]
    public async Task recipient_without_project_access_receives_no_assignment_details()
    {
        var ct = TestContext.Current.CancellationToken;
        var realtime = new RecordingRealtime();
        await using var context = await CreateAsync(realtime);
        var (organization, recipient) = await SeedAsync(context, ct);
        var item = await CreateItemAsync(context, recipient.User.Id, ct);
        var assignment = Assert.Single(await EventsAsync(context, item.Id, ct));
        var outsider = await context.RegisterAsync("outsider@test.local");
        await HandleAsync(context, assignment with { AssigneeId = outsider.User.Id }, ct);
        await HandleAsync(context, assignment with { AssigneeId = assignment.ActorId }, ct);
        await using var scope = context.Factory.Services.CreateAsyncScope();
        using var tenant = scope.ServiceProvider.GetRequiredService<AmbientCurrentTenant>().Use(organization.Id);
        var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
        Assert.Empty(await db.Notifications.ToListAsync(ct));
        Assert.Empty(await db.EmailOutbox.ToListAsync(ct));
        Assert.Empty(realtime.Messages);
    }

    private Task<ApiTestContext> CreateAsync(RecordingRealtime realtime, bool appRole = true, SaveChangesInterceptor? interceptor = null) =>
        ApiTestContext.CreateAsync(postgres, garage, "assignment_notifications", appRole: appRole,
            configure: settings => settings["Email:BaseUrl"] = "https://aictiq.test/",
            configureServices: services =>
            {
                services.AddOptions<NotificationEmailOptions>();
                services.AddSingleton<EmailTemplateRenderer>();
                services.AddScoped<NotificationEmailService>();
                services.AddSingleton<IUserRealtimePublisher>(realtime);
                if (interceptor is not null)
                    services.AddDbContext<NotificationsDbContext>(options => options.AddInterceptors(interceptor));
            });

    private static async Task<(OrganizationView Organization, AuthResponse Recipient)> SeedAsync(ApiTestContext context, CancellationToken ct)
    {
        var organizationResponse = await context.Admin.PostAsJsonAsync("/api/v1/orgs",
            new CreateOrganizationRequest("Assignments", "assignments", null, null), ApiTestContext.Json, ct);
        organizationResponse.EnsureSuccessStatusCode();
        var organization = (await organizationResponse.Content.ReadFromJsonAsync<OrganizationView>(ApiTestContext.Json, ct))!;
        var projectResponse = await context.Admin.PostAsJsonAsync("/api/v1/orgs/assignments/projects",
            new CreateProjectRequest("Web", "WEB", null, ProjectVisibility.Organization, null, null), ApiTestContext.Json, ct);
        projectResponse.EnsureSuccessStatusCode();
        var recipient = await context.RegisterAsync("assignee@test.local", "Assigned", "Member");
        await AddMemberAsync(context, organization.Id, recipient.User.Id, ct);
        return (organization, recipient);
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

    private static async Task<WorkItemView> CreateItemAsync(ApiTestContext context, string? assigneeId, CancellationToken ct)
    {
        var response = await context.Admin.PostAsJsonAsync(Items,
            new CreateWorkItemRequest(WorkItemType.Bug, "Review <b>assignment</b>", null, null, WorkItemPriority.High,
                assigneeId, null, null, null, null, null, null, new DateOnly(2026, 12, 1), null), ApiTestContext.Json, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<WorkItemView>(ApiTestContext.Json, ct))!;
    }

    private static async Task<WorkItemView> UpdateAsync(ApiTestContext context, WorkItemView item, string? assigneeId,
        CancellationToken ct, string? title = null)
    {
        var response = await context.Admin.PatchAsJsonAsync(ItemRoute + item.Key,
            new { assigneeId, version = item.Version, title }, ApiTestContext.Json, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<WorkItemView>(ApiTestContext.Json, ct))!;
    }

    private static async Task<List<WorkItemAssigned>> EventsAsync(ApiTestContext context, Guid itemId, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(context.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("""
            SELECT payload::text FROM shared.outbox_messages
            WHERE type LIKE '%WorkItemAssigned%' AND payload::text LIKE @item
            """, connection);
        command.Parameters.AddWithValue("item", $"%{itemId}%");
        await using var reader = await command.ExecuteReaderAsync(ct);
        var events = new List<WorkItemAssigned>();
        while (await reader.ReadAsync(ct)) events.Add(JsonSerializer.Deserialize<WorkItemAssigned>(reader.GetString(0))!);
        return events;
    }

    private static async Task HandleAsync(ApiTestContext context, WorkItemAssigned assignment, CancellationToken ct)
    {
        await using var scope = context.Factory.Services.CreateAsyncScope();
        await ActivatorUtilities.CreateInstance<AssignmentNotificationHandler>(scope.ServiceProvider).HandleAsync(assignment, ct);
    }

    private sealed class FailAssignmentEmailSaveOnce : SaveChangesInterceptor
    {
        private int _failed;
        public bool HasFailed => _failed != 0;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context is NotificationsDbContext db &&
                db.ChangeTracker.Entries<EmailOutboxMessage>().Any(entry =>
                    entry.State == EntityState.Added && entry.Entity.Template == "assignment") &&
                Interlocked.Exchange(ref _failed, 1) == 0)
                throw new DbUpdateException("Injected assignment email save failure.");
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class RecordingRealtime : IUserRealtimePublisher
    {
        public List<(string UserId, string EventName)> Messages { get; } = [];
        public Task PublishToUserAsync(string userId, string eventName, object payload, CancellationToken cancellationToken = default)
        {
            Messages.Add((userId, eventName));
            return Task.CompletedTask;
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

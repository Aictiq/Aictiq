using System.Collections.Concurrent;
using System.Net.Http.Json;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.Notifications;
using Aictiq.Modules.Notifications.Delivery;
using Aictiq.Modules.Notifications.Domain;
using Aictiq.Modules.Notifications.Events;
using Aictiq.Modules.Notifications.Templates;
using Aictiq.Modules.Tenancy.Domain;
using Aictiq.Modules.Tenancy.Endpoints;
using Aictiq.Modules.WorkItems.Domain;
using Aictiq.Modules.WorkItems.Endpoints;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Aictiq.IntegrationTests.Notifications;

[Trait("Category", "Notifications")]
public sealed class RunChatPermissionTests(PostgresFixture postgres, GarageFixture garage)
{
    [Theory]
    [InlineData(RunOutcomes.Succeeded, false)]
    [InlineData(RunOutcomes.Failed, false)]
    [InlineData(RunOutcomes.TimedOut, false)]
    [InlineData(RunOutcomes.Cancelled, false)]
    [InlineData(RunOutcomes.Succeeded, true)]
    public async Task run_events_exclude_stakeholder_requesters_and_assignees_from_inbox_and_all_personal_chat(
        string outcome, bool needsInput)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = await CreateAsync(new RecordingSender());
        var organization = await OrganizationAsync(context, "run-chat", ct);
        var projectResponse = await context.Admin.PostAsJsonAsync("/api/v1/orgs/run-chat/projects",
            new CreateProjectRequest("Web", "WEB", null, ProjectVisibility.Organization, null, null), ApiTestContext.Json, ct);
        projectResponse.EnsureSuccessStatusCode();
        var project = (await projectResponse.Content.ReadFromJsonAsync<ProjectView>(ApiTestContext.Json, ct))!;
        var stakeholder = await context.RegisterAsync("stakeholder@test.local");
        await MemberAsync(context, organization.Id, stakeholder.User.Id, false, ct);
        var itemResponse = await context.Admin.PostAsJsonAsync("/api/v1/orgs/run-chat/projects/WEB/items/",
            new CreateWorkItemRequest(WorkItemType.Bug, "Ship the change", null, null, null,
                stakeholder.User.Id, null, null, null, null, null, null, null, null), ApiTestContext.Json, ct);
        itemResponse.EnsureSuccessStatusCode();
        var item = (await itemResponse.Content.ReadFromJsonAsync<WorkItemView>(ApiTestContext.Json, ct))!;
        var admin = await context.LoginAsync(ApiTestContext.AdminEmail, ApiTestContext.AdminPassword);
        foreach (var type in Enum.GetValues<ChatChannelType>())
        {
            await ChannelAsync(context, stakeholder.User.Id, type, ct);
            await ChannelAsync(context, admin.User.Id, type, ct);
        }

        RunFinished Finished(string requester) => new(organization.Id, project.Id, item.Id, item.Key,
            Guid.NewGuid(), "agent", outcome, null, null, "Completed work", null, "Private failure detail")
            { RequestedBy = requester, NeedsInput = needsInput, Refinement = needsInput };
        await HandleAsync(context, Finished(stakeholder.User.Id), ct);
        await using var scope = context.Factory.Services.CreateAsyncScope();
        using var tenant = scope.ServiceProvider.GetRequiredService<AmbientCurrentTenant>().Use(organization.Id);
        var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
        Assert.Empty(await db.Notifications.ToListAsync(ct));
        Assert.Empty(await db.ChatOutbox.ToListAsync(ct));
        Assert.Empty(await db.ChatDigestEntries.ToListAsync(ct));

        var eligible = Finished(admin.User.Id);
        await HandleAsync(context, eligible, ct);
        await HandleAsync(context, eligible, ct);
        var notification = Assert.Single(await db.Notifications.ToListAsync(ct));
        Assert.Equal(admin.User.Id, notification.UserId);
        var messages = await db.ChatOutbox.ToListAsync(ct);
        Assert.Equal(3, messages.Count);
        Assert.All(messages, message =>
        {
            Assert.Equal(notification.OrganizationId, message.SourceOrganizationId);
            Assert.Equal(notification.Kind, message.Kind);
            Assert.Contains($"/o/run-chat/factory/runs/{eligible.RunId}", message.Text);
            Assert.DoesNotContain("Private failure detail", message.Text);
        });
    }

    [Theory]
    [InlineData(ChatChannelType.Telegram, EmailNotificationMode.Immediate)]
    [InlineData(ChatChannelType.Slack, EmailNotificationMode.Immediate)]
    [InlineData(ChatChannelType.Discord, EmailNotificationMode.Immediate)]
    [InlineData(ChatChannelType.Telegram, EmailNotificationMode.Digest)]
    [InlineData(ChatChannelType.Slack, EmailNotificationMode.Digest)]
    [InlineData(ChatChannelType.Discord, EmailNotificationMode.Digest)]
    public async Task personal_queue_checks_the_notifications_organization_and_preserves_ordinary_updates(
        ChatChannelType type, EmailNotificationMode mode)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = await CreateAsync(new RecordingSender());
        var organization = await OrganizationAsync(context, "personal-chat", ct);
        var other = await OrganizationAsync(context, "other-chat", ct);
        var member = await context.RegisterAsync("member@test.local");
        await MemberAsync(context, organization.Id, member.User.Id, false, ct);
        await MemberAsync(context, other.Id, member.User.Id, true, ct);
        await ChannelAsync(context, member.User.Id, type, ct);
        var excluded = RunKinds.Select(kind => Notification(organization.Id, member.User.Id, kind, "Excluded run")).ToArray();
        var ordinary = Notification(organization.Id, member.User.Id, NotificationKind.Transitioned, "Ordinary update");
        var allowed = RunKinds.Select(kind => Notification(other.Id, member.User.Id, kind, $"Allowed {kind}")).ToArray();
        await StoreAsync(context, excluded.Append(ordinary).Concat(allowed), ct);
        await using var scope = context.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
        foreach (var kind in RunKinds.Append(NotificationKind.Transitioned))
            db.Preferences.Add(new NotificationPreference { UserId = member.User.Id, Kind = kind, EmailMode = mode });
        await db.SaveChangesAsync(ct);
        var chat = scope.ServiceProvider.GetRequiredService<ChatNotificationService>();
        await chat.QueueAsync(excluded.Append(ordinary).Concat(allowed).ToArray(), null, respectPresence: false, ct);
        var outbox = await db.ChatOutbox.ToListAsync(ct);
        var digest = await db.ChatDigestEntries.ToListAsync(ct);
        Assert.Equal(mode == EmailNotificationMode.Immediate ? 4 : 0, outbox.Count);
        Assert.Equal(mode == EmailNotificationMode.Digest ? 4 : 0, digest.Count);
        var messages = outbox.Select(message => (message.SourceOrganizationId, message.Kind))
            .Concat(digest.Select(message => (message.SourceOrganizationId, message.Kind))).ToArray();
        Assert.DoesNotContain(messages, message => message.SourceOrganizationId == organization.Id && message.Kind != NotificationKind.Transitioned);
        Assert.Contains(messages, message => message.SourceOrganizationId == organization.Id && message.Kind == ordinary.Kind);
        Assert.All(allowed, notification => Assert.Contains(messages, message => message.SourceOrganizationId == other.Id && message.Kind == notification.Kind));
    }

    [Theory]
    [InlineData(ChatChannelType.Telegram)]
    [InlineData(ChatChannelType.Slack)]
    [InlineData(ChatChannelType.Discord)]
    public async Task personal_digest_rechecks_revoked_and_organization_specific_permissions(ChatChannelType type)
    {
        var ct = TestContext.Current.CancellationToken;
        var sender = new RecordingSender();
        await using var context = await CreateAsync(sender);
        var organization = await OrganizationAsync(context, "digest-chat", ct);
        var other = await OrganizationAsync(context, "other-digest-chat", ct);
        var revoked = await context.RegisterAsync("revoked@test.local");
        var cross = await context.RegisterAsync("cross@test.local");
        var stakeholder = await context.RegisterAsync("only-runs@test.local");
        await MemberAsync(context, organization.Id, revoked.User.Id, true, ct);
        await MemberAsync(context, organization.Id, cross.User.Id, false, ct);
        await MemberAsync(context, other.Id, cross.User.Id, true, ct);
        await MemberAsync(context, organization.Id, stakeholder.User.Id, false, ct);
        var revokedChannel = await ChannelAsync(context, revoked.User.Id, type, ct);
        var crossChannel = await ChannelAsync(context, cross.User.Id, type, ct);
        var stakeholderChannel = await ChannelAsync(context, stakeholder.User.Id, type, ct);
        var notifications = new List<Notification>();
        foreach (var user in new[] { revoked.User.Id, cross.User.Id, stakeholder.User.Id })
            notifications.AddRange(RunKinds.Select(kind => Notification(organization.Id, user, kind, "Excluded run")));
        notifications.Add(Notification(organization.Id, revoked.User.Id, NotificationKind.Transitioned, "Revoked ordinary update"));
        notifications.Add(Notification(organization.Id, cross.User.Id, NotificationKind.Transitioned, "Cross ordinary update"));
        notifications.AddRange(RunKinds.Select(kind => Notification(other.Id, cross.User.Id, kind, $"Allowed {kind}")));
        await StoreAsync(context, notifications, ct);
        await using (var scope = context.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
            foreach (var notification in notifications)
            {
                var channelId = notification.UserId == revoked.User.Id ? revokedChannel
                    : notification.UserId == cross.User.Id ? crossChannel : stakeholderChannel;
                // Saved rows reflect notifications queued before a permission change, and older saved rows.
                db.ChatDigestEntries.Add(new ChatDigestEntry
                {
                    ChannelId = channelId, SourceOrganizationId = notification.OrganizationId, Kind = notification.Kind,
                    Line = notification.Message, CreatedAt = notification.CreatedAt
                });
            }
            await db.SaveChangesAsync(ct);
        }
        await RevokeAsync(context, "digest-chat", revoked.User.Id, ct);
        var digestAt = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(1).AddHours(8).AddMinutes(10), TimeSpan.Zero);
        var digest = new DailyNotificationDigestService(context.Factory.Services.GetRequiredService<IServiceScopeFactory>(),
            new FixedClock(digestAt), NullLogger<DailyNotificationDigestService>.Instance);
        await digest.RunChatOnceAsync(ct);
        await digest.RunChatOnceAsync(ct);
        await using var inspection = context.Factory.Services.CreateAsyncScope();
        var notificationsDb = inspection.ServiceProvider.GetRequiredService<NotificationsDbContext>();
        var messages = await notificationsDb.ChatOutbox.ToListAsync(ct);
        Assert.Equal(2, messages.Count);
        Assert.DoesNotContain(messages, message => message.ChannelId == stakeholderChannel);
        var revokedMessage = Assert.Single(messages, message => message.ChannelId == revokedChannel);
        Assert.Contains("Revoked ordinary update", revokedMessage.Text);
        var crossMessage = Assert.Single(messages, message => message.ChannelId == crossChannel);
        Assert.Contains("Cross ordinary update", crossMessage.Text);
        Assert.All(RunKinds, kind => Assert.Contains($"Allowed {kind}", crossMessage.Text));
        Assert.All(messages, message => Assert.DoesNotContain("Excluded run", message.Text));
        Assert.Empty(await notificationsDb.ChatDigestEntries.ToListAsync(ct));
        await DueNowAsync(context, ct);
        await Delivery(context).RunOnceAsync(ct);
        Assert.Equal(2, sender.Sent.Count);
        Assert.All(sender.Sent, message => Assert.DoesNotContain("Excluded run", message.Text));
    }

    [Theory]
    [InlineData(ChatChannelType.Telegram)]
    [InlineData(ChatChannelType.Slack)]
    [InlineData(ChatChannelType.Discord)]
    public async Task personal_delivery_rechecks_permission_revoked_after_queueing(ChatChannelType type)
    {
        var ct = TestContext.Current.CancellationToken;
        var sender = new RecordingSender();
        await using var context = await CreateAsync(sender);
        var organization = await OrganizationAsync(context, "delivery-chat", ct);
        var member = await context.RegisterAsync("operator@test.local");
        await MemberAsync(context, organization.Id, member.User.Id, true, ct);
        await ChannelAsync(context, member.User.Id, type, ct);
        var notifications = RunKinds.Select(kind => Notification(organization.Id, member.User.Id, kind, "Revoked run"))
            .Append(Notification(organization.Id, member.User.Id, NotificationKind.Transitioned, "Ordinary update")).ToArray();
        await StoreAsync(context, notifications, ct);
        await using (var scope = context.Factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ChatNotificationService>()
                .QueueAsync(notifications, null, respectPresence: false, ct);
            Assert.Equal(4, await scope.ServiceProvider.GetRequiredService<NotificationsDbContext>().ChatOutbox.CountAsync(ct));
        }
        await RevokeAsync(context, "delivery-chat", member.User.Id, ct);
        await Delivery(context).RunOnceAsync(ct);
        var sent = Assert.Single(sender.Sent);
        Assert.Equal(type, sent.Type);
        Assert.Contains("Ordinary update", sent.Text);
        await using var inspection = context.Factory.Services.CreateAsyncScope();
        var messages = await inspection.ServiceProvider.GetRequiredService<NotificationsDbContext>().ChatOutbox.ToListAsync(ct);
        Assert.Equal(3, messages.Count(message => message.Status == EmailStatus.Skipped));
        Assert.Equal(1, messages.Count(message => message.Status == EmailStatus.Sent));
    }

    [Fact]
    public async Task deleting_an_organization_removes_its_pending_personal_chat_and_digest_entries()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = await CreateAsync(new RecordingSender());
        var deleted = await OrganizationAsync(context, "deleted-chat", ct);
        var retained = await OrganizationAsync(context, "retained-chat", ct);
        var admin = await context.LoginAsync(ApiTestContext.AdminEmail, ApiTestContext.AdminPassword);
        var channelId = await ChannelAsync(context, admin.User.Id, ChatChannelType.Slack, ct);
        await using var scope = context.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
        foreach (var organization in new[] { deleted.Id, retained.Id })
        {
            db.ChatOutbox.Add(new ChatOutboxMessage
            {
                ChannelId = channelId, SourceOrganizationId = organization, Kind = NotificationKind.RunFailed,
                Text = "Pending run", SendAfter = DateTimeOffset.UtcNow, CreatedAt = DateTimeOffset.UtcNow
            });
            db.ChatDigestEntries.Add(new ChatDigestEntry
            {
                ChannelId = channelId, SourceOrganizationId = organization, Kind = NotificationKind.RunFailed,
                Line = "Digest run", CreatedAt = DateTimeOffset.UtcNow
            });
        }
        await db.SaveChangesAsync(ct);
        await ActivatorUtilities.CreateInstance<NotificationsOrganizationDeletedHandler>(scope.ServiceProvider)
            .HandleAsync(new OrganizationDeleted(deleted.Id, deleted.Slug, [], admin.User.Id), ct);
        db.ChangeTracker.Clear();
        Assert.Equal(retained.Id, Assert.Single(await db.ChatOutbox.ToListAsync(ct)).SourceOrganizationId);
        Assert.Equal(retained.Id, Assert.Single(await db.ChatDigestEntries.ToListAsync(ct)).SourceOrganizationId);
        Assert.Equal(channelId, Assert.Single(await db.UserChannels.ToListAsync(ct)).Id);
    }

    private static readonly NotificationKind[] RunKinds =
        [NotificationKind.RunSucceeded, NotificationKind.RunFailed, NotificationKind.RunNeedsInput];

    private Task<ApiTestContext> CreateAsync(RecordingSender sender) =>
        ApiTestContext.CreateAsync(postgres, garage, "run_chat", appRole: true,
            configure: settings => settings["Email:BaseUrl"] = "https://aictiq.test/",
            configureServices: services =>
            {
                services.AddOptions<NotificationEmailOptions>();
                services.AddSingleton<EmailTemplateRenderer>();
                services.AddScoped<NotificationEmailService>();
                services.RemoveAll<IChatSender>();
                foreach (var type in Enum.GetValues<ChatChannelType>())
                    services.AddScoped<IChatSender>(_ => new FakeSender(sender, type));
            });

    private static async Task<OrganizationView> OrganizationAsync(ApiTestContext context, string slug, CancellationToken ct)
    {
        var response = await context.Admin.PostAsJsonAsync("/api/v1/orgs", new CreateOrganizationRequest(slug, slug, null, null), ApiTestContext.Json, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<OrganizationView>(ApiTestContext.Json, ct))!;
    }

    private static async Task MemberAsync(ApiTestContext context, Guid organizationId, string userId, bool operates, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(context.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("""
            INSERT INTO tenancy.organization_members (organization_id, user_id, role, joined_at, can_operate_factory)
            VALUES (@org, @user, @role, now(), @operates)
            """, connection);
        command.Parameters.AddWithValue("org", organizationId);
        command.Parameters.AddWithValue("user", userId);
        command.Parameters.AddWithValue("role", (int)OrgRole.Member);
        command.Parameters.AddWithValue("operates", operates);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<Guid> ChannelAsync(ApiTestContext context, string userId, ChatChannelType type, CancellationToken ct)
    {
        await using var scope = context.Factory.Services.CreateAsyncScope();
        var channel = new UserChatChannel
        {
            UserId = userId, Type = type, Status = ChatChannelStatus.Active,
            ProtectedTarget = scope.ServiceProvider.GetRequiredService<ChatSecrets>().Protect($"target-{userId}"),
            CreatedAt = DateTimeOffset.UtcNow
        };
        var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
        db.UserChannels.Add(channel);
        await db.SaveChangesAsync(ct);
        return channel.Id;
    }

    private static Notification Notification(Guid organizationId, string userId, NotificationKind kind, string message) => new()
    {
        OrganizationId = organizationId, UserId = userId, EventId = Guid.NewGuid(), Kind = kind,
        Message = message, CreatedAt = DateTimeOffset.UtcNow
    };

    private static async Task StoreAsync(ApiTestContext context, IEnumerable<Notification> notifications, CancellationToken ct)
    {
        await using var scope = context.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
        var tenant = scope.ServiceProvider.GetRequiredService<AmbientCurrentTenant>();
        foreach (var group in notifications.GroupBy(notification => notification.OrganizationId))
        {
            using var organization = tenant.Use(group.Key);
            db.Notifications.AddRange(group);
            await db.SaveChangesAsync(ct);
        }
    }

    private static async Task RevokeAsync(ApiTestContext context, string slug, string userId, CancellationToken ct)
    {
        var response = await context.Admin.PutAsJsonAsync($"/api/v1/orgs/{slug}/members/{userId}",
            new UpdateMemberRequest(null, false), ApiTestContext.Json, ct);
        response.EnsureSuccessStatusCode();
    }

    private static async Task HandleAsync(ApiTestContext context, RunFinished finished, CancellationToken ct)
    {
        await using var scope = context.Factory.Services.CreateAsyncScope();
        await ActivatorUtilities.CreateInstance<RunNotificationHandler>(scope.ServiceProvider).HandleAsync(finished, ct);
    }

    private static ChatDeliveryService Delivery(ApiTestContext context) =>
        ActivatorUtilities.CreateInstance<ChatDeliveryService>(context.Factory.Services,
            Options.Create(new ChatDeliveryOptions { MaxAttempts = 3 }));

    private static async Task DueNowAsync(ApiTestContext context, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(context.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("UPDATE notify.chat_outbox SET send_after = now() WHERE status = 'pending'", connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RecordingSender
    {
        public ConcurrentBag<(ChatChannelType Type, string Text)> Sent { get; } = [];
    }

    private sealed class FakeSender(RecordingSender recorder, ChatChannelType type) : IChatSender
    {
        public ChatChannelType Type => type;

        public Task SendAsync(string target, string text, CancellationToken cancellationToken)
        {
            recorder.Sent.Add((type, text));
            return Task.CompletedTask;
        }
    }
}

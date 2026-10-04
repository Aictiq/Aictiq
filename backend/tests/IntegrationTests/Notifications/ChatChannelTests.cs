using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.Notifications;
using Aictiq.Modules.Notifications.Delivery;
using Aictiq.Modules.Notifications.Domain;
using Aictiq.Modules.Notifications.Endpoints;
using Aictiq.Modules.Notifications.Events;
using Aictiq.Modules.Notifications.Templates;
using Aictiq.Modules.Tenancy.Domain;
using Aictiq.Modules.Tenancy.Endpoints;
using Aictiq.Modules.WorkItems.Domain;
using Aictiq.Modules.WorkItems.Endpoints;
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
public sealed class ChatChannelTests(PostgresFixture postgres, GarageFixture garage)
{
    private const string SlackUrl = "https://hooks.slack.com/services/T000/B000/secretslackpath1234";
    private const string DiscordUrl = "https://discord.com/api/webhooks/123/secretdiscordtoken9876";
    private const string WebhookSecret = "telegram-webhook-secret";

    [Fact]
    public async Task personal_channels_connect_with_masked_secrets_test_and_disconnect()
    {
        var ct = TestContext.Current.CancellationToken;
        var sender = new RecordingChatSender();
        await using var context = await CreateAsync(sender, telegram: true);

        var invalid = await context.Admin.PostAsJsonAsync("/api/v1/me/notification-channels",
            new { type = "slack", webhookUrl = "https://evil.example/services/x" }, ApiTestContext.Json, ct);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        var slack = await PostJsonAsync(context.Admin, "/api/v1/me/notification-channels", new { type = "slack", webhookUrl = SlackUrl }, ct);
        Assert.Equal("active", slack.GetProperty("channel").GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, slack.GetProperty("connectCode").ValueKind);

        var telegram = await PostJsonAsync(context.Admin, "/api/v1/me/notification-channels", new { type = "telegram" }, ct);
        Assert.Equal("pending", telegram.GetProperty("channel").GetProperty("status").GetString());
        var code = telegram.GetProperty("connectCode").GetString()!;
        Assert.Equal($"https://t.me/aictiq_test_bot?start={code}", telegram.GetProperty("connectUrl").GetString());

        // The bot webhook refuses updates without Telegram's secret header.
        using var anonymous = context.Anonymous();
        var forged = await anonymous.PostAsJsonAsync("/api/v1/telegram/webhook", Update(4242, $"/start {code}"), ct);
        Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);
        var linked = await PostTelegramAsync(anonymous, Update(987654321, $"/start@aictiq_test_bot {code}"), ct);
        Assert.Equal(HttpStatusCode.OK, linked.StatusCode);
        Assert.Contains(sender.Sent, s => s.Type == ChatChannelType.Telegram && s.Target == "987654321" && s.Text.StartsWith("Connected", StringComparison.Ordinal));
        // A code works once.
        await PostTelegramAsync(anonymous, Update(111, $"/start {code}"), ct);
        Assert.Contains(sender.Sent, s => s.Target == "111" && s.Text.Contains("not valid", StringComparison.Ordinal));

        var listing = await context.Admin.GetStringAsync("/api/v1/me/notification-channels", ct);
        Assert.DoesNotContain("secretslackpath", listing);
        Assert.DoesNotContain("987654321", listing);
        var channels = JsonDocument.Parse(listing).RootElement;
        Assert.True(channels.GetProperty("telegramAvailable").GetBoolean());
        var list = channels.GetProperty("channels").EnumerateArray().ToList();
        Assert.All(list, c => Assert.Equal("active", c.GetProperty("status").GetString()));
        Assert.Contains(list, c => c.GetProperty("target").GetString() == "hooks.slack.com/…1234");
        Assert.Contains(list, c => c.GetProperty("target").GetString()!.EndsWith("••••4321)", StringComparison.Ordinal));

        await using (var scope = context.Factory.Services.CreateAsyncScope())
        {
            var stored = await scope.ServiceProvider.GetRequiredService<NotificationsDbContext>().UserChannels.ToListAsync(ct);
            Assert.All(stored, c => Assert.DoesNotContain("secretslackpath", c.ProtectedTarget));
        }

        var slackId = slack.GetProperty("channel").GetProperty("id").GetString();
        var test = await context.Admin.PostAsync($"/api/v1/me/notification-channels/{slackId}/test", null, ct);
        Assert.Equal(HttpStatusCode.OK, test.StatusCode);
        Assert.Contains(sender.Sent, s => s.Type == ChatChannelType.Slack && s.Target == SlackUrl && s.Text.Contains("test message", StringComparison.Ordinal));
        sender.Failing.Add(ChatChannelType.Slack);
        var failed = await context.Admin.PostAsync($"/api/v1/me/notification-channels/{slackId}/test", null, ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, failed.StatusCode);
        Assert.Contains("refused", await failed.Content.ReadAsStringAsync(ct));

        // Someone else cannot test or remove it.
        using var other = context.ClientFor(await context.RegisterAsync($"other-{Guid.NewGuid():N}@test.local"));
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/api/v1/me/notification-channels/{slackId}", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await context.Admin.DeleteAsync($"/api/v1/me/notification-channels/{slackId}", ct)).StatusCode);
        var after = JsonDocument.Parse(await context.Admin.GetStringAsync("/api/v1/me/notification-channels", ct)).RootElement;
        Assert.Single(after.GetProperty("channels").EnumerateArray());
    }

    [Fact]
    public async Task telegram_is_not_offered_without_a_bot_token()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = await CreateAsync(new RecordingChatSender(), telegram: false);
        var channels = JsonDocument.Parse(await context.Admin.GetStringAsync("/api/v1/me/notification-channels", ct)).RootElement;
        Assert.False(channels.GetProperty("telegramAvailable").GetBoolean());
        var refused = await context.Admin.PostAsJsonAsync("/api/v1/me/notification-channels", new { type = "telegram" }, ApiTestContext.Json, ct);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        using var anonymous = context.Anonymous();
        Assert.Equal(HttpStatusCode.NotFound, (await PostTelegramAsync(anonymous, Update(1, "/start ABCDEFGH"), ct)).StatusCode);
        var discord = await context.Admin.PostAsJsonAsync("/api/v1/me/notification-channels", new { type = "discord", webhookUrl = DiscordUrl }, ApiTestContext.Json, ct);
        Assert.Equal(HttpStatusCode.OK, discord.StatusCode);
    }

    [Fact]
    public async Task run_events_reach_inbox_chat_and_shared_channels_by_preference_and_default()
    {
        var ct = TestContext.Current.CancellationToken;
        var sender = new RecordingChatSender();
        await using var context = await CreateAsync(sender, telegram: true, appRole: true);
        var organization = await PostJsonAsync(context.Admin, "/api/v1/orgs", new CreateOrganizationRequest("Chat", "chat-org", null, null), ct);
        var organizationId = organization.GetProperty("id").GetGuid();
        var project = await PostJsonAsync(context.Admin, "/api/v1/orgs/chat-org/projects",
            new CreateProjectRequest("Web", "WEB", null, ProjectVisibility.Organization, null, null), ct);
        var item = await PostJsonAsync(context.Admin, "/api/v1/orgs/chat-org/projects/WEB/items/",
            new CreateWorkItemRequest(WorkItemType.Bug, "Ship <chat>", null, null, null, null, null, null, null, null, null, null, null, null), ct);
        var itemId = item.GetProperty("id").GetGuid();
        var itemKey = item.GetProperty("key").GetString()!;
        var projectId = project.GetProperty("id").GetGuid();

        // Personal: Slack by webhook, Telegram linked through the bot.
        await PostJsonAsync(context.Admin, "/api/v1/me/notification-channels", new { type = "slack", webhookUrl = SlackUrl }, ct);
        var code = (await PostJsonAsync(context.Admin, "/api/v1/me/notification-channels", new { type = "telegram" }, ct)).GetProperty("connectCode").GetString();
        using var anonymous = context.Anonymous();
        await PostTelegramAsync(anonymous, Update(555, $"/start {code}"), ct);
        sender.Sent.Clear();

        // Run failed: Telegram immediate, Slack digest, email off. Run succeeded: Inbox off.
        var put = await context.Admin.PutAsJsonAsync("/api/v1/me/notification-preferences", new
        {
            preferences = new object[]
            {
                new { kind = "runFailed", inApp = true, email = "off", telegram = "immediate", slack = "digest", discord = (string?)null },
                new { kind = "runSucceeded", inApp = false, email = "immediate", telegram = "immediate", slack = "immediate", discord = (string?)null }
            }
        }, ApiTestContext.Json, ct);
        put.EnsureSuccessStatusCode();
        Assert.Contains("\"telegram\":\"immediate\"", await put.Content.ReadAsStringAsync(ct));

        // Shared: a Slack channel for the team, and an org default for "needs input".
        var shared = await PostJsonAsync(context.Admin, "/api/v1/orgs/chat-org/notification-channels",
            new { type = "slack", name = "#dev", webhookUrl = SlackUrl.Replace("1234", "team") }, ct);
        var sharedId = shared.GetProperty("channel").GetProperty("id").GetGuid();
        Assert.Equal("immediate", shared.GetProperty("channel").GetProperty("modes").GetProperty("runFailed").GetString());
        var badKind = await context.Admin.PatchAsJsonAsync($"/api/v1/orgs/chat-org/notification-channels/{sharedId}",
            new { modes = new Dictionary<string, string> { ["mentioned"] = "immediate" } }, ApiTestContext.Json, ct);
        Assert.Equal(HttpStatusCode.BadRequest, badKind.StatusCode);
        var defaults = await context.Admin.PutAsJsonAsync("/api/v1/orgs/chat-org/notification-defaults", new
        {
            defaults = new[] { new { kind = "runNeedsInput", email = "off", telegram = (string?)null, slack = "digest", discord = (string?)null } }
        }, ApiTestContext.Json, ct);
        defaults.EnsureSuccessStatusCode();
        Assert.Contains("runNeedsInput", await context.Admin.GetStringAsync("/api/v1/orgs/chat-org/notification-defaults", ct));

        var adminId = (await context.LoginAsync(ApiTestContext.AdminEmail, ApiTestContext.AdminPassword)).User.Id;
        var failedRun = Guid.NewGuid();
        var timedOut = Finished(organizationId, projectId, itemId, itemKey, failedRun, adminId, RunOutcomes.TimedOut);
        await HandleAsync(context, timedOut, ct);

        await using (var scope = context.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
            using var tenant = scope.ServiceProvider.GetRequiredService<AmbientCurrentTenant>().Use(organizationId);
            var notification = await db.Notifications.SingleAsync(ct);
            Assert.Equal(NotificationKind.RunFailed, notification.Kind);
            Assert.Equal(failedRun, notification.RunId);
            Assert.Equal($"The run on {itemKey} timed out.", notification.Message);
            Assert.Empty(await db.EmailOutbox.ToListAsync(ct));
            var queued = await db.ChatOutbox.ToListAsync(ct);
            Assert.Equal(2, queued.Count); // Telegram (personal) and #dev (shared)
            var personal = Assert.Single(queued, m => m.OrganizationId is null);
            Assert.Contains($"https://aictiq.test/o/chat-org/runs/{failedRun}", personal.Text);
            Assert.Contains("Ship &lt;chat&gt;", personal.Text);
            Assert.Equal(organizationId, Assert.Single(queued, m => m.OrganizationId is not null).OrganizationId);
            Assert.Single(await db.ChatDigestEntries.ToListAsync(ct));
        }

        // A replay queues nothing new.
        await HandleAsync(context, timedOut, ct);
        // Inbox off for run succeeded: no channel hears about it, except the shared one, which is not personal.
        await HandleAsync(context, Finished(organizationId, projectId, itemId, itemKey, Guid.NewGuid(), adminId, RunOutcomes.Succeeded), ct);
        // Needs input has no personal setting: the org default puts Slack on digest and email off,
        // and Telegram, which the org left unset, follows email.
        await HandleAsync(context, Finished(organizationId, projectId, itemId, itemKey, Guid.NewGuid(), adminId, RunOutcomes.Succeeded, needsInput: true), ct);
        await using (var scope = context.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
            Assert.Equal(3, await db.ChatOutbox.CountAsync(ct)); // + #dev for needs input
            Assert.Equal(2, await db.ChatDigestEntries.CountAsync(ct));
            Assert.Empty(await db.EmailOutbox.ToListAsync(ct));
        }

        // Delivery: everything immediate goes out.
        var delivery = ActivatorUtilities.CreateInstance<ChatDeliveryService>(context.Factory.Services,
            Options.Create(new ChatDeliveryOptions { MaxAttempts = 3 }));
        await delivery.RunOnceAsync(ct);
        Assert.Equal(3, sender.Sent.Count);
        Assert.Contains(sender.Sent, s => s.Type == ChatChannelType.Telegram && s.Target == "555");

        // The daily digest: one Slack message with both lines, once.
        var digest = new DailyNotificationDigestService(context.Factory.Services.GetRequiredService<IServiceScopeFactory>(),
            new FixedClock(new DateTimeOffset(2026, 10, 5, 8, 10, 0, TimeSpan.Zero)), NullLogger<DailyNotificationDigestService>.Instance);
        await digest.RunChatOnceAsync(ct);
        await digest.RunChatOnceAsync(ct);
        sender.Sent.Clear();
        await DueNowAsync(context, ct); // queued at the fixed clock's 08:10, which may be ahead of the database's
        await delivery.RunOnceAsync(ct);
        var digestMessage = Assert.Single(sender.Sent);
        Assert.Equal(ChatChannelType.Slack, digestMessage.Type);
        Assert.Contains("daily digest", digestMessage.Text);
        Assert.Contains("timed out", digestMessage.Text);
        Assert.Contains("needs your input", digestMessage.Text);

        // A deleted webhook: retries, then broken; Telegram keeps working.
        sender.Failing.Add(ChatChannelType.Slack);
        sender.Sent.Clear();
        for (var i = 0; i < 3; i++)
        {
            await HandleAsync(context, Finished(organizationId, projectId, itemId, itemKey, Guid.NewGuid(), adminId, RunOutcomes.Failed), ct);
            await DueNowAsync(context, ct);
            await delivery.RunOnceAsync(ct);
        }
        await using (var scope = context.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
            using var tenant = scope.ServiceProvider.GetRequiredService<AmbientCurrentTenant>().Use(organizationId);
            var broken = await db.OrgChannels.SingleAsync(c => c.Id == sharedId, ct);
            Assert.Equal(ChatChannelStatus.Broken, broken.Status);
            Assert.Contains("Slack refused", broken.LastError);
        }
        Assert.Equal(3, sender.Sent.Count(s => s.Type == ChatChannelType.Telegram));
        var sharedListing = await context.Admin.GetStringAsync("/api/v1/orgs/chat-org/notification-channels", ct);
        Assert.Contains("\"status\":\"broken\"", sharedListing);
        Assert.DoesNotContain("secretslackpath", sharedListing);
    }

    private Task<ApiTestContext> CreateAsync(RecordingChatSender sender, bool telegram, bool appRole = false) =>
        ApiTestContext.CreateAsync(postgres, garage, "chat", appRole: appRole,
            configure: settings =>
            {
                settings["Email:BaseUrl"] = "https://aictiq.test/";
                if (!telegram) return;
                settings["Notifications:Telegram:BotToken"] = "123456:test-token";
                settings["Notifications:Telegram:BotUsername"] = "aictiq_test_bot";
                settings["Notifications:Telegram:WebhookSecret"] = WebhookSecret;
            },
            configureServices: services =>
            {
                services.AddOptions<NotificationEmailOptions>();
                services.AddSingleton<EmailTemplateRenderer>();
                services.AddScoped<NotificationEmailService>();
                services.RemoveAll<IChatSender>();
                foreach (var type in Enum.GetValues<ChatChannelType>())
                    services.AddScoped<IChatSender>(_ => new FakeSender(sender, type));
            });

    private static object Update(long chatId, string text) =>
        new { update_id = 1, message = new { message_id = 1, text, chat = new { id = chatId, type = "private", first_name = "Ada" } } };

    private static async Task<HttpResponseMessage> PostTelegramAsync(HttpClient client, object update, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/telegram/webhook") { Content = JsonContent.Create(update) };
        request.Headers.Add("X-Telegram-Bot-Api-Secret-Token", WebhookSecret);
        return await client.SendAsync(request, ct);
    }

    private static async Task<JsonElement> PostJsonAsync(HttpClient client, string url, object body, CancellationToken ct)
    {
        var response = await client.PostAsJsonAsync(url, body, ApiTestContext.Json, ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(ct));
        return await response.Content.ReadFromJsonAsync<JsonElement>(ApiTestContext.Json, ct);
    }

    private static RunFinished Finished(Guid organizationId, Guid projectId, Guid itemId, string itemKey, Guid runId, string requestedBy,
        string outcome, bool needsInput = false) =>
        new(organizationId, projectId, itemId, itemKey, runId, "agent-that-does-not-exist", outcome, null, null,
            "Did the work.", null, "private failure detail")
        { RequestedBy = requestedBy, NeedsInput = needsInput, Refinement = needsInput };

    private static async Task HandleAsync(ApiTestContext context, RunFinished e, CancellationToken ct)
    {
        await using var scope = context.Factory.Services.CreateAsyncScope();
        await ActivatorUtilities.CreateInstance<RunNotificationHandler>(scope.ServiceProvider).HandleAsync(e, ct);
    }

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

    private sealed class RecordingChatSender
    {
        public ConcurrentBag<(ChatChannelType Type, string Target, string Text)> Sent { get; } = [];
        public ConcurrentBag<ChatChannelType> Failing { get; } = [];
    }

    private sealed class FakeSender(RecordingChatSender recorder, ChatChannelType type) : IChatSender
    {
        public ChatChannelType Type => type;

        public Task SendAsync(string target, string text, CancellationToken cancellationToken)
        {
            if (recorder.Failing.Contains(type)) throw new ChatDeliveryException("Slack refused the webhook (404 no_service).");
            recorder.Sent.Add((type, target, text));
            return Task.CompletedTask;
        }
    }
}

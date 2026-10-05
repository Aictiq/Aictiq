using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Aictiq.Modules.Notifications.Delivery;
using Aictiq.Modules.Notifications.Domain;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aictiq.Modules.Notifications.Endpoints;

/// <summary><c>target</c> is masked; the webhook URL or chat id is never returned.</summary>
public sealed record ChatChannelView(Guid Id, ChatChannelType Type, ChatChannelStatus Status, string? Target,
    string? LastError, DateTimeOffset? ConnectedAt, DateTimeOffset CreatedAt);
public sealed record OrgChatChannelView(Guid Id, string Name, ChatChannelType Type, ChatChannelStatus Status, string? Target,
    string? LastError, DateTimeOffset? ConnectedAt, DateTimeOffset CreatedAt, IReadOnlyDictionary<string, EmailNotificationMode> Modes);
/// <summary>The code and link are only for a Telegram channel, and only in this one response.</summary>
public sealed record ChatChannelConnectView<T>(T Channel, string? ConnectCode, string? ConnectUrl, DateTimeOffset? ExpiresAt);
public sealed record UserChatChannelsView(bool TelegramAvailable, string? TelegramBotUsername, IReadOnlyList<ChatChannelView> Channels);
public sealed record OrgChatChannelsView(bool TelegramAvailable, string? TelegramBotUsername, IReadOnlyList<NotificationKind> Kinds,
    IReadOnlyList<OrgChatChannelView> Channels);
public sealed record ConnectChatChannelRequest(ChatChannelType? Type, string? WebhookUrl, string? Name = null);
public sealed record UpdateOrgChatChannelRequest(string? Name, IReadOnlyDictionary<string, EmailNotificationMode>? Modes);
public sealed record OrgNotificationDefaultView(NotificationKind Kind, [property: JsonPropertyName("email")] EmailNotificationMode? EmailMode,
    [property: JsonPropertyName("telegram")] EmailNotificationMode? TelegramMode, [property: JsonPropertyName("slack")] EmailNotificationMode? SlackMode,
    [property: JsonPropertyName("discord")] EmailNotificationMode? DiscordMode);
public sealed record PutOrgNotificationDefaultsRequest(IReadOnlyList<OrgNotificationDefaultView>? Defaults);

/// <summary>
/// Chat channels - a person's own and an organization's shared ones - the organization's
/// member defaults, and the Telegram bot's webhook, which is how a one-time code becomes a
/// linked chat.
/// </summary>
public static partial class ChatChannelEndpoints
{
    private static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(15);

    public static IEndpointRouteBuilder MapChatChannelEndpoints(this IEndpointRouteBuilder api)
    {
        var me = api.MapGroup("/me/notification-channels").WithTags("Notifications").RequireAuthorization();
        me.MapGet("/", ListMine).RequireScope(Scopes.Read);
        me.MapPost("/", ConnectMine).RequireScope(Scopes.Write);
        me.MapPost("/{id:guid}/test", TestMine).RequireScope(Scopes.Write);
        me.MapDelete("/{id:guid}", DisconnectMine).RequireScope(Scopes.Write);

        var org = api.MapGroup("/orgs/{orgSlug}").WithTags("Notifications").RequireAuthorization();
        org.MapGet("/notification-channels", ListShared).RequireOrgRole(OrgRole.Admin).RequireScope(Scopes.Read);
        org.MapPost("/notification-channels", ConnectShared).RequireOrgRole(OrgRole.Admin).RequireScope(Scopes.Admin);
        org.MapPatch("/notification-channels/{id:guid}", UpdateShared).RequireOrgRole(OrgRole.Admin).RequireScope(Scopes.Admin);
        org.MapPost("/notification-channels/{id:guid}/test", TestShared).RequireOrgRole(OrgRole.Admin).RequireScope(Scopes.Admin);
        org.MapDelete("/notification-channels/{id:guid}", DisconnectShared).RequireOrgRole(OrgRole.Admin).RequireScope(Scopes.Admin);
        org.MapGet("/notification-defaults", Defaults).RequireOrgRole(OrgRole.Admin).RequireScope(Scopes.Read);
        org.MapPut("/notification-defaults", PutDefaults).RequireOrgRole(OrgRole.Admin).RequireScope(Scopes.Admin);

        api.MapPost("/telegram/webhook", TelegramWebhook).AllowAnonymous().WithTags("Notifications").ExcludeFromDescription();
        return api;
    }

    // ---- personal ----

    private static async Task<IResult> ListMine(NotificationsDbContext db, ICurrentUser user, IOptions<TelegramOptions> telegram, CancellationToken ct)
    {
        var channels = await db.UserChannels.AsNoTracking().Where(c => c.UserId == user.UserId).OrderBy(c => c.Type).ToListAsync(ct);
        return Results.Ok(new UserChatChannelsView(telegram.Value.Enabled, telegram.Value.Enabled ? telegram.Value.BotUsername : null,
            channels.Where(c => Offered(c.Type, telegram.Value)).Select(View).ToList()));
    }

    private static async Task<IResult> ConnectMine(ConnectChatChannelRequest request, NotificationsDbContext db, ICurrentUser user,
        ChatSecrets secrets, IOptions<TelegramOptions> telegram, TimeProvider clock, CancellationToken ct)
    {
        if (Validate(request, telegram.Value) is { } invalid) return invalid;
        var type = request.Type!.Value;
        var now = clock.GetUtcNow();
        // One of each per person: connecting again replaces the old one.
        var previous = await db.UserChannels.Where(c => c.UserId == user.UserId && c.Type == type).ToListAsync(ct);
        db.UserChannels.RemoveRange(previous);
        var previousIds = previous.Select(c => c.Id).ToArray();
        await db.ConnectCodes.Where(c => previousIds.Contains(c.ChannelId)).ExecuteDeleteAsync(ct);
        var channel = new UserChatChannel { UserId = user.UserId!, Type = type, CreatedAt = now };
        Connect(channel, request.WebhookUrl, secrets, now);
        db.UserChannels.Add(channel);
        var code = type == ChatChannelType.Telegram ? AddCode(db, channel.Id, null, now) : null;
        await db.SaveChangesAsync(ct);
        return Results.Ok(new ChatChannelConnectView<ChatChannelView>(View(channel), code,
            code is null ? null : $"https://t.me/{telegram.Value.BotUsername}?start={code}", code is null ? null : now + CodeLifetime));
    }

    private static async Task<IResult> TestMine(Guid id, NotificationsDbContext db, ICurrentUser user, IServiceProvider services, CancellationToken ct)
    {
        var channel = await db.UserChannels.SingleOrDefaultAsync(c => c.Id == id && c.UserId == user.UserId, ct);
        return channel is null ? Results.NotFound() : await TestAsync(db, channel, services, ct);
    }

    private static async Task<IResult> DisconnectMine(Guid id, NotificationsDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var deleted = await db.UserChannels.Where(c => c.Id == id && c.UserId == user.UserId).ExecuteDeleteAsync(ct);
        if (deleted == 0) return Results.NotFound();
        await ForgetAsync(db, id, ct);
        return Results.NoContent();
    }

    // ---- shared ----

    private static async Task<IResult> ListShared(NotificationsDbContext db, IOptions<TelegramOptions> telegram, CancellationToken ct)
    {
        var channels = await db.OrgChannels.AsNoTracking().OrderBy(c => c.CreatedAt).ToListAsync(ct);
        return Results.Ok(new OrgChatChannelsView(telegram.Value.Enabled, telegram.Value.Enabled ? telegram.Value.BotUsername : null,
            OrgChatChannel.OrgWideKinds, channels.Where(c => Offered(c.Type, telegram.Value)).Select(View).ToList()));
    }

    private static async Task<IResult> ConnectShared(ConnectChatChannelRequest request, NotificationsDbContext db, ICurrentTenant tenant,
        ChatSecrets secrets, IOptions<TelegramOptions> telegram, TimeProvider clock, CancellationToken ct)
    {
        if (Validate(request, telegram.Value) is { } invalid) return invalid;
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 100)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["Name the channel (up to 100 characters), e.g. #dev."] });
        var now = clock.GetUtcNow();
        var organizationId = tenant.OrganizationId!.Value;
        var channel = new OrgChatChannel
        {
            OrganizationId = organizationId, Name = request.Name.Trim(), Type = request.Type!.Value,
            Modes = OrgChatChannel.DefaultModes(), CreatedAt = now
        };
        Connect(channel, request.WebhookUrl, secrets, now);
        db.OrgChannels.Add(channel);
        var code = channel.Type == ChatChannelType.Telegram ? AddCode(db, channel.Id, organizationId, now) : null;
        await db.SaveChangesAsync(ct);
        // startgroup: Telegram asks which group to add the bot to, then sends the code there.
        return Results.Ok(new ChatChannelConnectView<OrgChatChannelView>(View(channel), code,
            code is null ? null : $"https://t.me/{telegram.Value.BotUsername}?startgroup={code}", code is null ? null : now + CodeLifetime));
    }

    private static async Task<IResult> UpdateShared(Guid id, UpdateOrgChatChannelRequest request, NotificationsDbContext db, CancellationToken ct)
    {
        var channel = await db.OrgChannels.SingleOrDefaultAsync(c => c.Id == id, ct);
        if (channel is null) return Results.NotFound();
        if (request.Name is not null)
        {
            if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 100)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["Name the channel (up to 100 characters)."] });
            channel.Name = request.Name.Trim();
        }
        if (request.Modes is not null)
        {
            var modes = new Dictionary<NotificationKind, EmailNotificationMode>(channel.Modes);
            foreach (var (name, mode) in request.Modes)
            {
                if (!TryParseKind(name, out var kind) || !OrgChatChannel.OrgWideKinds.Contains(kind))
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["modes"] = [$"'{name}' is not an organization-wide notification."] });
                modes[kind] = mode;
            }
            channel.Modes = modes;
        }
        await db.SaveChangesAsync(ct);
        return Results.Ok(View(channel));
    }

    private static async Task<IResult> TestShared(Guid id, NotificationsDbContext db, IServiceProvider services, CancellationToken ct)
    {
        var channel = await db.OrgChannels.SingleOrDefaultAsync(c => c.Id == id, ct);
        return channel is null ? Results.NotFound() : await TestAsync(db, channel, services, ct);
    }

    private static async Task<IResult> DisconnectShared(Guid id, NotificationsDbContext db, CancellationToken ct)
    {
        var deleted = await db.OrgChannels.Where(c => c.Id == id).ExecuteDeleteAsync(ct);
        if (deleted == 0) return Results.NotFound();
        await ForgetAsync(db, id, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> Defaults(NotificationsDbContext db, CancellationToken ct) =>
        Results.Ok(await db.OrgDefaults.AsNoTracking().OrderBy(d => d.Kind)
            .Select(d => new OrgNotificationDefaultView(d.Kind, d.EmailMode, d.TelegramMode, d.SlackMode, d.DiscordMode)).ToListAsync(ct));

    /// <summary>The list replaces the organization's defaults; a kind left out has none.</summary>
    private static async Task<IResult> PutDefaults(PutOrgNotificationDefaultsRequest request, NotificationsDbContext db, ICurrentTenant tenant, CancellationToken ct)
    {
        var wanted = (request.Defaults ?? []).Where(d => (d.EmailMode ?? d.TelegramMode ?? d.SlackMode ?? d.DiscordMode) is not null)
            .GroupBy(d => d.Kind).ToDictionary(g => g.Key, g => g.Last());
        var rows = await db.OrgDefaults.ToListAsync(ct);
        db.OrgDefaults.RemoveRange(rows.Where(r => !wanted.ContainsKey(r.Kind)));
        foreach (var (kind, value) in wanted)
        {
            var row = rows.SingleOrDefault(r => r.Kind == kind);
            if (row is null) db.OrgDefaults.Add(row = new OrgNotificationDefault { OrganizationId = tenant.OrganizationId!.Value, Kind = kind });
            row.EmailMode = value.EmailMode; row.TelegramMode = value.TelegramMode; row.SlackMode = value.SlackMode; row.DiscordMode = value.DiscordMode;
        }
        await db.SaveChangesAsync(ct);
        return await Defaults(db, ct);
    }

    // ---- Telegram ----

    /// <summary>
    /// Telegram posts every update the bot sees here. Only <c>/start &lt;code&gt;</c> means
    /// anything: it links the chat it was sent from to the channel the code was issued for.
    /// Anything else is acknowledged and ignored, because Telegram retries what is not.
    /// </summary>
    private static async Task<IResult> TelegramWebhook(HttpRequest http, NotificationsDbContext db, AmbientCurrentTenant tenant,
        ChatSecrets secrets, IOptions<TelegramOptions> options, IServiceProvider services, TimeProvider clock,
        ILoggerFactory loggers, CancellationToken ct)
    {
        var telegram = options.Value;
        if (!telegram.Enabled || string.IsNullOrEmpty(telegram.WebhookSecret)) return Results.NotFound();
        var given = http.Headers["X-Telegram-Bot-Api-Secret-Token"].ToString();
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(telegram.WebhookSecret)))
            return Results.Unauthorized();

        TelegramUpdate? update;
        try { update = await JsonSerializer.DeserializeAsync<TelegramUpdate>(http.Body, cancellationToken: ct); }
        catch (JsonException) { return Results.Ok(); }
        if (update?.Message is not { Text: { } text, Chat: { } chat }) return Results.Ok();
        var match = StartCommand().Match(text);
        if (!match.Success) return Results.Ok();

        var sender = services.GetServices<IChatSender>().Single(s => s.Type == ChatChannelType.Telegram);
        var chatId = chat.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var now = clock.GetUtcNow();
        var hash = ChatSecrets.HashCode(match.Groups["code"].Value);
        var code = await db.ConnectCodes.SingleOrDefaultAsync(c => c.CodeHash == hash && c.ExpiresAt > now, ct);
        IChatChannel? channel = null;
        using (code?.OrganizationId is { } organizationId ? tenant.Use(organizationId) : null)
        {
            if (code is not null)
                channel = code.OrganizationId is null
                    ? await db.UserChannels.SingleOrDefaultAsync(c => c.Id == code.ChannelId, ct)
                    : await db.OrgChannels.SingleOrDefaultAsync(c => c.Id == code.ChannelId, ct);
            if (channel is not null)
            {
                channel.ProtectedTarget = secrets.Protect(chatId);
                var name = chat.Title ?? (chat.Username is null ? chat.FirstName : "@" + chat.Username);
                channel.TargetHint = Truncate($"{name ?? "Telegram chat"} ({ChatSecrets.Mask(ChatChannelType.Telegram, chatId)})", 200);
                channel.Status = ChatChannelStatus.Active; channel.ConnectedAt = now;
                channel.ConsecutiveFailures = 0; channel.LastError = null;
                db.ConnectCodes.Remove(code!);
                await db.SaveChangesAsync(ct);
            }
        }

        var reply = channel is null
            ? "That code is not valid or has expired. Get a new one in Aictiq's notification settings."
            : "Connected. Aictiq notifications will arrive in this chat.";
        try { await sender.SendAsync(chatId, ChatFormatter.Escape(ChatChannelType.Telegram, reply), ct); }
        catch (ChatDeliveryException ex)
        {
            loggers.CreateLogger(typeof(ChatChannelEndpoints)).LogInformation("Telegram link reply was not delivered: {Error}", ex.Message);
        }
        return Results.Ok();
    }

    [GeneratedRegex(@"^/start(?:@\w+)?\s+(?<code>[A-Za-z0-9]{6,32})\s*$")]
    private static partial Regex StartCommand();

    private sealed record TelegramUpdate([property: JsonPropertyName("message")] TelegramMessage? Message);
    private sealed record TelegramMessage([property: JsonPropertyName("text")] string? Text, [property: JsonPropertyName("chat")] TelegramChat? Chat);
    private sealed record TelegramChat([property: JsonPropertyName("id")] long Id, [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("username")] string? Username, [property: JsonPropertyName("first_name")] string? FirstName);

    // ---- shared helpers ----

    private static IResult? Validate(ConnectChatChannelRequest request, TelegramOptions telegram)
    {
        if (request.Type is not { } type || !Enum.IsDefined(type))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["type"] = ["Choose telegram, slack or discord."] });
        if (type == ChatChannelType.Telegram)
            return telegram.Enabled ? null
                : Results.ValidationProblem(new Dictionary<string, string[]> { ["type"] = ["Telegram is not configured on this instance."] });
        return ChatSecrets.ValidateWebhookUrl(type, request.WebhookUrl) is { } error
            ? Results.ValidationProblem(new Dictionary<string, string[]> { ["webhookUrl"] = [error] })
            : null;
    }

    /// <summary>A webhook is live at once; a Telegram channel waits for its code.</summary>
    private static void Connect(IChatChannel channel, string? webhookUrl, ChatSecrets secrets, DateTimeOffset now)
    {
        if (channel.Type == ChatChannelType.Telegram)
        {
            channel.Status = ChatChannelStatus.Pending;
            return;
        }
        var url = webhookUrl!.Trim();
        channel.ProtectedTarget = secrets.Protect(url);
        channel.TargetHint = ChatSecrets.Mask(channel.Type, url);
        channel.Status = ChatChannelStatus.Active;
        channel.ConnectedAt = now;
    }

    private static string AddCode(NotificationsDbContext db, Guid channelId, Guid? organizationId, DateTimeOffset now)
    {
        var code = ChatSecrets.CreateCode();
        db.ConnectCodes.Add(new ChatConnectCode
        {
            CodeHash = ChatSecrets.HashCode(code), ChannelId = channelId, OrganizationId = organizationId, ExpiresAt = now + CodeLifetime
        });
        return code;
    }

    /// <summary>Sent now, while the person waits, so they see at once whether it works.
    /// A success also brings a broken channel back.</summary>
    private static async Task<IResult> TestAsync(NotificationsDbContext db, IChatChannel channel, IServiceProvider services, CancellationToken ct)
    {
        if (channel.ProtectedTarget is null)
            return Results.UnprocessableEntity(new { error = "Finish connecting the channel first." });
        var sender = services.GetServices<IChatSender>().Single(s => s.Type == channel.Type);
        try
        {
            var target = services.GetRequiredService<ChatSecrets>().Unprotect(channel.ProtectedTarget);
            await sender.SendAsync(target, ChatFormatter.Format(channel.Type,
                new ChatMessage("Aictiq test message", "Notifications will arrive here.", null)), ct);
        }
        catch (Exception ex) when (ex is ChatDeliveryException or CryptographicException)
        {
            channel.LastError = ex is ChatDeliveryException ? ex.Message : "The channel must be connected again.";
            await db.SaveChangesAsync(ct);
            return Results.UnprocessableEntity(new { error = channel.LastError });
        }
        channel.Status = ChatChannelStatus.Active; channel.ConsecutiveFailures = 0; channel.LastError = null;
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { ok = true });
    }

    private static async Task ForgetAsync(NotificationsDbContext db, Guid channelId, CancellationToken ct)
    {
        await db.ConnectCodes.Where(c => c.ChannelId == channelId).ExecuteDeleteAsync(ct);
        await db.ChatDigestEntries.Where(e => e.ChannelId == channelId).ExecuteDeleteAsync(ct);
        await db.ChatOutbox.Where(m => m.ChannelId == channelId && m.Status == EmailStatus.Pending).ExecuteDeleteAsync(ct);
    }

    private static bool Offered(ChatChannelType type, TelegramOptions telegram) => type != ChatChannelType.Telegram || telegram.Enabled;

    private static bool TryParseKind(string name, out NotificationKind kind) =>
        Enum.TryParse(name, ignoreCase: true, out kind) && Enum.IsDefined(kind) && !char.IsDigit(name[0]);

    internal static string KindName(NotificationKind kind) => JsonNamingPolicy.CamelCase.ConvertName(kind.ToString());

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];

    private static ChatChannelView View(UserChatChannel c) => new(c.Id, c.Type, c.Status, c.TargetHint, c.LastError, c.ConnectedAt, c.CreatedAt);

    private static OrgChatChannelView View(OrgChatChannel c) => new(c.Id, c.Name, c.Type, c.Status, c.TargetHint, c.LastError, c.ConnectedAt,
        c.CreatedAt, c.Modes.ToDictionary(m => KindName(m.Key), m => m.Value));
}

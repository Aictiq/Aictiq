namespace Aictiq.Modules.Notifications.Delivery;

/// <summary>
/// The Aictiq Telegram bot, bound from <c>Notifications:Telegram</c>. Slack and Discord need
/// nothing from the operator - a webhook URL is all they take - but Telegram needs a bot,
/// and a self-hosted instance must bring its own. Without a token Telegram is not offered.
/// </summary>
public sealed class TelegramOptions
{
    public const string SectionName = "Notifications:Telegram";

    /// <summary>From @BotFather. A secret: it is never logged or returned.</summary>
    public string? BotToken { get; init; }

    /// <summary>The bot's @username without the @, for the "send /start to @bot" instruction and t.me links.</summary>
    public string? BotUsername { get; init; }

    /// <summary>
    /// The <c>secret_token</c> given to Telegram's setWebhook. Telegram echoes it on every
    /// update, and an update without it is refused - otherwise anyone could post a forged
    /// <c>/start</c> to link their chat to someone else's code.
    /// </summary>
    public string? WebhookSecret { get; init; }

    public bool Enabled => !string.IsNullOrWhiteSpace(BotToken) && !string.IsNullOrWhiteSpace(BotUsername);
}

/// <summary>How hard the Workers process tries a chat message, bound from <c>Notifications:Chat</c>.</summary>
public sealed class ChatDeliveryOptions
{
    public const string SectionName = "Notifications:Chat";

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);
    public int BatchSize { get; set; } = 20;

    /// <summary>Attempts before a message fails, and consecutive failures before its channel is marked broken.</summary>
    public int MaxAttempts { get; set; } = 5;

    public int BaseRetrySeconds { get; set; } = 30;
    public int MaxRetrySeconds { get; set; } = 1800;

    /// <summary>Settled rows older than this are deleted; 0 keeps everything.</summary>
    public int RetentionDays { get; set; } = 14;
}

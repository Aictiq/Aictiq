using Aictiq.SharedKernel.Domain;

namespace Aictiq.Modules.Notifications.Domain;

/// <summary>Stored as its number: new kinds go on the end.</summary>
public enum NotificationKind : short { Assigned, Mentioned, Commented, Transitioned, Claimed, SprintStarted, SprintCompleted, WikiMentioned, InviteAccepted, Replied, Reacted, RunSucceeded, RunFailed, RunNeedsInput, RunRefined }

public sealed class Notification : TenantEntity
{
    public required string UserId { get; init; }
    public Guid EventId { get; init; }
    public NotificationKind Kind { get; init; }
    public Guid? ProjectId { get; init; }
    public Guid? ItemId { get; init; }
    public string? ItemKey { get; init; }
    public Guid? RunId { get; init; }
    public required string Message { get; init; }

    /// <summary>Who acted: a person, or the agent of a run. Null on rows written before it
    /// was recorded; the inbox then shows <see cref="Message"/> as it always did.</summary>
    public string? ActorId { get; init; }
    /// <summary>The state names of a transition as they were when it happened.</summary>
    public string? FromState { get; init; }
    public string? ToState { get; init; }
    public string? Emoji { get; init; }
    /// <summary>A title captured when the notification was written: what the inbox shows for
    /// a sprint, wiki page or invitation, which have no work item to look up.</summary>
    public string? Subject { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ReadAt { get; set; }
}

/// <summary>User delivery choices apply to their account across organizations.</summary>
public sealed class NotificationPreference : EntityBase
{
    public required string UserId { get; init; }
    public NotificationKind Kind { get; init; }
    public bool InApp { get; set; } = true;
    public EmailNotificationMode EmailMode { get; set; } = EmailNotificationMode.Immediate;

    /// <summary>Null follows the organization's default, and failing that <see cref="EmailMode"/>.</summary>
    public EmailNotificationMode? TelegramMode { get; set; }
    public EmailNotificationMode? SlackMode { get; set; }
    public EmailNotificationMode? DiscordMode { get; set; }

    public EmailNotificationMode? ChatMode(ChatChannelType type) => type switch
    {
        ChatChannelType.Telegram => TelegramMode,
        ChatChannelType.Slack => SlackMode,
        _ => DiscordMode
    };
}

/// <summary>Email is intentionally a three-state choice; a boolean cannot distinguish
/// "do not mail me" from "include this in tomorrow's digest".</summary>
public enum EmailNotificationMode : short { Off, Immediate, Digest }

/// <summary>One row per recipient prevents a restarted digest sweep from mailing twice.</summary>
public sealed class NotificationDigest : EntityBase
{
    public required string UserId { get; init; }
    public DateOnly? LastDeliveredLocalDate { get; set; }
    public DateTimeOffset? LastDeliveredAt { get; set; }
}

/// <summary>Last observed SignalR activity. It is persisted so the Workers process can
/// suppress immediate mail even when it is not the API instance holding the connection.</summary>
public sealed class NotificationPresence : EntityBase
{
    public required string UserId { get; init; }
    public DateTimeOffset LastSeenAt { get; set; }
}

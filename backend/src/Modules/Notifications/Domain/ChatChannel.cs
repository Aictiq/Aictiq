using Aictiq.SharedKernel.Domain;

namespace Aictiq.Modules.Notifications.Domain;

/// <summary>Stored as its number: new platforms go on the end.</summary>
public enum ChatChannelType : short { Telegram, Slack, Discord }

/// <summary>
/// A Telegram channel is <see cref="Pending"/> until someone sends the bot its code; a
/// webhook is active from the start. <see cref="Broken"/> stops delivery until a test
/// message gets through again.
/// </summary>
public enum ChatChannelStatus : short { Pending, Active, Broken }

/// <summary>What the two kinds of channel share: where messages go and how that is going.</summary>
public interface IChatChannel
{
    Guid Id { get; }
    ChatChannelType Type { get; }
    ChatChannelStatus Status { get; set; }

    /// <summary>The webhook URL or Telegram chat id, encrypted with Data Protection. Null while pending.</summary>
    string? ProtectedTarget { get; set; }

    /// <summary>A masked description that is safe to show and log - never the secret itself.</summary>
    string? TargetHint { get; set; }

    string? LastError { get; set; }
    int ConsecutiveFailures { get; set; }
    DateTimeOffset? ConnectedAt { get; set; }
}

/// <summary>
/// A person's own Telegram chat, Slack or Discord webhook - at most one of each. Like
/// <see cref="NotificationPreference"/> it belongs to the account, not to an organization,
/// so it is not a tenant table: the delivery sweep reads it for every organization.
/// </summary>
public sealed class UserChatChannel : EntityBase, IChatChannel
{
    public required string UserId { get; init; }
    public ChatChannelType Type { get; init; }
    public ChatChannelStatus Status { get; set; }
    public string? ProtectedTarget { get; set; }
    public string? TargetHint { get; set; }
    public string? LastError { get; set; }
    public int ConsecutiveFailures { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ConnectedAt { get; set; }

    /// <summary>The recipient's local date of the last digest, so a restarted sweep sends one a day.</summary>
    public DateOnly? LastDigestLocalDate { get; set; }
}

/// <summary>
/// A shared team channel an org admin connected. It receives org-wide events - never a
/// person's own ("you were mentioned") - at the mode chosen per kind in <see cref="Modes"/>.
/// </summary>
public sealed class OrgChatChannel : TenantEntity, IChatChannel
{
    public required string Name { get; set; }
    public ChatChannelType Type { get; init; }
    public ChatChannelStatus Status { get; set; }
    public string? ProtectedTarget { get; set; }
    public string? TargetHint { get; set; }
    public string? LastError { get; set; }
    public int ConsecutiveFailures { get; set; }
    public Dictionary<NotificationKind, EmailNotificationMode> Modes { get; set; } = [];
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ConnectedAt { get; set; }

    /// <summary>UTC date of the last digest: a shared channel has no one person's time zone.</summary>
    public DateOnly? LastDigestDate { get; set; }

    /// <summary>Events about the organization as a whole, as opposed to one person's work.</summary>
    public static readonly NotificationKind[] OrgWideKinds =
    [
        NotificationKind.Transitioned, NotificationKind.SprintStarted, NotificationKind.SprintCompleted,
        NotificationKind.RunSucceeded, NotificationKind.RunFailed, NotificationKind.RunNeedsInput
    ];

    /// <summary>What a newly connected shared channel hears about: the things a team acts on.</summary>
    public static Dictionary<NotificationKind, EmailNotificationMode> DefaultModes() => OrgWideKinds.ToDictionary(
        kind => kind, kind => kind is NotificationKind.Transitioned or NotificationKind.RunSucceeded
            ? EmailNotificationMode.Off : EmailNotificationMode.Immediate);
}

/// <summary>
/// A one-time Telegram link code. Kept apart from the channels because the bot's webhook
/// arrives with no organization: this table is what tells it which channel - and, for a
/// shared one, which tenant - the code belongs to. Only the hash is stored.
/// </summary>
public sealed class ChatConnectCode : EntityBase
{
    public required string CodeHash { get; init; }
    public Guid ChannelId { get; init; }

    /// <summary>Set for a shared channel; null for a person's own.</summary>
    public Guid? OrganizationId { get; init; }

    public DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>
/// An organization's default for a kind, used for a member who has not chosen one. Null
/// means the organization has no opinion on that channel either.
/// </summary>
public sealed class OrgNotificationDefault : TenantEntity
{
    public NotificationKind Kind { get; init; }
    public EmailNotificationMode? EmailMode { get; set; }
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

/// <summary>
/// One message waiting for a chat platform, already formatted for it. Modelled on
/// <see cref="EmailOutboxMessage"/> and, like it, not a tenant table: the sweep spans every
/// organization. The id is derived from what caused it, so a replayed event is a no-op.
/// </summary>
public sealed class ChatOutboxMessage : EntityBase
{
    public Guid ChannelId { get; init; }

    /// <summary>The tenant of a shared channel, needed to load it; null for a person's own.</summary>
    public Guid? OrganizationId { get; init; }

    public required string Text { get; init; }
    public string Status { get; set; } = EmailStatus.Pending;
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset SendAfter { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// One line waiting for a channel's next daily digest. Written when the event happens rather
/// than gathered from the inbox later, so the digest sweep never has to read tenant rows.
/// </summary>
public sealed class ChatDigestEntry : EntityBase
{
    public Guid ChannelId { get; init; }
    public Guid? OrganizationId { get; init; }
    public required string Line { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

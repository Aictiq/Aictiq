using Aictiq.SharedKernel.Domain;

namespace Aictiq.Modules.Notifications.Domain;

public enum NotificationKind : short { Assigned, Mentioned, Commented, Transitioned, Claimed, SprintStarted, SprintCompleted, WikiMentioned, InviteAccepted }

public sealed class Notification : TenantEntity
{
    public required string UserId { get; init; }
    public Guid EventId { get; init; }
    public NotificationKind Kind { get; init; }
    public Guid? ProjectId { get; init; }
    public Guid? ItemId { get; init; }
    public string? ItemKey { get; init; }
    public required string Message { get; init; }
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

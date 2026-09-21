using System.ComponentModel.DataAnnotations;

namespace Aictiq.Modules.Notifications.Delivery;

/// <summary>Instance-wide emergency stop for notification email. Transactional mail
/// (password resets and invitations) is intentionally not governed by this option.</summary>
public sealed class NotificationEmailOptions
{
    public const string SectionName = "Notifications";
    public bool EmailEnabled { get; init; } = true;
    [Range(1, 60)] public int PresenceMinutes { get; init; } = 2;
}

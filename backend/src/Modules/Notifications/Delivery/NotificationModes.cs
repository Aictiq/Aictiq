using Aictiq.Modules.Notifications.Domain;
using Microsoft.EntityFrameworkCore;

namespace Aictiq.Modules.Notifications.Delivery;

/// <summary>
/// Resolves how a person hears about a kind on each channel: their own choice first, then
/// their organization's default, then - for a chat channel - whatever their email does, which
/// is what makes a newly connected channel start out matching email.
/// </summary>
public sealed class NotificationModes
{
    private readonly Dictionary<(string UserId, NotificationKind Kind), NotificationPreference> _preferences;
    private readonly Dictionary<(Guid OrganizationId, NotificationKind Kind), OrgNotificationDefault> _defaults;

    private NotificationModes(Dictionary<(string, NotificationKind), NotificationPreference> preferences,
        Dictionary<(Guid, NotificationKind), OrgNotificationDefault> defaults) => (_preferences, _defaults) = (preferences, defaults);

    /// <summary>Organization defaults are tenant rows: only the current tenant's are read.</summary>
    public static async Task<NotificationModes> LoadAsync(NotificationsDbContext db, IReadOnlyCollection<string> userIds,
        IReadOnlyCollection<Guid> organizationIds, CancellationToken cancellationToken)
    {
        var preferences = await db.Preferences.AsNoTracking().Where(p => userIds.Contains(p.UserId)).ToListAsync(cancellationToken);
        var defaults = organizationIds.Count == 0 ? [] : await db.OrgDefaults.AsNoTracking()
            .Where(d => organizationIds.Contains(d.OrganizationId)).ToListAsync(cancellationToken);
        return new NotificationModes(preferences.ToDictionary(p => (p.UserId, p.Kind)), defaults.ToDictionary(d => (d.OrganizationId, d.Kind)));
    }

    public EmailNotificationMode Email(string userId, Guid organizationId, NotificationKind kind) =>
        _preferences.GetValueOrDefault((userId, kind))?.EmailMode
        ?? _defaults.GetValueOrDefault((organizationId, kind))?.EmailMode
        ?? EmailNotificationMode.Immediate;

    public EmailNotificationMode Chat(string userId, Guid organizationId, NotificationKind kind, ChatChannelType type) =>
        _preferences.GetValueOrDefault((userId, kind))?.ChatMode(type)
        ?? _defaults.GetValueOrDefault((organizationId, kind))?.ChatMode(type)
        ?? Email(userId, organizationId, kind);
}

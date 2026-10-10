using Aictiq.Modules.Notifications.Domain;
using Aictiq.SharedKernel.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Aictiq.Modules.Notifications;

/// <summary>Applies current Factory permissions before an inbox is paged or counted.</summary>
public sealed class NotificationVisibility(NotificationsDbContext db, IProjectAccess access)
{
    public async Task<IQueryable<Notification>> QueryAsync(
        string userId, bool unreadOnly = false, CancellationToken cancellationToken = default)
    {
        // The inbox spans organizations, but always belongs to this one recipient.
        var query = db.Notifications.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.UserId == userId && (!unreadOnly || x.ReadAt == null));
        var runOrganizations = await query
            .Where(x => x.Kind == NotificationKind.RunSucceeded || x.Kind == NotificationKind.RunFailed
                || x.Kind == NotificationKind.RunNeedsInput)
            .Select(x => x.OrganizationId).Distinct().ToListAsync(cancellationToken);
        var permittedOrganizations = new List<Guid>();
        foreach (var organizationId in runOrganizations)
            if (await access.CanOperateFactoryAsync(userId, organizationId, cancellationToken))
                permittedOrganizations.Add(organizationId);

        return query.Where(x => (x.Kind != NotificationKind.RunSucceeded && x.Kind != NotificationKind.RunFailed
                && x.Kind != NotificationKind.RunNeedsInput)
            || permittedOrganizations.Contains(x.OrganizationId));
    }
}

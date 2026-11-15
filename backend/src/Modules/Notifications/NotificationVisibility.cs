using Aictiq.Modules.Notifications.Domain;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Aictiq.Modules.Notifications;

/// <summary>Applies current permissions before an inbox is paged or counted.</summary>
public sealed class NotificationVisibility(NotificationsDbContext db, IProjectAccess access, AmbientCurrentTenant tenant)
{
    public async Task<IQueryable<Notification>> QueryAsync(
        string userId, bool unreadOnly = false, CancellationToken cancellationToken = default)
    {
        // The inbox spans organizations, but always belongs to this one recipient.
        var query = db.Notifications.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.UserId == userId && (!unreadOnly || x.ReadAt == null));
        var runOrganizations = await query
            .Where(x => x.Kind == NotificationKind.RunSucceeded || x.Kind == NotificationKind.RunFailed
                || x.Kind == NotificationKind.RunNeedsInput || x.Kind == NotificationKind.RunRefined)
            .Select(x => x.OrganizationId).Distinct().ToListAsync(cancellationToken);
        var permittedOrganizations = new List<Guid>();
        foreach (var organizationId in runOrganizations)
            if (await access.CanOperateFactoryAsync(userId, organizationId, cancellationToken))
                permittedOrganizations.Add(organizationId);

        var assignmentProjects = await query.Where(x => x.Kind == NotificationKind.Assigned && x.ProjectId != null)
            .Select(x => new { x.OrganizationId, ProjectId = x.ProjectId!.Value }).Distinct().ToListAsync(cancellationToken);
        var permittedProjects = new List<Guid>();
        foreach (var project in assignmentProjects)
        {
            using var organizationScope = tenant.Use(project.OrganizationId);
            if (await access.GetProjectRoleAsync(userId, project.ProjectId, cancellationToken) is not null)
                permittedProjects.Add(project.ProjectId);
        }

        query = query.Where(x => x.Kind != NotificationKind.Assigned ||
            (x.ProjectId != null && permittedProjects.Contains(x.ProjectId.Value)));

        return query.Where(x => (x.Kind != NotificationKind.RunSucceeded && x.Kind != NotificationKind.RunFailed
                && x.Kind != NotificationKind.RunNeedsInput && x.Kind != NotificationKind.RunRefined)
            || permittedOrganizations.Contains(x.OrganizationId));
    }
}

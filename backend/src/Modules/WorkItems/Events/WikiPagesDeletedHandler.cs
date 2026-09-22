using Aictiq.Modules.WorkItems.Domain;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Events;
using Aictiq.SharedKernel.Storage;
using Aictiq.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Aictiq.Modules.WorkItems.Events;

/// <summary>
/// Removes the attachments of wiki pages that no longer exist, object first and row second.
/// A replay finds no rows and deleting a missing object is success, so it is idempotent.
/// </summary>
public sealed class WikiPagesDeletedHandler(WorkItemsDbContext db, ICurrentTenant currentTenant, IBlobStorage storage)
    : IDomainEventHandler<WikiPagesDeleted>
{
    public async Task HandleAsync(WikiPagesDeleted @event, CancellationToken cancellationToken)
    {
        using var tenant = currentTenant is AmbientCurrentTenant ambient ? ambient.Use(@event.OrganizationId) : null;
        var pageIds = @event.PageIds.ToArray();
        var attachments = await db.Attachments
            .Where(x => x.ProjectId == @event.ProjectId && x.WikiPageId != null && pageIds.Contains(x.WikiPageId.Value))
            .ToListAsync(cancellationToken);
        foreach (var attachment in attachments)
        {
            await storage.DeleteAsync(attachment.ObjectKey, cancellationToken);
            db.Attachments.Remove(attachment);
        }
        if (attachments.Count > 0) await db.SaveChangesAsync(cancellationToken);
    }
}

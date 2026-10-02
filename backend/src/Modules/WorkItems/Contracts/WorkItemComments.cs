using Aictiq.SharedKernel.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Aictiq.Modules.WorkItems.Contracts;

/// <summary>The WorkItems half of <see cref="IWorkItemComments"/>; tenant-filtered like every read here.</summary>
internal sealed class WorkItemComments(WorkItemsDbContext db) : IWorkItemComments
{
    public async Task<WorkItemCommentReference?> FindAsync(Guid commentId, CancellationToken cancellationToken = default) =>
        await db.Comments.AsNoTracking().Where(x => x.Id == commentId)
            .Select(x => new WorkItemCommentReference(
                x.Id, x.ItemId, x.ParentCommentId ?? x.Id, x.AuthorId, x.BodyMarkdown, x.DeletedAt != null))
            .FirstOrDefaultAsync(cancellationToken);
}

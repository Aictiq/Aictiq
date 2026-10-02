using Aictiq.Modules.WorkItems.Domain;
using Microsoft.EntityFrameworkCore;

namespace Aictiq.Modules.WorkItems.Events;

/// <summary>Where an agent answers the comment that asked it to work.</summary>
internal static class AgentReplies
{
    /// <summary>
    /// The live thread root of <paramref name="commentId"/> on the item, or null when the comment
    /// or its thread was deleted meanwhile - the answer then starts a thread of its own, since a
    /// deleted thread cannot be replied to.
    /// </summary>
    public static async Task<Comment?> ThreadAsync(WorkItemsDbContext db, Guid itemId, Guid commentId, CancellationToken ct)
    {
        var comment = await db.Comments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == commentId && x.ItemId == itemId, ct);
        if (comment is null) return null;
        var root = comment.ParentCommentId is { } rootId
            ? await db.Comments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == rootId, ct)
            : comment;
        return root is { DeletedAt: null } ? root : null;
    }
}

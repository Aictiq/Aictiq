using Microsoft.EntityFrameworkCore;
using Aictiq.Modules.WorkItems.Domain;
using Aictiq.SharedKernel.Contracts;

namespace Aictiq.Modules.WorkItems.Endpoints;

/// <summary>
/// What of the AI factory's working a caller may see on an item. Someone who may not operate
/// the factory - a stakeholder, a Guest - sees the work and none of the machinery: no agent's
/// comments, no thread an agent started, and no history row a run wrote about itself.
///
/// The rule is <see cref="IProjectAccess.CanOperateFactoryAsync"/>, the same door runs and
/// their logs already stand behind, so the two can never disagree about who is let in.
/// </summary>
internal static class FactoryVisibility
{
    /// <summary>
    /// The agents whose comments this caller may not see, or null when they may see
    /// everything. Resolved from the authors of <paramref name="candidates"/>, which must
    /// include the thread roots of any comment later filtered with it.
    /// </summary>
    public static async Task<string[]?> HiddenAuthorsAsync(IProjectAccess access, IUserDirectory directory,
        string userId, Guid organizationId, IQueryable<Comment> candidates, CancellationToken ct)
    {
        if (await access.CanOperateFactoryAsync(userId, organizationId, ct)) return null;
        var authors = await candidates.Select(x => x.AuthorId).Distinct().ToListAsync(ct);
        return [.. await directory.FilterAgentsAsync(authors, ct)];
    }

    /// <summary>Drops agents' comments and every reply in a thread an agent started.</summary>
    public static IQueryable<Comment> WithoutFactory(this IQueryable<Comment> comments, WorkItemsDbContext db, string[]? hiddenAuthors) =>
        hiddenAuthors is null or []
            ? comments
            : comments.Where(x => !hiddenAuthors.Contains(x.AuthorId)
                && (x.ParentCommentId == null || !db.Comments.Any(root => root.Id == x.ParentCommentId && hiddenAuthors.Contains(root.AuthorId))));

    /// <summary>Whether one comment is hidden from a caller who may not operate the factory.</summary>
    public static async Task<bool> HiddenAsync(IProjectAccess access, IUserDirectory directory, WorkItemsDbContext db,
        string userId, Comment comment, CancellationToken ct)
    {
        if (await access.CanOperateFactoryAsync(userId, comment.OrganizationId, ct)) return false;
        var rootAuthor = comment.ParentCommentId is { } rootId
            ? await db.Comments.AsNoTracking().Where(x => x.Id == rootId).Select(x => x.AuthorId).FirstOrDefaultAsync(ct)
            : null;
        string[] authors = rootAuthor is null ? [comment.AuthorId] : [comment.AuthorId, rootAuthor];
        return (await directory.FilterAgentsAsync(authors, ct)).Count > 0;
    }

    /// <summary>
    /// Drops what a run wrote about itself: its <c>run-finished</c> row and the rows of the
    /// same event that only make sense beside it (the pull request it opened, the move it
    /// was refused). The state change itself stays - that is what happened to the item.
    /// </summary>
    public static IQueryable<ItemHistory> WithoutFactory(this IQueryable<ItemHistory> rows, WorkItemsDbContext db, bool hide) =>
        !hide
            ? rows
            : rows.Where(x => x.Field != "run-finished"
                && !((x.Field == "pull-request-linked" || x.Field == "auto-transition-skipped")
                    && db.ItemHistory.Any(run => run.EventId == x.EventId && run.Field == "run-finished")));
}

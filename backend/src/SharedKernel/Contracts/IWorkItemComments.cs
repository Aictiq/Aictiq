namespace Aictiq.SharedKernel.Contracts;

/// <param name="ThreadId">The thread the comment belongs to: its own id for a comment that starts one.</param>
public sealed record WorkItemCommentReference(
    Guid Id, Guid ItemId, Guid ThreadId, string AuthorId, string BodyMarkdown, bool Deleted);

/// <summary>
/// Reads one comment for a module that reacts to it - Automation, when a comment asks an agent
/// to work. The <c>CommentAdded</c> event carries only an excerpt, sized for an email; the
/// instruction an agent follows is the whole text, read as it stands when the event is handled,
/// so a comment deleted in between asks for nothing.
/// </summary>
public interface IWorkItemComments
{
    Task<WorkItemCommentReference?> FindAsync(Guid commentId, CancellationToken cancellationToken = default);
}

/// <summary>No WorkItems module means there are no comments to read.</summary>
public sealed class NullWorkItemComments : IWorkItemComments
{
    public Task<WorkItemCommentReference?> FindAsync(Guid commentId, CancellationToken cancellationToken = default) =>
        Task.FromResult<WorkItemCommentReference?>(null);
}

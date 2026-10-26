namespace Aictiq.SharedKernel.Contracts;

/// <summary>A file attached to one question of a person's project chat.</summary>
public sealed record ProjectChatAttachment(Guid Id, Guid ChatMessageId, string FileName, string ContentType, long SizeBytes);

/// <summary>Why files could not be attached to a question, in words for the panel.</summary>
/// <param name="Field">The request field the problem belongs to: <c>attachmentIds</c>, or <c>plan</c> for the storage allowance.</param>
public sealed record ProjectChatAttachFailure(string Field, string Message, PlanLimitDecision? Plan = null);

/// <summary>
/// The files of a person's private project chat. Implemented by WorkItems, which owns
/// attachments; asked by Automation, which owns the chat. A chat file is its uploader's alone:
/// nobody else may download it, except the live project-chat run answering that person.
/// </summary>
public interface IProjectChatAttachments
{
    /// <summary>
    /// Binds the caller's own pending uploads in <paramref name="projectId"/> to the question
    /// <paramref name="chatMessageId"/> under the same storage allowance as any commit. Fails,
    /// binding nothing, when one is not a pending upload of theirs, cannot be read in chat or
    /// does not fit the allowance.
    /// </summary>
    Task<(IReadOnlyList<ProjectChatAttachment> Attached, ProjectChatAttachFailure? Failure)> AttachAsync(
        Guid projectId, string userId, Guid chatMessageId, IReadOnlyList<Guid> attachmentIds, CancellationToken ct = default);

    /// <summary>Returns files to pending, for a question that was refused after they were attached.</summary>
    Task DetachAsync(Guid chatMessageId, CancellationToken ct = default);

    /// <summary>The person's chat files in the project, in the order they were attached to their questions.</summary>
    Task<IReadOnlyList<ProjectChatAttachment>> ListAsync(Guid projectId, string userId, CancellationToken ct = default);

    /// <summary>Deletes the files of the given questions, or of the person's whole chat when <paramref name="chatMessageIds"/> is null.</summary>
    Task DeleteAsync(Guid projectId, string userId, IReadOnlyCollection<Guid>? chatMessageIds, CancellationToken ct = default);
}

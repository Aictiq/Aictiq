using Aictiq.Modules.WorkItems.Domain;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Aictiq.Modules.WorkItems.Access;

/// <summary>Chat files are ordinary attachments owned by a chat question instead of an item, comment or page.</summary>
public sealed class WorkItemsProjectChatAttachments(
    WorkItemsDbContext db, IBlobStorage storage, IPlanLimits planLimits, IOptions<AttachmentsOptions> options, TimeProvider clock)
    : IProjectChatAttachments
{
    public async Task<(IReadOnlyList<ProjectChatAttachment> Attached, ProjectChatAttachFailure? Failure)> AttachAsync(
        Guid projectId, string userId, Guid chatMessageId, IReadOnlyList<Guid> attachmentIds, CancellationToken ct = default)
    {
        var ids = attachmentIds.Distinct().ToArray();
        if (ids.Length == 0) return ([], null);
        if (ids.Length > ChatAttachmentFiles.MaxPerQuestion)
            return ([], Failure($"Attach at most {ChatAttachmentFiles.MaxPerQuestion} files to one question."));

        var attachments = await db.Attachments
            .Where(x => ids.Contains(x.Id) && x.Status == AttachmentStatus.Pending && x.UploadedBy == userId && x.ProjectId == projectId)
            .ToListAsync(ct);
        if (attachments.Count != ids.Length)
            return ([], Failure("A selected file is no longer uploaded (uploads expire after an hour). Remove it and attach it again."));

        foreach (var attachment in attachments.OrderBy(x => Array.IndexOf(ids, x.Id)))
        {
            if (!ChatAttachmentFiles.IsSupported(attachment.ContentType))
                return ([], Failure($"{attachment.FileName}: {ChatAttachmentFiles.UnsupportedType}"));
            var blob = await storage.HeadAsync(attachment.ObjectKey, ct);
            if (blob is null || blob.ContentLength != attachment.SizeBytes || blob.ContentLength > options.Value.MaxBytes)
                return ([], Failure($"{attachment.FileName} did not finish uploading. Remove it and attach it again."));
            // Images were decoded and re-encoded at upload; the rest is checked again here, since
            // an upload made for an item or by another client never went through the chat check.
            if (AttachmentImages.IsImage(attachment.ContentType)) continue;
            var read = await storage.OpenReadAsync(attachment.ObjectKey, ct);
            if (read is null) return ([], Failure($"{attachment.FileName} did not finish uploading. Remove it and attach it again."));
            byte[] bytes;
            await using (read.Content)
            {
                using var buffer = new MemoryStream();
                await read.Content.CopyToAsync(buffer, ct);
                bytes = buffer.ToArray();
            }
            if (ChatAttachmentFiles.Unreadable(attachment.FileName, attachment.ContentType, bytes) is { } reason)
                return ([], Failure(reason));
        }

        var organizationId = attachments[0].OrganizationId;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // The same lock, sum and commit as AttachmentEndpoints.CommitAsync, for all files at once.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtext({organizationId.ToString()}))", ct);
        var plan = await planLimits.CanStoreBytesAsync(organizationId, attachments.Sum(x => x.SizeBytes), ct);
        if (!plan.Allowed) return ([], new ProjectChatAttachFailure("plan", plan.Reason ?? "The storage allowance is used up.", plan));
        var now = clock.GetUtcNow();
        // Uploads finish in any order; a microsecond apart, the commit times keep the order the
        // person attached the files in, which is the order the chat lists them.
        var ordered = attachments.OrderBy(x => Array.IndexOf(ids, x.Id)).ToList();
        for (var index = 0; index < ordered.Count; index++)
        {
            ordered[index].Status = AttachmentStatus.Committed;
            ordered[index].ChatMessageId = chatMessageId;
            ordered[index].CommittedAt = now.AddTicks(index * TimeSpan.TicksPerMicrosecond);
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return (ordered.Select(View).ToList(), null);
    }

    public Task DetachAsync(Guid chatMessageId, CancellationToken ct = default) =>
        db.Attachments.Where(x => x.ChatMessageId == chatMessageId && x.Status == AttachmentStatus.Committed)
            .ExecuteUpdateAsync(set => set
                .SetProperty(x => x.Status, AttachmentStatus.Pending)
                .SetProperty(x => x.ChatMessageId, (Guid?)null)
                .SetProperty(x => x.CommittedAt, (DateTimeOffset?)null), ct);

    public async Task<IReadOnlyList<ProjectChatAttachment>> ListAsync(Guid projectId, string userId, CancellationToken ct = default) =>
        (await db.Attachments.AsNoTracking()
            .Where(x => x.ProjectId == projectId && x.UploadedBy == userId && x.ChatMessageId != null && x.Status == AttachmentStatus.Committed)
            .OrderBy(x => x.CommittedAt).ThenBy(x => x.CreatedAt)
            .ToListAsync(ct)).Select(View).ToList();

    public async Task DeleteAsync(Guid projectId, string userId, IReadOnlyCollection<Guid>? chatMessageIds, CancellationToken ct = default)
    {
        var ids = chatMessageIds?.ToArray();
        if (ids is { Length: 0 }) return;
        var doomed = await db.Attachments
            .Where(x => x.ProjectId == projectId && x.UploadedBy == userId && x.ChatMessageId != null
                && (ids == null || ids.Contains(x.ChatMessageId.Value)))
            .ToListAsync(ct);
        if (doomed.Count == 0) return;
        // The objects go through the outbox, as a deleted item attachment's do.
        foreach (var attachment in doomed)
        {
            attachment.Deleted();
            db.Attachments.Remove(attachment);
        }
        await db.SaveChangesAsync(ct);
    }

    private static ProjectChatAttachFailure Failure(string message) => new("attachmentIds", message);

    private static ProjectChatAttachment View(Attachment x) =>
        new(x.Id, x.ChatMessageId!.Value, x.FileName, x.ContentType, x.SizeBytes);
}

using Aictiq.Modules.WorkItems.Domain;
using Aictiq.Modules.WorkItems.Endpoints;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Storage;
using Microsoft.EntityFrameworkCore;

namespace Aictiq.Modules.WorkItems.Access;

/// <summary>
/// Gives an item created from project chat its own copies of the files its reporter shared in
/// that chat. A copy, not a shared object: the chat file stays private to its uploader and dies
/// with the chat, while the item's copy is an ordinary item attachment anyone who can see the
/// item can read, and outlives the chat. A ZIP is copied as the archive it is.
/// </summary>
public static class ChatAttachmentCopies
{
    public const int MaxPerItem = 20;

    public sealed record Copied(Guid Id, Guid SourceId, string FileName, string ContentType, long SizeBytes);

    public sealed record Failed(Guid SourceId, string? FileName, string Reason);

    /// <summary>
    /// The chat files <paramref name="ownerId"/> shared in <paramref name="projectId"/>, in the
    /// order asked, and the ids that are not one of them. Nobody else's chat file, and no file of
    /// another project, is ever found here: the same reach <see cref="ChatAttachmentAccess"/> gives.
    /// </summary>
    public static async Task<(IReadOnlyList<Attachment> Found, IReadOnlyList<Guid> Missing)> FindAsync(
        WorkItemsDbContext db, Guid projectId, string ownerId, IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        var rows = await db.Attachments.AsNoTracking()
            .Where(x => ids.Contains(x.Id) && x.ProjectId == projectId && x.UploadedBy == ownerId
                && x.ChatMessageId != null && x.Status == AttachmentStatus.Committed)
            .ToListAsync(ct);
        var found = ids.Select(id => rows.FirstOrDefault(x => x.Id == id)).OfType<Attachment>().ToList();
        return (found, ids.Except(found.Select(x => x.Id)).ToList());
    }

    /// <summary>
    /// Copies <paramref name="sources"/> onto <paramref name="item"/> as committed attachments
    /// uploaded by <paramref name="ownerId"/>. Each copy is staged as a pending row and object,
    /// then all are committed at once under the same lock, sum and allowance as any other commit;
    /// what does not fit, or could not be read, is reported per file and leaves nothing behind.
    /// </summary>
    public static async Task<(IReadOnlyList<Copied> Copied, IReadOnlyList<Failed> Failed)> CopyAsync(
        WorkItemsDbContext db, IBlobStorage storage, IPlanLimits planLimits, TimeProvider clock,
        WorkItem item, string ownerId, IReadOnlyList<Attachment> sources, CancellationToken ct)
    {
        var failed = new List<Failed>();
        var staged = new List<(Attachment Copy, Attachment Source)>();
        foreach (var source in sources)
        {
            var read = await storage.OpenReadAsync(source.ObjectKey, ct);
            if (read is null) { failed.Add(new(source.Id, source.FileName, "the chat file is no longer stored")); continue; }
            var id = Guid.CreateVersion7();
            var copy = new Attachment
            {
                Id = id, OrganizationId = item.OrganizationId, ProjectId = item.ProjectId,
                FileName = source.FileName, ContentType = source.ContentType, SizeBytes = source.SizeBytes, Sha256 = source.Sha256,
                UploadedBy = ownerId, CreatedAt = clock.GetUtcNow(),
                ObjectKey = AttachmentEndpoints.ObjectKey(item.OrganizationId, item.ProjectId, id, source.FileName),
            };
            // The row first, as at upload: a store write that fails leaves a pending row the
            // sweeper removes, never an object no row knows about.
            db.Attachments.Add(copy);
            await db.SaveChangesAsync(ct);
            try
            {
                await using (read.Content)
                {
                    using var buffer = new MemoryStream();
                    await read.Content.CopyToAsync(buffer, ct);
                    buffer.Position = 0;
                    await storage.PutAsync(copy.ObjectKey, buffer, copy.ContentType, ct);
                }
                staged.Add((copy, source));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await DiscardAsync(db, storage, [copy]);
                failed.Add(new(source.Id, source.FileName, "it could not be copied to the item"));
            }
        }
        if (staged.Count == 0) return ([], failed);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // The lock, sum and commit of AttachmentEndpoints.CommitAsync, for all copies at once.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtext({item.OrganizationId.ToString()}))", ct);
        var plan = await planLimits.CanStoreBytesAsync(item.OrganizationId, staged.Sum(x => x.Copy.SizeBytes), ct);
        if (!plan.Allowed)
        {
            await transaction.RollbackAsync(ct);
            await DiscardAsync(db, storage, staged.Select(x => x.Copy).ToList());
            var reason = plan.Reason ?? "the organization's storage allowance is used up";
            failed.AddRange(staged.Select(x => new Failed(x.Source.Id, x.Source.FileName, reason)));
            return ([], failed);
        }
        var now = clock.GetUtcNow();
        for (var index = 0; index < staged.Count; index++)
        {
            // A microsecond apart, so the item lists them in the order they were asked for.
            staged[index].Copy.ItemId = item.Id;
            staged[index].Copy.Status = AttachmentStatus.Committed;
            staged[index].Copy.CommittedAt = now.AddTicks(index * TimeSpan.TicksPerMicrosecond);
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return (staged.Select(x => new Copied(x.Copy.Id, x.Source.Id, x.Copy.FileName, x.Copy.ContentType, x.Copy.SizeBytes)).ToList(), failed);
    }

    /// <summary>
    /// The item page shows an item's files where its description links them, so the copies are
    /// linked there: images inline, everything else as a download. File names are user-controlled
    /// and escaped so one cannot break out of its link.
    /// </summary>
    public static string Section(string organizationSlug, IReadOnlyList<Copied> copied) =>
        "**Files from project chat**\n\n" + string.Join("\n\n", copied.Select(file =>
        {
            var link = $"[{Label(file.FileName)}](/api/v1/orgs/{organizationSlug}/attachments/{file.Id}/download)";
            return AttachmentImages.IsImage(file.ContentType) ? $"!{link}" : link;
        }));

    private static string Label(string fileName) =>
        string.Concat(fileName.Select(c => char.IsControl(c) ? "_" : "\\`*_[]()<>!#|".Contains(c) ? $"\\{c}" : c.ToString()));

    private static async Task DiscardAsync(WorkItemsDbContext db, IBlobStorage storage, IReadOnlyList<Attachment> copies)
    {
        foreach (var copy in copies)
        {
            await storage.DeleteAsync(copy.ObjectKey, CancellationToken.None);
            db.Attachments.Remove(copy);
        }
        await db.SaveChangesAsync(CancellationToken.None);
    }
}

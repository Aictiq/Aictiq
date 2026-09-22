using Aictiq.Modules.WorkItems.Contracts;
using Aictiq.SharedKernel.Domain;

namespace Aictiq.Modules.WorkItems.Domain;

public enum AttachmentStatus : short { Pending = 0, Committed = 1 }

/// <summary>Metadata for an object uploaded directly to object storage.</summary>
public sealed class Attachment : TenantEntity
{
    public Guid ProjectId { get; init; }
    public Guid? ItemId { get; set; }
    public Guid? CommentId { get; set; }
    public Guid? WikiPageId { get; set; }
    public required string ObjectKey { get; init; }
    public required string FileName { get; init; }
    public required string ContentType { get; init; }
    public long SizeBytes { get; set; }
    public string? Sha256 { get; set; }
    public required string UploadedBy { get; init; }
    public AttachmentStatus Status { get; set; } = AttachmentStatus.Pending;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? CommittedAt { get; set; }

    public void Deleted() => Raise(new AttachmentDeleted(ObjectKey));
}

namespace Aictiq.Modules.WorkItems;

/// <summary>Upload limits are server policy; the client-provided size is only an early rejection.</summary>
public sealed class AttachmentsOptions
{
    public const string SectionName = "Attachments";
    public long MaxBytes { get; set; } = 25 * 1024 * 1024;
    public string[] AllowedContentTypes { get; set; } =
    [
        "image/png", "image/jpeg", "image/gif", "image/webp", "application/pdf",
        "text/plain", "text/markdown", "application/zip"
    ];
    /// <summary>Wider images are scaled down to this width; every still image is stored as WebP.</summary>
    public int MaxImageWidth { get; set; } = 1920;
    public int WebpQuality { get; set; } = 80;
    /// <summary>Checked from the header before decoding, so a tiny file cannot claim a huge canvas.</summary>
    public long MaxImagePixels { get; set; } = 50_000_000;
    public TimeSpan PendingLifetime { get; set; } = TimeSpan.FromHours(1);
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromHours(1);
    public int SweepBatchSize { get; set; } = 100;
}

using System.IO.Compression;
using System.Text;

namespace Aictiq.Modules.WorkItems;

/// <summary>
/// What a project chat accepts: the files an agent can actually read - images, PDFs, plain
/// text and ZIP archives of those. Checked when the file is uploaded for chat and again when it
/// is attached to a question, so the panel says what is wrong instead of the agent quietly
/// answering without it. The runner extracts ZIPs within its own bounds; this only refuses
/// archives that hold nothing it could extract.
/// </summary>
public static class ChatAttachmentFiles
{
    public const int MaxPerQuestion = 5;

    /// <summary>A bigger central directory is a bomb or a whole disk, not context for a question.</summary>
    public const int MaxZipEntries = 1000;

    private static readonly string[] TextTypes = ["text/plain", "text/markdown", "text/csv", "text/tab-separated-values"];

    public const string UnsupportedType = "This file type cannot be read in chat. Attach an image, a PDF, a text file or a ZIP of those.";

    public static bool IsSupported(string contentType) =>
        AttachmentImages.IsImage(contentType)
        || string.Equals(contentType, "application/pdf", StringComparison.OrdinalIgnoreCase)
        || string.Equals(contentType, "application/zip", StringComparison.OrdinalIgnoreCase)
        || TextTypes.Contains(contentType, StringComparer.OrdinalIgnoreCase);

    /// <summary>Why the agent could not read <paramref name="bytes"/>, or null when it can.</summary>
    public static string? Unreadable(string fileName, string contentType, byte[] bytes)
    {
        if (!IsSupported(contentType)) return UnsupportedType;
        if (string.Equals(contentType, "application/pdf", StringComparison.OrdinalIgnoreCase))
        {
            // The header may follow a little junk, which readers tolerate within the first 1 KB.
            var head = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 1024));
            return head.Contains("%PDF-", StringComparison.Ordinal) ? null : $"{fileName} is not a readable PDF.";
        }
        if (string.Equals(contentType, "application/zip", StringComparison.OrdinalIgnoreCase)) return UnreadableZip(fileName, bytes);
        if (TextTypes.Contains(contentType, StringComparer.OrdinalIgnoreCase))
        {
            try { _ = new UTF8Encoding(false, throwOnInvalidBytes: true).GetCharCount(bytes); }
            catch (DecoderFallbackException) { return $"{fileName} is not UTF-8 text."; }
        }
        return null;
    }

    private static string? UnreadableZip(string fileName, byte[] bytes)
    {
        try
        {
            using var archive = new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read);
            if (archive.Entries.Count > MaxZipEntries) return $"{fileName} holds more than {MaxZipEntries:N0} entries.";
            var files = archive.Entries.Where(entry => !entry.FullName.EndsWith('/')).ToList();
            if (files.Count == 0) return $"{fileName} is an empty ZIP archive.";
            if (files.All(entry => entry.IsEncrypted)) return $"{fileName} is password-protected, so its files cannot be read.";
            return null;
        }
        catch (InvalidDataException)
        {
            return $"{fileName} is not a readable ZIP archive.";
        }
    }
}

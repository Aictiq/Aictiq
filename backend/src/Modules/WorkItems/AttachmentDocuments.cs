namespace Aictiq.Modules.WorkItems;

/// <summary>
/// Document types we can offer as downloads. An extension is a fallback for clients that
/// omit the MIME type, not a claim that the contents are safe to open or execute.
/// </summary>
public static class AttachmentDocuments
{
    private static readonly Dictionary<string, string> TypesByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".doc"] = "application/msword",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xls"] = "application/vnd.ms-excel",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".ppt"] = "application/vnd.ms-powerpoint",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        [".odt"] = "application/vnd.oasis.opendocument.text",
        [".ods"] = "application/vnd.oasis.opendocument.spreadsheet",
        [".odp"] = "application/vnd.oasis.opendocument.presentation",
        [".rtf"] = "application/rtf",
        [".csv"] = "text/csv",
        [".tsv"] = "text/tab-separated-values"
    };

    public static IEnumerable<string> ContentTypes => TypesByExtension.Values.Append("text/rtf");

    /// <summary>The resolved type must still pass the configured server allowlist.</summary>
    public static string? ResolveContentType(string? fileName, string? contentType)
    {
        if ((string.IsNullOrWhiteSpace(contentType) || contentType == "application/octet-stream") &&
            fileName is not null && TypesByExtension.TryGetValue(Path.GetExtension(fileName), out var documentType))
            return documentType;
        return contentType;
    }
}

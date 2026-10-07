namespace Aictiq.Modules.WorkItems;

/// <summary>
/// Document types we can offer as downloads, and the names browsers give the default types. An extension is a fallback for clients that
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

    /// <summary>The non-document defaults browsers and OSes also leave untyped, notably Markdown and ZIP.</summary>
    private static readonly Dictionary<string, string> BaseTypesByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".zip"] = "application/zip",
        [".pdf"] = "application/pdf",
        [".txt"] = "text/plain",
        [".md"] = "text/markdown",
        [".markdown"] = "text/markdown"
    };

    /// <summary>
    /// Other names for an allowed type. Windows reports a ZIP as <c>application/x-zip-compressed</c>,
    /// so without this the default allowlist refused every ZIP picked there.
    /// </summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["application/x-zip-compressed"] = "application/zip",
        ["application/x-zip"] = "application/zip",
        ["multipart/x-zip"] = "application/zip",
        ["text/x-markdown"] = "text/markdown"
    };

    public static IEnumerable<string> ContentTypes => TypesByExtension.Values.Append("text/rtf");

    /// <summary>The resolved type must still pass the configured server allowlist.</summary>
    public static string? ResolveContentType(string? fileName, string? contentType)
    {
        if (contentType is not null && Aliases.TryGetValue(contentType, out var canonical)) return canonical;
        if ((string.IsNullOrWhiteSpace(contentType) || contentType == "application/octet-stream") && fileName is not null)
        {
            var extension = Path.GetExtension(fileName);
            if (TypesByExtension.TryGetValue(extension, out var documentType) ||
                BaseTypesByExtension.TryGetValue(extension, out documentType))
                return documentType;
        }
        return contentType;
    }
}

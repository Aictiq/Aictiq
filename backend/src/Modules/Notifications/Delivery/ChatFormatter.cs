using System.Net;
using System.Text;
using Aictiq.Modules.Notifications.Domain;

namespace Aictiq.Modules.Notifications.Delivery;

/// <summary>What a chat message says, before it is written in one platform's markup.</summary>
public sealed record ChatMessage(string Title, string? Summary, string? Url, string? UrlLabel = null);

/// <summary>
/// Each platform has its own markup and its own idea of what needs escaping; text from
/// items and runs is user content, so it is always escaped, never trusted as markup.
/// </summary>
public static class ChatFormatter
{
    /// <summary>Discord's hard limit; Telegram's is 4096 and Slack's far higher.</summary>
    public static int MaxLength(ChatChannelType type) => type switch
    {
        ChatChannelType.Discord => 2000,
        ChatChannelType.Telegram => 4096,
        _ => 12000
    };

    public static string Format(ChatChannelType type, ChatMessage message)
    {
        var text = new StringBuilder(Bold(type, message.Title));
        if (!string.IsNullOrWhiteSpace(message.Summary)) text.Append('\n').Append(Escape(type, Excerpt(message.Summary, 600)));
        if (SafeUrl(message.Url) is { } url) text.Append('\n').Append(Link(type, url, message.UrlLabel ?? "Open in Aictiq"));
        return Truncate(type, text.ToString());
    }

    /// <summary>One bullet of a digest: the title, linked when there is somewhere to go.</summary>
    public static string Line(ChatChannelType type, ChatMessage message) =>
        "• " + (SafeUrl(message.Url) is { } url ? Link(type, url, message.Title) : Escape(type, message.Title));

    public static string Digest(ChatChannelType type, string heading, IReadOnlyList<string> lines)
    {
        var text = new StringBuilder(Bold(type, heading));
        var limit = MaxLength(type) - 40;
        var written = 0;
        foreach (var line in lines)
        {
            if (text.Length + line.Length + 1 > limit) break;
            text.Append('\n').Append(line); written++;
        }
        if (written < lines.Count) text.Append('\n').Append(Escape(type, $"…and {lines.Count - written} more in your Aictiq inbox."));
        return text.ToString();
    }

    private static string Bold(ChatChannelType type, string text) => type switch
    {
        ChatChannelType.Telegram => $"<b>{Escape(type, text)}</b>",
        ChatChannelType.Slack => $"*{Escape(type, text)}*",
        _ => $"**{Escape(type, text)}**"
    };

    private static string Link(ChatChannelType type, string url, string label) => type switch
    {
        ChatChannelType.Telegram => $"<a href=\"{WebUtility.HtmlEncode(url)}\">{Escape(type, label)}</a>",
        ChatChannelType.Slack => $"<{url}|{Escape(type, label).Replace("|", "¦")}>",
        // Angle brackets keep Discord from unfurling a preview card under every message.
        _ => $"[{Escape(type, label).Replace("]", ")")}](<{url}>)"
    };

    internal static string Escape(ChatChannelType type, string text) => type switch
    {
        ChatChannelType.Telegram => WebUtility.HtmlEncode(text),
        ChatChannelType.Slack => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;"),
        _ => EscapeMarkdown(text)
    };

    private static string EscapeMarkdown(string text)
    {
        var escaped = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is '\\' or '*' or '_' or '~' or '`' or '|' or '>' or '#' or '[' or ']' or '(' or ')' or '<' or '@') escaped.Append('\\');
            escaped.Append(c);
        }
        return escaped.ToString();
    }

    private static string? SafeUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" ? uri.AbsoluteUri : null;

    private static string Excerpt(string text, int max)
    {
        text = text.Trim();
        return text.Length <= max ? text : text[..max].TrimEnd() + "…";
    }

    private static string Truncate(ChatChannelType type, string text) =>
        text.Length <= MaxLength(type) ? text : text[..(MaxLength(type) - 1)] + "…";
}

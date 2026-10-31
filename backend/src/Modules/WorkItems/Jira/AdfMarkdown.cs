using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Aictiq.Modules.WorkItems.Jira;

/// <summary>
/// Jira Cloud's rich text (Atlassian Document Format) as Markdown. The node types are the
/// documented ones; anything else keeps its text and loses its decoration, so nothing a person
/// wrote is dropped. A plain string (wiki markup from older content) gets the common wiki
/// conversions.
/// </summary>
public static class AdfMarkdown
{
    public static string Convert(JsonElement document)
    {
        if (document.ValueKind == JsonValueKind.String) return Wiki(document.GetString() ?? "");
        if (document.ValueKind != JsonValueKind.Object) return "";
        var output = new StringBuilder();
        Blocks(document, output, "");
        return Regex.Replace(output.ToString(), @"\n{3,}", "\n\n").Trim();
    }

    private static void Blocks(JsonElement parent, StringBuilder output, string indent)
    {
        foreach (var node in Children(parent)) Block(node, output, indent);
    }

    private static void Block(JsonElement node, StringBuilder output, string indent)
    {
        switch (Type(node))
        {
            case "paragraph":
                output.Append(indent).Append(Inline(node).Replace("\n", "\n" + indent)).Append("\n\n");
                break;
            case "heading":
                var level = Attr(node, "level") is { } l && int.TryParse(l, out var n) ? Math.Clamp(n, 1, 6) : 2;
                output.Append(indent).Append('#', level).Append(' ').Append(Inline(node)).Append("\n\n");
                break;
            case "bulletList":
            case "orderedList":
                List(node, output, indent);
                output.Append('\n');
                break;
            case "taskList":
                foreach (var item in Children(node))
                    output.Append(indent).Append(Attr(item, "state") == "DONE" ? "- [x] " : "- [ ] ").Append(Inline(item)).Append('\n');
                output.Append('\n');
                break;
            case "codeBlock":
                var language = Attr(node, "language") ?? "";
                var code = string.Concat(Children(node).Select(x => Str(x, "text")));
                var fence = code.Contains("```", StringComparison.Ordinal) ? "~~~~" : "```";
                output.Append(indent).Append(fence).Append(language).Append('\n').Append(code).Append('\n').Append(indent).Append(fence).Append("\n\n");
                break;
            case "blockquote":
            case "panel":
                var inner = new StringBuilder();
                Blocks(node, inner, "");
                foreach (var line in inner.ToString().TrimEnd().Split('\n')) output.Append(indent).Append("> ").Append(line).Append('\n');
                output.Append('\n');
                break;
            case "rule":
                output.Append(indent).Append("---\n\n");
                break;
            case "table":
                Table(node, output, indent);
                break;
            case "mediaSingle":
            case "mediaGroup":
                foreach (var media in Children(node)) output.Append(indent).Append(Media(media)).Append('\n');
                output.Append('\n');
                break;
            case "expand":
            case "nestedExpand":
                if (Attr(node, "title") is { Length: > 0 } title) output.Append(indent).Append("**").Append(Escape(title)).Append("**\n\n");
                Blocks(node, output, indent);
                break;
            default:
                // An unknown block still has text worth keeping.
                if (Children(node).Any(x => Type(x) is "text" or "hardBreak" or "mention" or "emoji")) output.Append(indent).Append(Inline(node)).Append("\n\n");
                else Blocks(node, output, indent);
                break;
        }
    }

    private static void List(JsonElement list, StringBuilder output, string indent)
    {
        var ordered = Type(list) == "orderedList";
        var number = Attr(list, "order") is { } start && int.TryParse(start, out var first) ? first : 1;
        foreach (var item in Children(list))
        {
            var marker = ordered ? $"{number++}. " : "- ";
            var body = new StringBuilder();
            foreach (var child in Children(item))
            {
                if (Type(child) is "bulletList" or "orderedList") List(child, body, new string(' ', marker.Length));
                else Block(child, body, "");
            }
            var lines = body.ToString().TrimEnd().Split('\n');
            output.Append(indent).Append(marker).Append(lines[0].TrimStart()).Append('\n');
            foreach (var line in lines.Skip(1).Where(x => x.Length > 0)) output.Append(indent).Append(line.StartsWith(' ') ? line : new string(' ', marker.Length) + line).Append('\n');
        }
    }

    private static void Table(JsonElement table, StringBuilder output, string indent)
    {
        var rows = Children(table).Select(row => Children(row).Select(cell =>
        {
            var text = new StringBuilder();
            Blocks(cell, text, "");
            return text.ToString().Trim().Replace("\n", "<br>").Replace("|", "\\|");
        }).ToList()).Where(r => r.Count > 0).ToList();
        if (rows.Count == 0) return;
        var width = rows.Max(r => r.Count);
        foreach (var (row, index) in rows.Select((r, i) => (r, i)))
        {
            output.Append(indent).Append("| ").Append(string.Join(" | ", row.Concat(Enumerable.Repeat("", width - row.Count)))).Append(" |\n");
            if (index == 0) output.Append(indent).Append('|').Append(string.Concat(Enumerable.Repeat(" --- |", width))).Append('\n');
        }
        output.Append('\n');
    }

    private static string Inline(JsonElement parent)
    {
        var output = new StringBuilder();
        foreach (var node in Children(parent))
        {
            switch (Type(node))
            {
                case "text": output.Append(Marks(node, Str(node, "text"))); break;
                case "hardBreak": output.Append("  \n"); break;
                case "mention": output.Append(Escape(Attr(node, "text") is { Length: > 0 } text ? text.StartsWith('@') ? text : "@" + text : "@someone")); break;
                case "emoji": output.Append(Attr(node, "text") ?? Attr(node, "shortName") ?? ""); break;
                case "inlineCard":
                case "blockCard":
                    if (Attr(node, "url") is { } url) output.Append('<').Append(url).Append('>');
                    break;
                case "date":
                    if (Attr(node, "timestamp") is { } stamp && long.TryParse(stamp, out var ms)) output.Append(DateTimeOffset.FromUnixTimeMilliseconds(ms).ToString("yyyy-MM-dd"));
                    break;
                case "status": output.Append('`').Append(Attr(node, "text") ?? "").Append('`'); break;
                case "mediaInline": output.Append(Media(node)); break;
                default: output.Append(Inline(node)); break;
            }
        }
        return output.ToString();
    }

    private static string Marks(JsonElement node, string text)
    {
        if (text.Length == 0 || !node.TryGetProperty("marks", out var marks) || marks.ValueKind != JsonValueKind.Array) return Escape(text);
        var kinds = marks.EnumerateArray().Select(m => (Type: Type(m), Href: m.TryGetProperty("attrs", out var a) ? Str(a, "href") : "")).ToList();
        if (kinds.Any(k => k.Type == "code")) return "`" + text.Replace("`", "'") + "`";
        var result = Escape(text);
        // Emphasis must hug the words: "** bold**" is not bold in Markdown.
        var lead = result.Length - result.TrimStart().Length; var trail = result.Length - result.TrimEnd().Length;
        var core = result.Trim();
        if (core.Length == 0) return result;
        foreach (var kind in kinds)
        {
            core = kind.Type switch
            {
                "strong" => $"**{core}**",
                "em" => $"*{core}*",
                "strike" => $"~~{core}~~",
                "link" when IsSafeLink(kind.Href) => $"[{core}]({kind.Href.Replace(")", "%29").Replace(" ", "%20")})",
                _ => core,
            };
        }
        return new string(' ', lead) + core + new string(' ', trail);
    }

    private static string Media(JsonElement node) =>
        Attr(node, "alt") is { Length: > 0 } alt ? $"_[attachment: {Escape(alt)}]_" : "_[attachment]_";

    private static bool IsSafeLink(string href) => Uri.TryCreate(href, UriKind.Absolute, out var url) && url.Scheme is "http" or "https" or "mailto";

    private static readonly Regex Special = new(@"([\\`*_\[\]<>#|])", RegexOptions.Compiled);
    private static string Escape(string text) => Special.Replace(text, @"\$1");

    private static string Wiki(string text)
    {
        var markdown = Regex.Replace(text, @"^h([1-6])\.\s*", m => new string('#', int.Parse(m.Groups[1].Value)) + " ", RegexOptions.Multiline);
        markdown = Regex.Replace(markdown, @"\{code(?::([a-zA-Z0-9]+))?[^}]*\}([\s\S]*?)\{code\}", m => $"```{m.Groups[1].Value}\n{m.Groups[2].Value.Trim('\n')}\n```");
        markdown = Regex.Replace(markdown, @"\{noformat\}([\s\S]*?)\{noformat\}", m => $"```\n{m.Groups[1].Value.Trim('\n')}\n```");
        markdown = Regex.Replace(markdown, @"^\s*\*\s+", "- ", RegexOptions.Multiline);
        markdown = Regex.Replace(markdown, @"^\s*#\s+", "1. ", RegexOptions.Multiline);
        markdown = Regex.Replace(markdown, @"(?<![\w*])\*(\S[^*\n]*?)\*(?![\w*])", "**$1**");
        markdown = Regex.Replace(markdown, @"\{\{([^}]+)\}\}", "`$1`");
        markdown = Regex.Replace(markdown, @"\[([^|\]]+)\|(https?://[^\]]+)\]", "[$1]($2)");
        return markdown.Trim();
    }

    private static IEnumerable<JsonElement> Children(JsonElement node) =>
        node.ValueKind == JsonValueKind.Object && node.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array ? content.EnumerateArray() : [];

    private static string Type(JsonElement node) => Str(node, "type");
    private static string Str(JsonElement node, string name) => node.ValueKind == JsonValueKind.Object && node.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string? Attr(JsonElement node, string name) =>
        node.ValueKind == JsonValueKind.Object && node.TryGetProperty("attrs", out var attrs) && attrs.ValueKind == JsonValueKind.Object && attrs.TryGetProperty(name, out var value)
            ? value.ValueKind switch { JsonValueKind.String => value.GetString(), JsonValueKind.Number => value.GetRawText(), _ => null } : null;
}

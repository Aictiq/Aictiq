using System.Text.RegularExpressions;

namespace Aictiq.Modules.Integrations;

/// <summary>Extracts work-item references from GitHub text without treating examples or links as work.</summary>
public static partial class ReferenceParser
{
    public sealed record Reference(string? ProjectKey, int ItemNumber);

    [GeneratedRegex("(?s)(```.*?```|~~~.*?~~~)|(?:https?://|www\\.)\\S+", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex IgnoredText();

    [GeneratedRegex("(?<![A-Z0-9-])(?<key>[A-Z][A-Z0-9]{1,9})-(?<number>\\d+)(?![A-Z0-9-])", RegexOptions.Compiled)]
    private static partial Regex KeyedReference();

    [GeneratedRegex("(?<![A-Z0-9])#(?<number>\\d+)(?!\\d)", RegexOptions.Compiled)]
    private static partial Regex LocalReference();

    public static IReadOnlyList<Reference> Parse(string? text, bool allowLocalReferences)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        // Preserve offsets so the regular expressions remain simple, while making ignored
        // ranges impossible to match. A URL or fenced sample must never create a link.
        var searchable = text.ToCharArray();
        foreach (Match match in IgnoredText().Matches(text))
            for (var i = match.Index; i < match.Index + match.Length; i++) searchable[i] = ' ';
        var source = new string(searchable);
        var references = new List<Reference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in KeyedReference().Matches(source))
            Add(match.Groups["key"].Value, match.Groups["number"].Value);
        if (allowLocalReferences)
            foreach (Match match in LocalReference().Matches(source)) Add(null, match.Groups["number"].Value);

        return references;

        void Add(string? key, string number)
        {
            if (!int.TryParse(number, out var itemNumber) || itemNumber <= 0) return;
            var dedupe = $"{key?.ToUpperInvariant() ?? "#"}-{itemNumber}";
            if (seen.Add(dedupe)) references.Add(new Reference(key?.ToUpperInvariant(), itemNumber));
        }
    }

    /// <summary>Branches deliberately accept lower-case keys, unlike prose references.</summary>
    public static IReadOnlyList<Reference> ParseBranch(string? branch)
    {
        if (string.IsNullOrWhiteSpace(branch)) return [];
        var references = new List<Reference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(branch, "(?<![A-Za-z0-9])(?<key>[A-Za-z][A-Za-z0-9]{1,9})-(?<number>\\d+)(?![A-Za-z0-9])"))
        {
            if (!int.TryParse(match.Groups["number"].Value, out var number) || number <= 0) continue;
            var key = match.Groups["key"].Value.ToUpperInvariant();
            if (seen.Add($"{key}-{number}")) references.Add(new Reference(key, number));
        }
        return references;
    }
}

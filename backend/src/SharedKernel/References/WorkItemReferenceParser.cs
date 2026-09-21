using System.Text.RegularExpressions;

namespace Aictiq.SharedKernel.References;

/// <summary>Extracts project work-item keys from user-authored text and branch names.</summary>
public static partial class WorkItemReferenceParser
{
    public sealed record Reference(string? ProjectKey, int ItemNumber);

    [GeneratedRegex("(?<![A-Z0-9-])(?<key>[A-Z][A-Z0-9]{1,9})-(?<number>\\d+)(?![A-Z0-9-])", RegexOptions.Compiled)]
    private static partial Regex Keyed();
    [GeneratedRegex("(?<![A-Za-z0-9])(?<key>[A-Za-z][A-Za-z0-9]{1,9})-(?<number>\\d+)(?![A-Za-z0-9])", RegexOptions.Compiled)]
    private static partial Regex BranchKeyed();

    public static IReadOnlyList<Reference> Parse(string? text, bool caseInsensitive = false)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var matches = (caseInsensitive ? BranchKeyed() : Keyed()).Matches(text);
        var result = new List<Reference>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in matches)
        {
            if (!int.TryParse(match.Groups["number"].Value, out var number) || number < 1) continue;
            var key = match.Groups["key"].Value.ToUpperInvariant();
            if (seen.Add($"{key}-{number}")) result.Add(new Reference(key, number));
        }
        return result;
    }
}

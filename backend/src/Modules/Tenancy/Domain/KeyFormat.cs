using System.Text.RegularExpressions;

namespace Aictiq.Modules.Tenancy.Domain;

/// <summary>
/// The shape of a short, quotable code - a project's key and a team's alike.
///
/// One rule in one place because it is the same rule: upper case and digits, starting
/// with a letter, so that <c>ACME-123</c> parses unambiguously and a code can never be
/// mistaken for the number beside it.
/// </summary>
public static partial class KeyFormat
{
    public const int MinLength = 2;
    public const int MaxLength = 10;

    /// <summary><c>^[A-Z][A-Z0-9]{1,9}$</c>.</summary>
    [GeneratedRegex(@"^[A-Z][A-Z0-9]{1,9}$")]
    private static partial Regex Pattern { get; }

    /// <summary>The SQL form of <see cref="Pattern"/>, for the check constraints.</summary>
    public const string CheckConstraintPattern = "^[A-Z][A-Z0-9]{1,9}$";

    public static bool IsWellFormed(string key) => Pattern.IsMatch(key);

    /// <summary>
    /// Suggests a key from a display name: initials for several words, the letters for
    /// one. Only a suggestion - the caller may send whatever they like, and the endpoint
    /// resolves collisions.
    /// </summary>
    public static string Suggest(string name)
    {
        var words = name
            .Split([' ', '-', '_', '.', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(word => new string([.. word.Where(char.IsAsciiLetterOrDigit)]).ToUpperInvariant())
            .Where(word => word.Length > 0)
            .ToList();

        var candidate = words.Count switch
        {
            0 => "",
            1 => words[0],
            _ => new string([.. words.Select(word => word[0])]),
        };

        // A key has to start with a letter, and trimming digits off the front is kinder
        // than refusing a name that happens to begin with one.
        candidate = candidate.TrimStart('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');

        return candidate.Length > MaxLength ? candidate[..MaxLength] : candidate;
    }
}

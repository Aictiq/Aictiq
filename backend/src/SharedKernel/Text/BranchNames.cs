using System.Text.RegularExpressions;

namespace Aictiq.SharedKernel.Text;

/// <summary>
/// The branch name agent work uses: the item's key in lower case followed by a slug of
/// the title - <c>acme-123-fix-login-redirect</c>. Living in SharedKernel because two
/// modules name branches (WorkItems' create-branch endpoint and Automation's run
/// dispatch) and an agent's branches must not change shape with the surface that mints them.
/// </summary>
public static partial class BranchNames
{
    public static string For(string itemKey, string title)
    {
        var slug = NonSlug().Replace(title.ToLowerInvariant(), "-").Trim('-');
        return $"{itemKey.ToLowerInvariant()}-{(string.IsNullOrEmpty(slug) ? "work" : slug[..Math.Min(40, slug.Length)])}";
    }

    /// <summary>
    /// The branch a follow-up starts when the pull request of <paramref name="branch"/> was merged
    /// or closed: the same name with its number counted up - <c>acme-123-fix</c> becomes
    /// <c>acme-123-fix-2</c>, and that one <c>acme-123-fix-3</c>.
    /// </summary>
    public static string Next(string branch)
    {
        var numbered = Numbered().Match(branch);
        return numbered.Success && int.TryParse(numbered.Groups["n"].Value, out var n) && n < 1000
            ? $"{numbered.Groups["stem"].Value}-{n + 1}"
            : $"{branch}-2";
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlug();

    [GeneratedRegex(@"^(?<stem>.+)-(?<n>[2-9]|[1-9][0-9]{1,2})$")]
    private static partial Regex Numbered();
}

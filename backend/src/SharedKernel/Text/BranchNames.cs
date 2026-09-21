using System.Text.RegularExpressions;

namespace Aictiq.SharedKernel.Text;

/// <summary>
/// The branch name agent work uses: the item's key in lower case followed by a slug of
/// the title — <c>acme-123-fix-login-redirect</c>. Living in SharedKernel because two
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

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlug();
}

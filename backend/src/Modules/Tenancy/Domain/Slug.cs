using System.Text;
using System.Text.RegularExpressions;

namespace Aictiq.Modules.Tenancy.Domain;

/// <summary>
/// Organization slugs: the URL segment that identifies a tenant.
///
/// Two rules do the work. A slug must be a plain lowercase kebab token, because it ends
/// up in URLs, in agent configuration and in people's muscle memory; and it must not
/// collide with a path the application itself owns, because <c>/api</c> as an
/// organization slug is a routing bug waiting for a deployment.
/// </summary>
public static partial class Slug
{
    public const int MinLength = 2;
    public const int MaxLength = 40;

    /// <summary>
    /// Paths the product uses, or plausibly will. Reserving generously is cheap - the
    /// cost is one rejected slug at signup; the cost of the reverse is a route that
    /// cannot be added without breaking an existing customer's URLs.
    /// </summary>
    public static readonly IReadOnlySet<string> Reserved = new HashSet<string>(StringComparer.Ordinal)
    {
        "about", "account", "admin", "agent", "agents", "api", "app", "assets", "auth",
        "billing", "blog", "board", "cdn", "changelog", "cli", "contact", "dashboard",
        "dev", "docs", "download", "email", "explore", "favicon", "features", "feed",
        "health", "help", "home", "hubs", "images", "img", "index", "invite", "invites",
        "invitation", "invitations", "aictiq", "join", "legal", "login", "logout", "mail",
        "mcp", "me", "media", "new", "news", "notifications", "null", "oauth", "onboarding",
        "org", "orgs", "organization", "organizations", "password", "pricing", "privacy",
        "profile", "project", "projects", "public", "register", "reset", "root", "s3",
        "search", "security", "session", "settings", "setup", "signin", "signout", "signup",
        "sitemap", "static", "status", "support", "system", "team", "teams", "terms", "test",
        "undefined", "user", "users", "webhook", "webhooks", "wiki", "www",
    };

    /// <summary>The shape the database also enforces (see <c>ck_organizations_slug_format</c>).</summary>
    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]*[a-z0-9])?$")]
    private static partial Regex Shape { get; }

    public static bool IsWellFormed(string? slug) =>
        slug is { Length: >= MinLength and <= MaxLength }
        && Shape.IsMatch(slug)
        && !slug.Contains("--", StringComparison.Ordinal);

    public static bool IsReserved(string slug) => Reserved.Contains(slug);

    /// <summary>
    /// Best-effort slug for a display name. Accents are folded rather than dropped, so
    /// "Ćuljak Software" becomes "culjak-software" instead of "uljak-software".
    /// Returns an empty string when nothing usable survives (a name of only emoji, say),
    /// which the caller turns into a generated fallback rather than a validation error.
    /// </summary>
    public static string From(string name)
    {
        var folded = name.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(folded.Length);

        foreach (var rune in folded.EnumerateRunes())
        {
            var category = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(rune.Value);
            if (category == System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            var lowered = Rune.ToLowerInvariant(rune);
            if (Rune.IsLetterOrDigit(lowered) && lowered.IsAscii)
            {
                builder.Append(lowered);
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        var slug = builder.ToString().Trim('-');
        return slug.Length > MaxLength ? slug[..MaxLength].Trim('-') : slug;
    }
}

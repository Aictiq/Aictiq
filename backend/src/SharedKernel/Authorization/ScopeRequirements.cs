namespace Aictiq.SharedKernel.Authorization;

/// <summary>The scopes a personal access token can carry.</summary>
public static class Scopes
{
    public const string Read = "read";
    public const string Write = "write";
    public const string Admin = "admin";

    /// <summary>Access to the MCP endpoint. Separate so an agent token can be MCP-only.</summary>
    public const string Mcp = "mcp";

    public static readonly string[] All = [Read, Write, Admin, Mcp];
}

public static class ScopeRequirements
{
    /// <summary>
    /// Whether a principal may perform an action needing <paramref name="required"/>.
    ///
    /// A browser session carries no scopes at all, and that means *unscoped* — the user's
    /// role already decides what they may do. Scopes only ever narrow a token: they exist
    /// so a PAT can be weaker than its owner, never stronger. So an empty set passes and a
    /// non-empty one must contain the scope (or `admin`, which implies the rest).
    /// </summary>
    public static bool IsSatisfiedBy(IReadOnlyCollection<string> granted, string required)
    {
        if (granted.Count == 0)
        {
            return true;
        }

        return granted.Contains(required, StringComparer.Ordinal)
            || granted.Contains(Scopes.Admin, StringComparer.Ordinal);
    }
}

namespace Aictiq.SharedKernel;

/// <summary>
/// Roles drive endpoint authorization; row-level data scoping is separate (see the
/// tenancy query filters for the pattern).
/// </summary>
public static class Roles
{
    public const string Admin = "Admin";
    public const string User = "User";

    public static readonly string[] All = [Admin, User];
}

/// <summary>Authorization policy names registered in the Api composition root.</summary>
public static class Policies
{
    public const string Admin = "admin";
}

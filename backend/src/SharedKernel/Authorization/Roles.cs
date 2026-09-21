namespace Aictiq.SharedKernel.Authorization;

/// <summary>
/// Organization roles. Ordered by authority so a requirement is a simple comparison:
/// a lower number outranks a higher one.
/// </summary>
public enum OrgRole
{
    Owner = 0,
    Admin = 1,
    Member = 2,

    /// <summary>Read and comment. Cannot create projects or see billing.</summary>
    Guest = 3,
}

/// <summary>Project roles, ordered the same way.</summary>
public enum ProjectRole
{
    Admin = 0,
    Member = 1,

    /// <summary>Read and comment only.</summary>
    Guest = 2,
}

public static class RoleExtensions
{
    /// <summary>
    /// True when <paramref name="actual"/> is at least as privileged as
    /// <paramref name="required"/>. Roles are ranked, so this is a comparison rather than
    /// a set of hard-coded pairs that drift as roles are added.
    /// </summary>
    public static bool Satisfies(this OrgRole actual, OrgRole required) => actual <= required;

    public static bool Satisfies(this ProjectRole actual, ProjectRole required) => actual <= required;
}

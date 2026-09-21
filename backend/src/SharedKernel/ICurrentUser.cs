namespace Aictiq.SharedKernel;

public interface ICurrentUser
{
    string? UserId { get; }
    string? UserName { get; }
    IReadOnlyCollection<string> Roles { get; }

    /// <summary>
    /// True for agent identities (bots owned by a human). Agents are ordinary
    /// members permission-wise; this exists so their actions can be *labelled* as theirs
    /// everywhere — a comment from an agent must never look like one from a person.
    /// </summary>
    bool IsAgent { get; }

    /// <summary>
    /// Scopes from a personal access token. Empty for a browser session, which
    /// is unscoped by definition — so an empty set means "not scope-limited", not
    /// "permitted nothing"; <see cref="Authorization.ScopeRequirements"/> encodes that.
    /// </summary>
    IReadOnlyCollection<string> Scopes { get; }

    /// <summary>The organization the credential is bound to, when it is bound to one.</summary>
    Guid? OrganizationId { get; }
}

/// <summary>Identity of background processes (Workers); audit rows get UserId = null, UserName "system".</summary>
public sealed class SystemCurrentUser : ICurrentUser
{
    public string? UserId => null;
    public string? UserName => "system";
    public IReadOnlyCollection<string> Roles => [];
    public bool IsAgent => false;
    public IReadOnlyCollection<string> Scopes => [];
    public Guid? OrganizationId => null;
}

namespace Aictiq.SharedKernel.Authorization;

/// <summary>
/// The claim names a Aictiq principal carries, beyond the standard <c>sub</c>,
/// <c>name</c> and <c>role</c>.
///
/// They live in SharedKernel because the module that <em>writes</em> them (Identity, in
/// both the JWT and the personal-access-token handler) and the host that <em>reads</em>
/// them (the API's <c>ICurrentUser</c>) must agree on the strings, and a constant in one
/// of them would be a copy in the other.
///
/// Short names throughout, never <c>ClaimTypes.*</c> URIs: the bearer handler runs with
/// <c>MapInboundClaims = false</c> so what is written is what is read.
/// </summary>
public static class PrincipalClaims
{
    /// <summary>Principal type: <c>user</c>, <c>agent</c> or <c>runner</c>.</summary>
    public const string PrincipalType = "typ";

    /// <summary>The organization a credential is bound to, when it is bound to one.</summary>
    public const string Organization = "org";

    /// <summary>One claim per personal-access-token scope.</summary>
    public const string Scope = "scp";

    /// <summary>
    /// The refresh-token family this access token belongs to — what <c>/me/sessions</c>
    /// calls a session. Only a JWT carries it: a personal access token is not a session,
    /// and nothing about it appears in that list.
    /// </summary>
    public const string SessionId = "sid";

    public const string UserPrincipalType = "user";

    /// <summary>An agent identity: an ordinary member that is not a person.</summary>
    public const string AgentPrincipalType = "agent";

    /// <summary>
    /// A factory runner: a machine, not an actor. Its principal carries this type,
    /// <see cref="Organization"/>, <see cref="Runner"/> and the single scope
    /// <see cref="RunnerScope"/> — and never <c>sub</c>, so nothing that asks "which user is
    /// this" can be satisfied by it.
    /// </summary>
    public const string RunnerPrincipalType = "runner";

    /// <summary>The runner's id, on a runner principal only.</summary>
    public const string Runner = "rnr";

    /// <summary>
    /// The one scope a runner principal holds. Not a personal-access-token scope (it is not in
    /// <see cref="Scopes.All"/>, so no token can be minted with it); it is here so that a runner
    /// principal is never an <em>empty</em> scope set, which <see cref="ScopeRequirements"/>
    /// reads as an unscoped browser session.
    /// </summary>
    public const string RunnerScope = "runner";
}

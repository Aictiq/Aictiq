namespace Aictiq.SharedKernel.Contracts;

/// <param name="Email">
/// Synthesised and undeliverable (<c>…@agents.{slug}.invalid</c>). An agent has no inbox,
/// and giving it a real-looking address would invite someone to write to it.
/// </param>
public sealed record AgentIdentity(
    string Id, string DisplayName, string OwnerUserId, string Email, bool IsActive,
    DateTimeOffset CreatedAt);

/// <param name="Display"><c>aiq_a1b2c3d4…</c> — all that survives of the secret.</param>
public sealed record AgentTokenSummary(
    Guid Id, string Name, string Display, IReadOnlyList<string> Scopes,
    DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, DateTimeOffset? LastUsedAt);

/// <param name="Secret">Returned exactly once, like any other personal access token.</param>
public sealed record AgentTokenIssued(AgentTokenSummary Token, string Secret);

/// <summary>
/// Everything Identity knows about agents, for the module that owns where they belong.
///
/// Agents are ordinary <c>users</c> rows with <c>is_agent = true</c>, so every assignee,
/// author and audit column keeps working unchanged — that is the whole reason they are
/// users rather than a parallel concept. But they are <b>created</b> and <b>governed</b>
/// inside an organization, which is Tenancy's business, and the account itself is
/// Identity's. This contract is the seam: Tenancy decides who may have an agent and what
/// it can reach; Identity decides what an account is.
///
/// Tokens are here rather than on a Tenancy endpoint of their own because they are
/// personal access tokens like any other — the same table, the same hashing, the same
/// revocation. Only the question "may this caller act for that agent" is Tenancy's.
/// </summary>
public interface IAgentIdentities
{
    /// <param name="organizationSlug">Only to synthesise the address. Membership is the caller's business.</param>
    Task<AgentIdentity> CreateAsync(
        string organizationSlug, string displayName, string ownerUserId,
        CancellationToken cancellationToken = default);

    Task<AgentIdentity?> FindAsync(string agentId, CancellationToken cancellationToken = default);

    /// <summary>Batch, because a roster resolves every agent on it at once.</summary>
    Task<IReadOnlyDictionary<string, AgentIdentity>> GetAsync(
        IReadOnlyCollection<string> agentIds, CancellationToken cancellationToken = default);

    /// <summary>Null leaves a field alone. Returns false when the id is not an agent.</summary>
    Task<bool> UpdateAsync(
        string agentId, string? displayName, bool? isActive,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Disables the account <b>and</b> revokes every token it holds, in one transaction.
    /// Never a delete: an agent's id is written into every item it touched, and removing
    /// the row would leave that history pointing at nothing.
    /// </summary>
    Task<bool> DisableAsync(string agentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Hands the named agents to a new owner. Idempotent — reassigning to the owner they
    /// already have changes nothing — because it is driven by an at-least-once event.
    /// </summary>
    Task<int> ReassignOwnerAsync(
        IReadOnlyCollection<string> agentIds, string previousOwnerId, string newOwnerId,
        CancellationToken cancellationToken = default);

    Task<AgentTokenIssued> IssueTokenAsync(
        string agentId, Guid organizationId, string name, IReadOnlyCollection<string> scopes,
        DateTimeOffset? expiresAt, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AgentTokenSummary>> ListTokensAsync(
        string agentId, CancellationToken cancellationToken = default);

    Task<bool> RevokeTokenAsync(
        string agentId, Guid tokenId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The fallback when Identity is not registered. Fail-closed, like the other null
/// contracts and unlike <see cref="UnlimitedPlanLimits"/>: an agent is an account, and a
/// host with no account module must not pretend to have made one.
/// </summary>
public sealed class NullAgentIdentities : IAgentIdentities
{
    public Task<AgentIdentity> CreateAsync(
        string organizationSlug, string displayName, string ownerUserId,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("No identity module is registered, so agents cannot be created.");

    public Task<AgentIdentity?> FindAsync(string agentId, CancellationToken cancellationToken = default) =>
        Task.FromResult<AgentIdentity?>(null);

    public Task<IReadOnlyDictionary<string, AgentIdentity>> GetAsync(
        IReadOnlyCollection<string> agentIds, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<string, AgentIdentity>>(new Dictionary<string, AgentIdentity>());

    public Task<bool> UpdateAsync(
        string agentId, string? displayName, bool? isActive, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    public Task<bool> DisableAsync(string agentId, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    public Task<int> ReassignOwnerAsync(
        IReadOnlyCollection<string> agentIds, string previousOwnerId, string newOwnerId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(0);

    public Task<AgentTokenIssued> IssueTokenAsync(
        string agentId, Guid organizationId, string name, IReadOnlyCollection<string> scopes,
        DateTimeOffset? expiresAt, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("No identity module is registered, so tokens cannot be issued.");

    public Task<IReadOnlyList<AgentTokenSummary>> ListTokensAsync(
        string agentId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AgentTokenSummary>>([]);

    public Task<bool> RevokeTokenAsync(
        string agentId, Guid tokenId, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);
}

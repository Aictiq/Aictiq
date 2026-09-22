using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Aictiq.Modules.Identity.Domain;
using Aictiq.SharedKernel.Contracts;

namespace Aictiq.Modules.Identity.Access;

/// <summary>
/// The real <see cref="IAgentIdentities"/>: Identity owns accounts, so it is what an agent
/// actually is. Tenancy decides who may have one and where it belongs.
/// </summary>
public sealed class IdentityAgentIdentities(
    IdentityDbContext db,
    UserManager<ApplicationUser> userManager,
    TimeProvider timeProvider) : IAgentIdentities
{
    /// <summary>
    /// Reserved by RFC 2606 precisely so that nothing will ever try to deliver to it. An
    /// agent has no inbox, and an address that merely *looks* undeliverable would still
    /// invite someone to write to it.
    /// </summary>
    public const string EmailDomainSuffix = ".invalid";

    public async Task<AgentIdentity> CreateAsync(
        string organizationSlug, string displayName, string ownerUserId,
        CancellationToken cancellationToken = default)
    {
        var id = Guid.CreateVersion7().ToString();
        var email = $"agent+{id}@agents.{organizationSlug}{EmailDomainSuffix}";
        var now = timeProvider.GetUtcNow();

        var agent = new ApplicationUser
        {
            Id = id,
            UserName = email,
            Email = email,
            // Confirmed, because there is nothing to confirm: no one will ever read it.
            EmailConfirmed = true,
            FirstName = displayName,
            LastName = "",
            IsAgent = true,
            AgentOwnerUserId = ownerUserId,
            CreatedAt = now,
        };

        // No password at all - not a random one. An account with a password nobody knows
        // still has a reset flow, and a reset flow is a way in.
        var result = await userManager.CreateAsync(agent);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"Could not create the agent: {string.Join("; ", result.Errors.Select(e => e.Description))}");
        }

        // The same application role a person gets. An agent's *permissions* come from its
        // organization membership and its token's scopes, exactly like anyone else's.
        await userManager.AddToRoleAsync(agent, SharedKernel.Roles.User);

        return ToIdentity(agent);
    }

    public async Task<AgentIdentity?> FindAsync(string agentId, CancellationToken cancellationToken = default)
    {
        var agent = await db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == agentId && u.IsAgent, cancellationToken);

        return agent is null ? null : ToIdentity(agent);
    }

    public async Task<IReadOnlyDictionary<string, AgentIdentity>> GetAsync(
        IReadOnlyCollection<string> agentIds, CancellationToken cancellationToken = default)
    {
        if (agentIds.Count == 0)
        {
            return new Dictionary<string, AgentIdentity>();
        }

        var ids = agentIds.Distinct().ToArray();
        var agents = await db.Users
            .AsNoTracking()
            .Where(u => ids.Contains(u.Id) && u.IsAgent)
            .ToListAsync(cancellationToken);

        return agents.ToDictionary(a => a.Id, ToIdentity);
    }

    public async Task<bool> UpdateAsync(
        string agentId, string? displayName, bool? isActive, CancellationToken cancellationToken = default)
    {
        var agent = await db.Users.FirstOrDefaultAsync(u => u.Id == agentId && u.IsAgent, cancellationToken);
        if (agent is null)
        {
            return false;
        }

        if (displayName is not null)
        {
            agent.FirstName = displayName;
        }

        if (isActive is { } active)
        {
            agent.IsActive = active;
        }

        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> DisableAsync(string agentId, CancellationToken cancellationToken = default)
    {
        var agent = await db.Users.FirstOrDefaultAsync(u => u.Id == agentId && u.IsAgent, cancellationToken);
        if (agent is null)
        {
            return false;
        }

        var now = timeProvider.GetUtcNow();

        // Both halves or neither: an account marked inactive whose tokens still worked
        // would be the worst of the two states.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        agent.IsActive = false;
        await db.SaveChangesAsync(cancellationToken);

        await db.PersonalAccessTokens
            .Where(t => t.UserId == agentId && t.RevokedAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(t => t.RevokedAt, now), cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<int> ReassignOwnerAsync(
        IReadOnlyCollection<string> agentIds, string previousOwnerId, string newOwnerId,
        CancellationToken cancellationToken = default)
    {
        if (agentIds.Count == 0)
        {
            return 0;
        }

        var ids = agentIds.Distinct().ToArray();
        // The previous owner is in the predicate, so a replay after someone else has
        // already been made owner changes nothing - which is what an at-least-once event
        // needs from the write it drives.
        return await db.Users
            .Where(u => ids.Contains(u.Id) && u.IsAgent && u.AgentOwnerUserId == previousOwnerId)
            .ExecuteUpdateAsync(
                set => set.SetProperty(u => u.AgentOwnerUserId, newOwnerId), cancellationToken);
    }

    public async Task<AgentTokenIssued> IssueTokenAsync(
        string agentId, Guid organizationId, string name, IReadOnlyCollection<string> scopes,
        DateTimeOffset? expiresAt, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        // Always bound to the organization the agent belongs to: an agent's credential
        // must be useless anywhere else, and there is no legitimate reading of an
        // unbound one.
        var token = PersonalAccessToken.Create(
            agentId, organizationId, name, scopes, expiresAt, now, out var secret);

        db.PersonalAccessTokens.Add(token);
        await db.SaveChangesAsync(cancellationToken);

        return new AgentTokenIssued(ToSummary(token), secret);
    }

    public async Task<IReadOnlyList<AgentTokenSummary>> ListTokensAsync(
        string agentId, CancellationToken cancellationToken = default)
    {
        var tokens = await db.PersonalAccessTokens
            .AsNoTracking()
            .Where(t => t.UserId == agentId && t.RevokedAt == null)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync(cancellationToken);

        return [.. tokens.Select(ToSummary)];
    }

    public async Task<bool> RevokeTokenAsync(
        string agentId, Guid tokenId, CancellationToken cancellationToken = default)
    {
        var token = await db.PersonalAccessTokens
            .FirstOrDefaultAsync(t => t.Id == tokenId && t.UserId == agentId, cancellationToken);

        if (token is null)
        {
            return false;
        }

        token.RevokedAt ??= timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static AgentIdentity ToIdentity(ApplicationUser agent) => new(
        agent.Id, agent.FullName, agent.AgentOwnerUserId ?? "", agent.Email ?? "",
        agent.IsActive, agent.CreatedAt);

    private static AgentTokenSummary ToSummary(PersonalAccessToken token) => new(
        token.Id, token.Name, token.Display, token.Scopes,
        token.CreatedAt, token.ExpiresAt, token.LastUsedAt);
}

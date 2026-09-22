namespace Aictiq.SharedKernel.Contracts;

/// <summary>What a claim attempt decided.</summary>
public enum WorkItemClaimOutcome : byte
{
    /// <summary>The item now carries the agent's claim.</summary>
    Claimed = 0,

    /// <summary>Someone else holds a live claim on the item.</summary>
    Conflict = 1,

    /// <summary>No such item in the current organization.</summary>
    NotFound = 2,
}

/// <param name="Version">The item's current xmin. On <see cref="WorkItemClaimOutcome.Conflict"/> this is
/// what the winning claimant left behind, so a client can refetch; on
/// <see cref="WorkItemClaimOutcome.Claimed"/> it is the claimed item's new version.</param>
public sealed record WorkItemClaimResult(WorkItemClaimOutcome Outcome, uint Version, string? ClaimedBy = null);

/// <summary>
/// Claims a work item for an agent. The one cross-module <em>write</em> contract, and a
/// contract only because the factory's dispatcher must have the decision back
/// synchronously - the caller is waiting on it, as with <see cref="ITeamUsage"/>.
///
/// Implemented by WorkItems on the same compare-and-swap the claim endpoint and the
/// <c>claim_item</c> MCP tool use: the claim, the assignment and the move to the
/// workflow's first Active state are one UPDATE the database arbitrates, so two
/// dispatchers racing on one item produce exactly one claim. Everything that happens
/// after the run is over travels by <see cref="RunFinished"/>, never by this contract.
/// </summary>
public interface IWorkItemClaims
{
    Task<WorkItemClaimResult> ClaimForAsync(
        Guid itemId, string agentUserId, CancellationToken cancellationToken = default);
}

/// <summary>No WorkItems module means there is nothing to claim; fail closed.</summary>
public sealed class NullWorkItemClaims : IWorkItemClaims
{
    public Task<WorkItemClaimResult> ClaimForAsync(
        Guid itemId, string agentUserId, CancellationToken cancellationToken = default) =>
        Task.FromResult(new WorkItemClaimResult(WorkItemClaimOutcome.NotFound, 0, null));
}

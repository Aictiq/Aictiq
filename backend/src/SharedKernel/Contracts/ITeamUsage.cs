namespace Aictiq.SharedKernel.Contracts;

/// <summary>
/// Whether anything outside Tenancy still points at a team.
///
/// Tenancy owns teams and therefore owns deleting them, but it cannot see the work items
/// that reference one — those live in WorkItems' schema, and modules do not read across
/// schemas. So the question is asked rather than answered: WorkItems implements it
/// and Tenancy refuses the delete when the answer is yes.
///
/// A question, not an event: deletion is a synchronous decision the caller is waiting on,
/// and an "ask everyone and collect refusals" event round trip would turn one HTTP request
/// into a distributed protocol for no gain inside a single process.
/// </summary>
public interface ITeamUsage
{
    Task<bool> HasItemsAsync(Guid teamId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The answer until WorkItems exists: nothing references a team, so any team may be
/// deleted. Permissive like <see cref="UnlimitedPlanLimits"/> and unlike the fail-closed
/// stubs — refusing every deletion because the module that would object is not installed
/// would make teams undeletable on an instance that has no work items at all.
/// </summary>
public sealed class NoTeamUsage : ITeamUsage
{
    public Task<bool> HasItemsAsync(Guid teamId, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);
}

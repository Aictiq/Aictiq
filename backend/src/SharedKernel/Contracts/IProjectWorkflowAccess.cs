namespace Aictiq.SharedKernel.Contracts;

/// <summary>Answers whether workflow state ids belong to a project, across the module boundary.</summary>
public interface IProjectWorkflowAccess
{
    /// <summary>Display names, restricted to this tenant's project workflow states.</summary>
    Task<IReadOnlyDictionary<Guid, string>> GetStateNamesAsync(
        Guid projectId, IReadOnlyCollection<Guid> stateIds, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());

    Task<bool> StatesBelongToProjectAsync(
        Guid projectId, IReadOnlyCollection<Guid> stateIds,
        CancellationToken cancellationToken = default);
}

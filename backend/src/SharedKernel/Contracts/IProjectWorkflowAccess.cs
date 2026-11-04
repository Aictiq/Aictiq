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

    /// <summary>
    /// True when every column is on a board of one of the project's teams and shows the state
    /// paired with it there.
    /// </summary>
    Task<bool> ColumnsShowStatesAsync(
        Guid projectId, IReadOnlyCollection<(Guid ColumnId, Guid StateId)> columns,
        CancellationToken cancellationToken = default);
}

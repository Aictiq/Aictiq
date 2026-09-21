namespace Aictiq.SharedKernel.Contracts;

/// <summary>Answers whether workflow state ids belong to a project, across the module boundary.</summary>
public interface IProjectWorkflowAccess
{
    Task<bool> StatesBelongToProjectAsync(
        Guid projectId, IReadOnlyCollection<Guid> stateIds,
        CancellationToken cancellationToken = default);
}

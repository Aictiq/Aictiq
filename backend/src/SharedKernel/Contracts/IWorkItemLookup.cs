namespace Aictiq.SharedKernel.Contracts;

/// <summary>Read-only item projection for modules which need to render item references.</summary>
public interface IWorkItemLookup
{
    Task<IReadOnlyList<WorkItemReference>> FindByKeysAsync(Guid projectId, IReadOnlyCollection<string> keys, CancellationToken cancellationToken = default);
}

public sealed record WorkItemReference(Guid Id, string Key, string Title, string State, string? AssigneeId);

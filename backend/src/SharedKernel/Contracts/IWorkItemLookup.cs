namespace Aictiq.SharedKernel.Contracts;

/// <summary>Read-only item projection for modules which need to render item references.</summary>
public interface IWorkItemLookup
{
    Task<IReadOnlyList<WorkItemReference>> FindByKeysAsync(Guid projectId, IReadOnlyCollection<string> keys, CancellationToken cancellationToken = default);

    /// <summary>
    /// The items of the tenant in scope, as they are now: title, type and the name of their
    /// workflow state. Ids from another organization, and deleted items, are simply absent.
    /// </summary>
    Task<IReadOnlyList<WorkItemSummary>> GetSummariesAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<WorkItemSummary>>([]);
}

public sealed record WorkItemReference(Guid Id, string Key, string Title, string State, string? AssigneeId);

/// <param name="Type">The work-item type in camelCase, as the API spells it: <c>story</c>, <c>bug</c>.</param>
/// <param name="State">The workflow state's display name, such as "In Review".</param>
public sealed record WorkItemSummary(Guid Id, Guid ProjectId, string Key, string Title, string Type, string State);

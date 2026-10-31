namespace Aictiq.SharedKernel.Contracts;

/// <summary>
/// Label questions for modules that must not reach into WorkItems' own tables - an
/// automation rule may require a label on the item it watches, and the firing
/// handler needs to ask whether one item carries one label without joining across the
/// module boundary.
/// </summary>
public interface IWorkItemLabels
{
    Task<bool> ItemHasLabelAsync(
        Guid itemId, Guid labelId, CancellationToken cancellationToken = default);

    /// <summary>Whether every label id names a label of this project - the same shape as <see cref="IProjectWorkflowAccess.StatesBelongToProjectAsync"/>.</summary>
    Task<bool> LabelsBelongToProjectAsync(
        Guid projectId, IReadOnlyCollection<Guid> labelIds, CancellationToken cancellationToken = default);

    /// <summary>The names of the labels on one item - a multi-repository run reads its <c>repo:</c> labels.</summary>
    Task<IReadOnlyList<string>> ItemLabelNamesAsync(Guid itemId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    /// <summary>
    /// The state GitHub last reported for each of the item's linked pull requests, by URL:
    /// <c>open</c>, <c>draft</c>, <c>closed</c> or <c>merged</c>. A pull request never linked is absent.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> PullRequestStatesAsync(Guid itemId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
}

/// <summary>No WorkItems module means there is nothing to check a label against; fail closed.</summary>
public sealed class NullWorkItemLabels : IWorkItemLabels
{
    public Task<bool> ItemHasLabelAsync(
        Guid itemId, Guid labelId, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    public Task<bool> LabelsBelongToProjectAsync(
        Guid projectId, IReadOnlyCollection<Guid> labelIds, CancellationToken cancellationToken = default) =>
        Task.FromResult(labelIds.Count == 0);
}

namespace Aictiq.SharedKernel.Contracts;

/// <summary>Ownership check used by storage without granting WorkItems access to wiki tables.</summary>
public interface IWikiPageAccess
{
    Task<bool> CanWriteAsync(Guid pageId, Guid projectId, string userId, CancellationToken cancellationToken = default);

    Task<bool> CanReadAsync(Guid pageId, Guid projectId, string userId, CancellationToken cancellationToken = default);

    Task<IReadOnlySet<Guid>> VisiblePageIdsAsync(Guid projectId, string userId, bool write,
        CancellationToken cancellationToken = default);

    /// <summary>Drops the cached access for a project. Call after anything that changes the page tree.</summary>
    Task InvalidateAsync(Guid projectId, CancellationToken cancellationToken = default);
}

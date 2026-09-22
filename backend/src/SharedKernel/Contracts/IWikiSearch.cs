namespace Aictiq.SharedKernel.Contracts;

public sealed record WikiSearchResult(Guid Id, Guid ProjectId, string Slug, string Title, string Snippet, float Rank);

/// <summary>Module boundary for project-visible wiki search.  WorkItems owns the aggregate
/// search response but never queries the wiki schema directly.</summary>
public interface IWikiSearch
{
    Task<IReadOnlyList<WikiSearchResult>> SearchAsync(IReadOnlyList<Guid> projectIds, string query, int limit,
        string userId, CancellationToken cancellationToken = default);
}

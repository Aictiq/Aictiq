using Aictiq.SharedKernel.Persistence;
using Aictiq.SharedKernel.Contracts;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Aictiq.Modules.Wiki.Search;

internal sealed class WikiSearchService(WikiDbContext db, IWikiPageAccess access) : IWikiSearch
{
    public async Task<IReadOnlyList<WikiSearchResult>> SearchAsync(IReadOnlyList<Guid> projectIds, string query, int limit,
        string userId, CancellationToken cancellationToken = default)
    {
        if (projectIds.Count == 0) return [];
        const string sql = """
            WITH search_query AS (SELECT websearch_to_tsquery('english', @query) || websearch_to_tsquery('simple', @query) AS value)
            SELECT page.id, page.project_id, page.slug, page.title,
                ts_headline('simple', replace(replace(replace(revision.content_markdown, '&', '&amp;'), '<', '&lt;'), '>', '&gt;'), search_query.value,
                    'StartSel=<mark>, StopSel=</mark>, MaxWords=18, MinWords=8, MaxFragments=2, FragmentDelimiter= … ') AS snippet,
                ts_rank_cd(page.search || setweight(to_tsvector('english', revision.content_markdown), 'C') || setweight(to_tsvector('simple', revision.content_markdown), 'C'), search_query.value) AS rank
            FROM wiki.pages page JOIN wiki.page_revisions revision ON revision.id = page.current_revision_id CROSS JOIN search_query
            WHERE page.project_id = ANY(@projects)
                AND (page.search || setweight(to_tsvector('english', revision.content_markdown), 'C') || setweight(to_tsvector('simple', revision.content_markdown), 'C')) @@ search_query.value
            ORDER BY rank DESC, page.updated_at DESC, page.id LIMIT @limit
            """;
        var connection = await db.Database.OpenTenantConnectionAsync(cancellationToken);
        var result = new List<WikiSearchResult>();
        // The reader owns the connection until it is disposed, and the permission checks
        // below query the same context - so the raw SQL runs and drains inside its own scope.
        {
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("projects", NpgsqlDbType.Array | NpgsqlDbType.Uuid, projectIds.ToArray());
            command.Parameters.AddWithValue("query", query); command.Parameters.AddWithValue("limit", limit);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) result.Add(new WikiSearchResult(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetFloat(5)));
        }
        // One context, one query at a time: Task.WhenAll here would run concurrent
        // commands on a single connection and throw NpgsqlOperationInProgressException.
        var allowed = new Dictionary<Guid, IReadOnlySet<Guid>>();
        foreach (var projectId in projectIds)
        {
            allowed[projectId] = await access.VisiblePageIdsAsync(projectId, userId, false, cancellationToken);
        }
        return result.Where(row => allowed.TryGetValue(row.ProjectId, out var pages) && pages.Contains(row.Id)).ToList();
    }
}

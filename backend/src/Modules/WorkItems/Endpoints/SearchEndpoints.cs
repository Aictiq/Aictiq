using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Aictiq.Modules.WorkItems.Domain;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Http;
using Aictiq.SharedKernel.Persistence;
using Aictiq.SharedKernel.Tenancy;

namespace Aictiq.Modules.WorkItems.Endpoints;

public sealed record SearchItemResult(Guid Id, string Key, string Title, string Snippet, float Rank);
public sealed record SearchCommentResult(Guid Id, string ItemKey, string ItemTitle, string Snippet, float Rank);
public sealed record SearchPageResult(Guid Id, Guid ProjectId, string Slug, string Title, string Snippet, float Rank);
public sealed record SearchResponse(IReadOnlyList<SearchItemResult> Items, IReadOnlyList<SearchCommentResult> Comments, IReadOnlyList<SearchPageResult> Pages);

/// <summary>Full-text search stays in WorkItems: it owns both documents and never joins Tenancy tables.</summary>
public static class SearchEndpoints
{
    private static readonly Regex ItemKey = new("^[A-Za-z][A-Za-z0-9]{0,11}-[1-9][0-9]*$", RegexOptions.Compiled);

    public static IEndpointRouteBuilder MapSearchEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapGroup("/orgs/{orgSlug}/projects/{projectKey}").WithTags("Search").RequireAuthorization()
            .MapGet("/search", ProjectSearch).RequireProjectRole(ProjectRole.Guest).RequireScope(Scopes.Read)
            .RequireOperationRateLimit(OperationRateLimiter.Search);
        api.MapGroup("/orgs/{orgSlug}").WithTags("Search").RequireAuthorization()
            .MapGet("/search", OrganizationSearch).RequireOrgRole(OrgRole.Guest).RequireScope(Scopes.Read)
            .RequireOperationRateLimit(OperationRateLimiter.Search);
        return api;
    }

    private static Task<IResult> ProjectSearch(HttpContext http, WorkItemsDbContext db, IWikiSearch wiki, IProjectAccess access, IUserDirectory directory,
        ICurrentUser user, ICurrentTenant tenant, string? q, string? types, int limit = 20, CancellationToken ct = default) =>
        SearchAsync(db, wiki, access, directory, tenant.OrganizationId!.Value, [http.ResolvedProjectId()!.Value], user.UserId!, q, types, limit, ct);

    private static async Task<IResult> OrganizationSearch(WorkItemsDbContext db, IWikiSearch wiki, IProjectAccess access, IUserDirectory directory, ICurrentUser user,
        ICurrentTenant tenant, string? q, string? types, int limit = 20, CancellationToken ct = default)
    {
        // Project visibility belongs to Tenancy. We hand it candidate ids, rather than joining
        // its tables from this module, and retain only projects the caller may actually see.
        var candidates = await db.Items.AsNoTracking().Select(item => item.ProjectId).Distinct().ToListAsync(ct);
        // Asked one project at a time: the contract is served by a single scoped
        // TenancyDbContext, which tolerates no concurrent queries.
        var visible = new List<Guid>(candidates.Count);
        foreach (var id in candidates)
        {
            if (await access.GetProjectRoleAsync(user.UserId!, id, ct) is not null) visible.Add(id);
        }
        return await SearchAsync(db, wiki, access, directory, tenant.OrganizationId!.Value, visible, user.UserId!, q, types, limit, ct);
    }

    private static async Task<IResult> SearchAsync(WorkItemsDbContext db, IWikiSearch wiki, IProjectAccess access, IUserDirectory directory,
        Guid organizationId, IReadOnlyList<Guid> projectIds, string userId, string? rawQuery, string? rawTypes, int limit, CancellationToken ct)
    {
        var query = SearchQuery.Normalize(rawQuery);
        if (query is null) return Results.ValidationProblem(new Dictionary<string, string[]> { ["q"] = ["A search query is required."] });
        if (!TryTypes(rawTypes, out var includeItems, out var includeComments, out var includePages))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["types"] = ["types must contain items, comments, pages, or any combination."] });
        if (projectIds.Count == 0) return Results.Ok(new SearchResponse([], [], []));

        var take = Math.Clamp(limit == 0 ? 20 : limit, 1, 100);
        // A key is an identifier, not natural language. Avoid the ranking path entirely so
        // ACME-12 always resolves directly, even when its title has since changed.
        if (includeItems && ItemKey.IsMatch(query))
        {
            var exact = await ExactItemAsync(db, projectIds, query, ct);
            if (exact is not null) return Results.Ok(new SearchResponse([exact], [], []));
        }

        var items = includeItems ? await FullTextItemsAsync(db, projectIds, query, take, ct) : [];
        // "1377" names an item the full-text document cannot find (see SearchQuery.ItemNumber),
        // one per project that has it, ahead of anything that merely mentions the number.
        if (includeItems && SearchQuery.ItemNumber(query) is { } number && !query.Contains('-'))
        {
            var numbered = await NumberedItemsAsync(db, projectIds, number, take, ct);
            items = numbered.Concat(items.Where(x => numbered.All(n => n.Id != x.Id))).Take(take).ToList();
        }
        var comments = includeComments
            ? await FullTextCommentsAsync(db, projectIds, query, take,
                await FactoryVisibility.HiddenAuthorsAsync(access, directory, userId, organizationId,
                    db.Comments.AsNoTracking().Where(x => db.Items.Any(i => i.Id == x.ItemId && projectIds.Contains(i.ProjectId))), ct) ?? [], ct)
            : [];
        var pages = includePages ? (await wiki.SearchAsync(projectIds, query, take, userId, ct)).Select(x => new SearchPageResult(x.Id, x.ProjectId, x.Slug, x.Title, x.Snippet, x.Rank)).ToList() : [];
        // pg_trgm is deliberately a fallback. A correct lexical result must never lose to a
        // fuzzy one, and the index only exists on titles where a typo is most likely.
        if (items.Count == 0 && comments.Count == 0 && includeItems)
            items = await TrigramItemsAsync(db, projectIds, query, take, ct);
        return Results.Ok(new SearchResponse(items, comments, pages));
    }

    private static bool TryTypes(string? value, out bool items, out bool comments, out bool pages)
    {
        items = comments = pages = false;
        if (string.IsNullOrWhiteSpace(value)) { items = comments = pages = true; return true; }
        foreach (var type in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (type.Equals("items", StringComparison.OrdinalIgnoreCase)) items = true;
            else if (type.Equals("comments", StringComparison.OrdinalIgnoreCase)) comments = true;
            else if (type.Equals("pages", StringComparison.OrdinalIgnoreCase)) pages = true;
            else return false;
        }
        return items || comments || pages;
    }

    private static async Task<SearchItemResult?> ExactItemAsync(WorkItemsDbContext db, IReadOnlyList<Guid> projects, string query, CancellationToken ct)
    {
        const string sql = """
            SELECT id, project_key || '-' || number::text, title
            FROM work.items
            WHERE project_id = ANY(@projects) AND lower(project_key || '-' || number::text) = lower(@query)
            LIMIT 1
            """;
        await using var command = await CommandAsync(db, sql, projects, query, ct);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new SearchItemResult(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), "", 1)
            : null;
    }

    private static async Task<List<SearchItemResult>> NumberedItemsAsync(WorkItemsDbContext db, IReadOnlyList<Guid> projects, int number, int limit, CancellationToken ct)
    {
        const string sql = """
            SELECT item.id, item.project_key || '-' || item.number::text, item.title,
                replace(replace(replace(item.title, '&', '&amp;'), '<', '&lt;'), '>', '&gt;') AS snippet,
                1::real AS rank
            FROM work.items item
            WHERE item.project_id = ANY(@projects) AND item.number = @number
            ORDER BY item.project_key
            LIMIT @limit
            """;
        await using var command = await CommandAsync(db, sql, projects, number.ToString(System.Globalization.CultureInfo.InvariantCulture), ct);
        command.Parameters.AddWithValue("number", number);
        command.Parameters.AddWithValue("limit", limit);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var results = new List<SearchItemResult>();
        while (await reader.ReadAsync(ct)) results.Add(new SearchItemResult(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetFloat(4)));
        return results;
    }

    private static async Task<List<SearchItemResult>> FullTextItemsAsync(WorkItemsDbContext db, IReadOnlyList<Guid> projects, string query, int limit, CancellationToken ct)
    {
        const string sql = """
            WITH search_query AS (SELECT websearch_to_tsquery('english', @query) || websearch_to_tsquery('simple', @query) AS value)
            SELECT item.id, item.project_key || '-' || item.number::text, item.title,
                ts_headline('simple', replace(replace(replace(item.title || ' ' || item.description_markdown, '&', '&amp;'), '<', '&lt;'), '>', '&gt;'), search_query.value,
                    'StartSel=<mark>, StopSel=</mark>, MaxWords=18, MinWords=8, MaxFragments=2, FragmentDelimiter= … ') AS snippet,
                ts_rank_cd(item.search, search_query.value) AS rank
            FROM work.items item CROSS JOIN search_query
            WHERE item.project_id = ANY(@projects) AND item.search @@ search_query.value
            ORDER BY rank DESC, item.updated_at DESC, item.id
            LIMIT @limit
            """;
        return await ReadItemsAsync(db, sql, projects, query, limit, ct);
    }

    private static async Task<List<SearchItemResult>> TrigramItemsAsync(WorkItemsDbContext db, IReadOnlyList<Guid> projects, string query, int limit, CancellationToken ct)
    {
        const string sql = """
            SELECT item.id, item.project_key || '-' || item.number::text, item.title,
                replace(replace(replace(item.title, '&', '&amp;'), '<', '&lt;'), '>', '&gt;') AS snippet,
                similarity(item.title, @query) AS rank
            FROM work.items item
            WHERE item.project_id = ANY(@projects) AND (item.title % @query OR item.title ILIKE @query || '%')
            ORDER BY rank DESC, item.updated_at DESC, item.id
            LIMIT @limit
            """;
        return await ReadItemsAsync(db, sql, projects, query, limit, ct);
    }

    private static async Task<List<SearchItemResult>> ReadItemsAsync(WorkItemsDbContext db, string sql, IReadOnlyList<Guid> projects, string query, int limit, CancellationToken ct)
    {
        await using var command = await CommandAsync(db, sql, projects, query, ct);
        command.Parameters.AddWithValue("limit", limit);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var results = new List<SearchItemResult>();
        while (await reader.ReadAsync(ct)) results.Add(new SearchItemResult(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetFloat(4)));
        return results;
    }

    /// <param name="hiddenAuthors">Agents whose comments, and threads, the caller may not see (<see cref="FactoryVisibility"/>).</param>
    private static async Task<List<SearchCommentResult>> FullTextCommentsAsync(WorkItemsDbContext db, IReadOnlyList<Guid> projects, string query, int limit,
        string[] hiddenAuthors, CancellationToken ct)
    {
        const string sql = """
            WITH search_query AS (SELECT websearch_to_tsquery('english', @query) || websearch_to_tsquery('simple', @query) AS value)
            SELECT comment.id, item.project_key || '-' || item.number::text, item.title,
                ts_headline('simple', replace(replace(replace(comment.body_markdown, '&', '&amp;'), '<', '&lt;'), '>', '&gt;'), search_query.value,
                    'StartSel=<mark>, StopSel=</mark>, MaxWords=18, MinWords=8, MaxFragments=2, FragmentDelimiter= … ') AS snippet,
                ts_rank_cd(comment.search, search_query.value) AS rank
            FROM work.comments comment
            JOIN work.items item ON item.id = comment.item_id
            CROSS JOIN search_query
            WHERE item.project_id = ANY(@projects) AND comment.deleted_at IS NULL AND comment.search @@ search_query.value
              AND NOT comment.author_id = ANY(@hidden)
              AND NOT EXISTS (SELECT 1 FROM work.comments root WHERE root.id = comment.parent_comment_id AND root.author_id = ANY(@hidden))
            ORDER BY rank DESC, comment.created_at DESC, comment.id
            LIMIT @limit
            """;
        await using var command = await CommandAsync(db, sql, projects, query, ct);
        command.Parameters.AddWithValue("limit", limit);
        command.Parameters.AddWithValue("hidden", NpgsqlDbType.Array | NpgsqlDbType.Text, hiddenAuthors);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var results = new List<SearchCommentResult>();
        while (await reader.ReadAsync(ct)) results.Add(new SearchCommentResult(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetFloat(4)));
        return results;
    }

    private static async Task<NpgsqlCommand> CommandAsync(WorkItemsDbContext db, string sql, IReadOnlyList<Guid> projects, string query, CancellationToken ct)
    {
        var connection = await db.Database.OpenTenantConnectionAsync(ct);
        var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("projects", NpgsqlDbType.Array | NpgsqlDbType.Uuid, projects.ToArray());
        command.Parameters.AddWithValue("query", query);
        return command;
    }
}

/// <summary>One query parser shared by the endpoint and the work-item list's <c>q</c> filter.</summary>
public static class SearchQuery
{
    public static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, 200)];

    private static readonly Regex NumberQuery = new(@"^#?([1-9][0-9]{0,8})$", RegexOptions.Compiled);
    private static readonly Regex KeyQuery = new(@"^[A-Za-z][A-Za-z0-9]{0,11}-([1-9][0-9]{0,8})?$", RegexOptions.Compiled);

    /// <summary>
    /// The item number a query names - "1377", "#1377" or "PROJ2-1377" - or null. Full text
    /// cannot find these: Postgres reads the "-1377" of a key as a negative number, so the
    /// document holds '-1377' and a search for 1377 never matches the item it obviously means.
    /// </summary>
    public static int? ItemNumber(string query)
    {
        var match = NumberQuery.Match(query);
        if (!match.Success) match = KeyQuery.Match(query);
        return match.Success && int.TryParse(match.Groups[1].Value, out var number) ? number : null;
    }

    /// <summary>
    /// The search box on a list (items, backlog, board): full text over title, key, labels and
    /// description, a piece of the title, or the item's key. The substring branch is for
    /// typing - full text matches whole words, so "time" would not find "Timeout" until the
    /// word is finished, and a box that empties the list mid-word reads as broken; the title's
    /// trigram index serves the ILIKE. A key is matched as typed, "PROJ2-13" finding every key
    /// that starts so, and a bare number is that item's number.
    /// </summary>
    public static async Task<Guid[]> ItemIdsAsync(WorkItemsDbContext db, IReadOnlyList<Guid> projects, string query, CancellationToken ct)
    {
        const string sql = """
            WITH search_query AS (SELECT websearch_to_tsquery('english', @query) || websearch_to_tsquery('simple', @query) AS value)
            SELECT item.id
            FROM work.items item CROSS JOIN search_query
            WHERE item.project_id = ANY(@projects)
                AND (item.search @@ search_query.value
                    OR item.title ILIKE '%' || @like || '%'
                    OR item.number = @number
                    OR (@key_like <> '' AND lower(item.project_key || '-' || item.number::text) LIKE @key_like))
            """;
        await using var command = await OpenCommandAsync(db, sql, projects, query, ct);
        var like = query.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");
        command.Parameters.AddWithValue("like", like);
        command.Parameters.AddWithValue("number", NumberQuery.Match(query) is { Success: true } ? ItemNumber(query)!.Value : 0);
        command.Parameters.AddWithValue("key_like", KeyQuery.IsMatch(query) ? like.ToLowerInvariant() + "%" : "");
        await using var reader = await command.ExecuteReaderAsync(ct);
        var ids = new List<Guid>();
        while (await reader.ReadAsync(ct)) ids.Add(reader.GetGuid(0));
        return ids.ToArray();
    }

    private static async Task<NpgsqlCommand> OpenCommandAsync(WorkItemsDbContext db, string sql, IReadOnlyList<Guid> projects, string query, CancellationToken ct)
    {
        var connection = await db.Database.OpenTenantConnectionAsync(ct);
        var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("projects", NpgsqlDbType.Array | NpgsqlDbType.Uuid, projects.ToArray());
        command.Parameters.AddWithValue("query", query);
        return command;
    }
}

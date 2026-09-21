using System.ComponentModel;
using Aictiq.Modules.Tenancy;
using Aictiq.Modules.Tenancy.Domain;
using Aictiq.Modules.Wiki.Domain;
using Aictiq.Modules.Wiki.Endpoints;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Mcp;
using Aictiq.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;
using ModelContextProtocol;

namespace Aictiq.Modules.Wiki.Mcp;

public sealed record McpWikiPage(Guid Id, string Slug, string Title, Guid? ParentId, int RevisionNumber, DateTimeOffset UpdatedAt);
public sealed record McpWikiContent(Guid Id, string Slug, string Title, int Version, int RevisionNumber,
    string ContentMarkdown, bool Truncated, string? Range);

/// <summary>Agent-facing wiki operations.  The same project membership check protects both
/// tool and resource reads; a page id is never enough to cross a project boundary.</summary>
[McpServerToolType]
public sealed class WikiMcpTools(WikiDbContext db, TenancyDbContext tenancy, ICurrentTenant tenant,
    ICurrentUser user, IProjectAccess access, IWikiPageAccess pageAccess, IWorkItemLookup items, TimeProvider clock)
{
    private const int DefaultBodyLimit = 20_000;

    [McpServerTool(Name = "search_wiki", ReadOnly = true)]
    [Description("Searches current wiki page titles and Markdown in a project.")]
    public async Task<IReadOnlyList<McpWikiPage>> SearchWiki(string project, string q, int limit = 25,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 100) throw new McpException("limit must be between 1 and 100.");
        var visible = await ProjectAsync(project, false, cancellationToken);
        if (visible is null || string.IsNullOrWhiteSpace(q)) return [];
        var needle = q.Trim();
        var rows = await db.Pages.AsNoTracking().Where(x => x.ProjectId == visible.Id)
            .Join(db.PageRevisions.AsNoTracking(), p => p.CurrentRevisionId, r => r.Id, (p, r) => new { p, r })
            .Where(x => EF.Functions.ILike(x.p.Title, $"%{needle}%") || EF.Functions.ILike(x.r.ContentMarkdown, $"%{needle}%"))
            .OrderByDescending(x => x.p.UpdatedAt).Take(limit).ToListAsync(cancellationToken);
        var readable = await pageAccess.VisiblePageIdsAsync(visible.Id, user.UserId!, false, cancellationToken);
        return rows.Where(x => readable.Contains(x.p.Id)).Select(x => new McpWikiPage(x.p.Id, x.p.Slug, x.p.Title, x.p.ParentId, x.r.Number, x.p.UpdatedAt)).ToList();
    }

    [McpServerTool(Name = "list_pages", ReadOnly = true)]
    public async Task<IReadOnlyList<McpWikiPage>> ListPages(string project, Guid? parent = null,
        CancellationToken cancellationToken = default)
    {
        var visible = await ProjectAsync(project, false, cancellationToken); if (visible is null) return [];
        var readable = await pageAccess.VisiblePageIdsAsync(visible.Id, user.UserId!, false, cancellationToken);
        // Ordered before the projection: EF cannot translate an OrderBy over a record it has
        // just constructed, so sorting afterwards made the tool fail whenever a page existed.
        return (await db.Pages.AsNoTracking().Where(x => x.ProjectId == visible.Id && x.ParentId == parent)
            .OrderBy(p => p.Title)
            .Join(db.PageRevisions.AsNoTracking(), p => p.CurrentRevisionId, r => r.Id, (p, r) => new McpWikiPage(p.Id, p.Slug, p.Title, p.ParentId, r.Number, p.UpdatedAt))
            .ToListAsync(cancellationToken)).Where(page => readable.Contains(page.Id)).ToList();
    }

    [McpServerTool(Name = "get_page", ReadOnly = true)]
    public async Task<McpWikiContent?> GetPage(string project, string slugOrId, string? range = null,
        CancellationToken cancellationToken = default)
    {
        var visible = await ProjectAsync(project, false, cancellationToken); if (visible is null) throw new McpAnswerException($"project '{project}' not found or no access");
        var page = await PageAsync(visible.Id, slugOrId, cancellationToken); if (page is null || !await pageAccess.CanReadAsync(page.Id, visible.Id, user.UserId!, cancellationToken)) throw new McpAnswerException($"page '{slugOrId}' not found or no access");
        var revision = await db.PageRevisions.AsNoTracking().SingleAsync(x => x.Id == page.CurrentRevisionId, cancellationToken);
        return Content(page, revision, range);
    }

    [McpServerTool(Name = "create_page")]
    public async Task<McpWikiContent?> CreatePage(string project, string title, string markdown, Guid? parent = null,
        CancellationToken cancellationToken = default)
    {
        if (!ValidTitle(title) || !ValidMarkdown(markdown)) throw new McpException("title must contain 1 to 500 characters and markdown may not exceed 1 MB.");
        var visible = await ProjectAsync(project, true, cancellationToken); if (visible is null) throw new McpAnswerException($"project '{project}' not found or no access");
        if (parent is { } parentId && (!await db.Pages.AnyAsync(x => x.Id == parentId && x.ProjectId == visible.Id, cancellationToken)
            || !await pageAccess.CanWriteAsync(parentId, visible.Id, user.UserId!, cancellationToken))) throw new McpAnswerException("parent page not found or no access");
        var now = clock.GetUtcNow();
        var page = new WikiPage { OrganizationId = tenant.OrganizationId!.Value, ProjectId = visible.Id, ParentId = parent,
            Title = title.Trim(), Slug = await UniqueSlugAsync(visible.Id, parent, title, cancellationToken),
            Position = await NextPositionAsync(visible.Id, parent, cancellationToken), CreatedBy = user.UserId!, CreatedAt = now, UpdatedAt = now };
        var revision = new WikiPageRevision { OrganizationId = page.OrganizationId, PageId = page.Id, Number = 1,
            ContentMarkdown = markdown.Trim(), ContentHtml = WikiPageEndpoints.Render(markdown.Trim()), AuthorId = user.UserId!, At = now };
        page.CurrentRevisionId = revision.Id; page.Updated(user.UserId!, "created");
        db.Pages.Add(page); db.PageRevisions.Add(revision);
        await WikiPageEndpoints.ReplaceItemLinksAsync(db, page, revision, items, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await pageAccess.InvalidateAsync(visible.Id, cancellationToken);
        return Content(page, revision, null);
    }

    [McpServerTool(Name = "update_page")]
    public async Task<McpWikiContent?> UpdatePage(Guid id, uint version, string markdown, string? summary = null,
        CancellationToken cancellationToken = default)
    {
        if (!ValidMarkdown(markdown) || summary?.Length > 500) throw new McpException("markdown may not exceed 1 MB and summary may not exceed 500 characters.");
        var page = await db.Pages.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (page is null || !await pageAccess.CanWriteAsync(page.Id, page.ProjectId, user.UserId!, cancellationToken)) throw new McpAnswerException($"page '{id}' not found or no access");
        // Archived is read-only here as it is over REST; get_page and create_page already
        // refuse an archived project, and an id is not a way around that.
        if (await tenancy.Projects.AsNoTracking().AnyAsync(x => x.Id == page.ProjectId && x.ArchivedAt != null, cancellationToken)) throw new McpAnswerException("the page's project is archived and read-only");
        if (page.Version != version) throw new McpAnswerException("conflict: version changed");
        var current = await db.PageRevisions.SingleAsync(x => x.Id == page.CurrentRevisionId, cancellationToken);
        var now = clock.GetUtcNow(); var content = markdown.Trim();
        var revision = new WikiPageRevision { OrganizationId = page.OrganizationId, PageId = page.Id, Number = current.Number + 1,
            ContentMarkdown = content, ContentHtml = WikiPageEndpoints.Render(content), AuthorId = user.UserId!, At = now, Summary = summary?.Trim() };
        page.CurrentRevisionId = revision.Id; page.UpdatedAt = now; page.Updated(user.UserId!, "updated");
        db.Entry(page).Property(x => x.Version).OriginalValue = version;
        db.PageRevisions.Add(revision);
        await WikiPageEndpoints.ReplaceItemLinksAsync(db, page, revision, items, cancellationToken);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { throw new McpAnswerException("conflict: version changed"); }
        return Content(page, revision, null);
    }

    private async Task<Project?> ProjectAsync(string key, bool write, CancellationToken ct)
    {
        if (tenant.OrganizationId is not { } organizationId || string.IsNullOrWhiteSpace(key)) return null;
        var project = await tenancy.Projects.AsNoTracking().SingleOrDefaultAsync(x => x.Key == key.Trim().ToUpperInvariant() && x.ArchivedAt == null, ct);
        if (project is null || project.OrganizationId != organizationId) return null;
        var role = await access.GetProjectRoleAsync(user.UserId!, project.Id, ct);
        return role is not null && (!write || role.Value.Satisfies(ProjectRole.Member)) ? project : null;
    }

    private Task<WikiPage?> PageAsync(Guid projectId, string slugOrId, CancellationToken ct) =>
        Guid.TryParse(slugOrId, out var id)
            ? db.Pages.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.ProjectId == projectId, ct)
            : db.Pages.AsNoTracking().Where(x => x.ProjectId == projectId && x.Slug == slugOrId.Trim()).OrderBy(x => x.ParentId).FirstOrDefaultAsync(ct);

    private McpWikiContent Content(WikiPage page, WikiPageRevision revision, string? range)
    {
        var (start, length) = ParseRange(range);
        var body = revision.ContentMarkdown; start = Math.Min(start, body.Length);
        var take = Math.Min(length ?? DefaultBodyLimit, body.Length - start);
        var truncated = start + take < body.Length;
        return new McpWikiContent(page.Id, page.Slug, page.Title, unchecked((int)page.Version), revision.Number,
            McpContentBoundary.Wrap(body.Substring(start, take)), truncated, truncated ? $"{start + take}:{DefaultBodyLimit}" : null);
    }

    private static (int Start, int? Length) ParseRange(string? range)
    {
        if (string.IsNullOrWhiteSpace(range)) return (0, null);
        var pieces = range.Split(':', StringSplitOptions.TrimEntries);
        if (pieces.Length is < 1 or > 2 || !int.TryParse(pieces[0], out var start) || start < 0
            || pieces.Length == 2 && (!int.TryParse(pieces[1], out var length) || length is < 1 or > DefaultBodyLimit))
            throw new McpException("range must be start:length, with a length from 1 to 20,000.");
        return (start, pieces.Length == 2 ? int.Parse(pieces[1]) : null);
    }

    private static bool ValidTitle(string value) => !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= 500;
    private static bool ValidMarkdown(string value) => value is not null && value.Length <= WikiPageEndpoints.MaxMarkdownLength;
    private async Task<int> NextPositionAsync(Guid projectId, Guid? parentId, CancellationToken ct) =>
        (await db.Pages.Where(x => x.ProjectId == projectId && x.ParentId == parentId).Select(x => (int?)x.Position).MaxAsync(ct) ?? -1) + 1;
    private async Task<string> UniqueSlugAsync(Guid projectId, Guid? parentId, string title, CancellationToken ct)
    {
        var stem = WikiPageEndpoints.Slugify(title); var used = await db.Pages.Where(x => x.ProjectId == projectId && x.ParentId == parentId).Select(x => x.Slug).ToListAsync(ct);
        var set = used.ToHashSet(StringComparer.OrdinalIgnoreCase); for (var suffix = 1; ; suffix++) { var candidate = suffix == 1 ? stem : $"{stem}-{suffix}"; if (!set.Contains(candidate)) return candidate; }
    }
}

[McpServerResourceType]
public sealed class WikiMcpContext(WikiDbContext db, TenancyDbContext tenancy, ICurrentTenant tenant, ICurrentUser user, IProjectAccess access, IWikiPageAccess pageAccess)
{
    [McpServerResource(UriTemplate = "aictiq://wiki/{project}/{slug}", Name = "wiki-page", MimeType = "text/markdown")]
    public async Task<string> WikiPage(string project, string slug, CancellationToken cancellationToken)
    {
        if (tenant.OrganizationId is not { } organizationId) return "# Wiki page not found";
        var pageProject = await tenancy.Projects.AsNoTracking().SingleOrDefaultAsync(x => x.Key == project.Trim().ToUpperInvariant() && x.OrganizationId == organizationId, cancellationToken);
        if (pageProject is null || await access.GetProjectRoleAsync(user.UserId!, pageProject.Id, cancellationToken) is null) return "# Wiki page not found";
        var page = await db.Pages.AsNoTracking().Where(x => x.ProjectId == pageProject.Id && x.Slug == slug).OrderBy(x => x.ParentId).FirstOrDefaultAsync(cancellationToken);
        if (page is null || !await pageAccess.CanReadAsync(page.Id, pageProject.Id, user.UserId!, cancellationToken)) return "# Wiki page not found";
        var revision = await db.PageRevisions.AsNoTracking().SingleAsync(x => x.Id == page.CurrentRevisionId, cancellationToken);
        return McpContentBoundary.Wrap($"# {page.Title}\n\n{revision.ContentMarkdown}");
    }
}

using Aictiq.Modules.Wiki.Domain;
using Aictiq.Modules.Wiki.Endpoints;
using Aictiq.SharedKernel.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Aictiq.Modules.Wiki;

internal sealed class WikiPageContentService(WikiDbContext db, IWikiPageAccess access, IWorkItemLookup items, TimeProvider clock)
    : IWikiPageContent, IWikiPageCreator
{
    public async Task<WikiPageContent?> GetMarkdownAsync(
        Guid pageId, Guid projectId, CancellationToken cancellationToken = default)
    {
        return await db.Pages.AsNoTracking()
            .Where(page => page.Id == pageId && page.ProjectId == projectId && page.CurrentRevisionId != null)
            .Join(db.PageRevisions.AsNoTracking(), page => page.CurrentRevisionId, revision => revision.Id,
                (page, revision) => new WikiPageContent(page.Title, revision.ContentMarkdown, revision.Id))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<Guid?> CreateStarterPageAsync(
        Guid organizationId, Guid projectId, string actorId, string markdown,
        CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        var factory = await SectionAsync(organizationId, projectId, actorId, now, cancellationToken);
        if (await db.Pages.AnyAsync(
                page => page.ProjectId == projectId && page.ParentId == factory.Id && page.Slug == "implement",
                cancellationToken))
            return null;

        var implement = NewPage(organizationId, projectId, factory.Id, "Implement", "implement", actorId, now,
            await NextPositionAsync(projectId, factory.Id, cancellationToken));
        await AddAsync(implement, markdown, actorId, now, cancellationToken);
        return implement.Id;
    }

    public async Task<Guid?> CreateFactoryPageAsync(
        Guid organizationId, Guid projectId, string actorId, string title, string markdown,
        CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        var factory = await SectionAsync(organizationId, projectId, actorId, now, cancellationToken);
        var page = NewPage(organizationId, projectId, factory.Id, title.Trim(),
            await WikiPageEndpoints.UniqueSlugAsync(db, projectId, factory.Id, title, null, cancellationToken), actorId, now,
            await NextPositionAsync(projectId, factory.Id, cancellationToken));
        await AddAsync(page, markdown, actorId, now, cancellationToken);
        return page.Id;
    }

    public async Task<bool> WriteFactoryPageAsync(
        Guid pageId, Guid projectId, string actorId, string markdown,
        CancellationToken cancellationToken = default)
    {
        if (!await IsInFactorySectionAsync(pageId, projectId, cancellationToken)) return false;
        var page = await db.Pages.SingleAsync(x => x.Id == pageId, cancellationToken);
        var current = await db.PageRevisions.AsNoTracking()
            .Where(x => x.PageId == pageId).OrderByDescending(x => x.Number).FirstOrDefaultAsync(cancellationToken);
        if (current is not null && current.Id == page.CurrentRevisionId && current.ContentMarkdown == markdown) return true;
        var now = clock.GetUtcNow();
        var revision = WikiPageEndpoints.NewRevision(page, (current?.Number ?? 0) + 1, markdown, actorId, now, "Edited from the playbook");
        page.CurrentRevisionId = revision.Id;
        page.UpdatedAt = now;
        page.Updated(actorId, "updated");
        db.PageRevisions.Add(revision);
        await WikiPageEndpoints.ReplaceItemLinksAsync(db, page, revision, items, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> IsInFactorySectionAsync(
        Guid pageId, Guid projectId, CancellationToken cancellationToken = default)
    {
        var pages = await db.Pages.AsNoTracking().Where(page => page.ProjectId == projectId)
            .Select(page => new { page.Id, page.ParentId, page.IsFactorySection }).ToDictionaryAsync(page => page.Id, cancellationToken);
        if (!pages.TryGetValue(pageId, out var start) || start.IsFactorySection) return false;
        for (var parent = start.ParentId; parent is { } id && pages.TryGetValue(id, out var above); parent = above.ParentId)
            if (above.IsFactorySection) return true;
        return false;
    }

    /// <summary>The project's Factory section, created as a top-level page when it has none.</summary>
    private async Task<WikiPage> SectionAsync(Guid organizationId, Guid projectId, string actorId, DateTimeOffset now, CancellationToken ct)
    {
        var factory = await db.Pages.FirstOrDefaultAsync(page => page.ProjectId == projectId && page.IsFactorySection, ct);
        if (factory is not null) return factory;
        factory = NewPage(organizationId, projectId, null, "Factory",
            await WikiPageEndpoints.UniqueSlugAsync(db, projectId, null, "Factory", null, ct), actorId, now,
            await NextPositionAsync(projectId, null, ct), isFactorySection: true);
        var revision = WikiPageEndpoints.NewRevision(factory, 1, "# Factory\n\nInstructions used by automated runs.", actorId, now, null);
        factory.CurrentRevisionId = revision.Id;
        db.Pages.Add(factory);
        db.PageRevisions.Add(revision);
        return factory;
    }

    private async Task AddAsync(WikiPage page, string markdown, string actorId, DateTimeOffset now, CancellationToken ct)
    {
        var revision = WikiPageEndpoints.NewRevision(page, 1, markdown, actorId, now, null);
        page.CurrentRevisionId = revision.Id;
        page.Updated(actorId, "created");
        db.Pages.Add(page);
        db.PageRevisions.Add(revision);
        await WikiPageEndpoints.ReplaceItemLinksAsync(db, page, revision, items, ct);
        await db.SaveChangesAsync(ct);
        await access.InvalidateAsync(page.ProjectId, ct);
    }

    private static WikiPage NewPage(
        Guid organizationId, Guid projectId, Guid? parentId, string title, string slug,
        string actorId, DateTimeOffset now, int position, bool isFactorySection = false) => new()
    {
        OrganizationId = organizationId,
        ProjectId = projectId,
        ParentId = parentId,
        Title = title,
        Slug = slug,
        Position = position,
        IsFactorySection = isFactorySection,
        CreatedBy = actorId,
        CreatedAt = now,
        UpdatedAt = now,
    };

    private async Task<int> NextPositionAsync(Guid projectId, Guid? parentId, CancellationToken cancellationToken) =>
        (await db.Pages.Where(page => page.ProjectId == projectId && page.ParentId == parentId)
            .Select(page => (int?)page.Position).MaxAsync(cancellationToken) ?? -1) + 1;
}

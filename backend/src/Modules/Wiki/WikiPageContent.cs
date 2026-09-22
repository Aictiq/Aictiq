using Aictiq.Modules.Wiki.Domain;
using Aictiq.Modules.Wiki.Endpoints;
using Aictiq.SharedKernel.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Aictiq.Modules.Wiki;

internal sealed class WikiPageContentService(WikiDbContext db, IWikiPageAccess access, TimeProvider clock)
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
        var factory = await db.Pages.FirstOrDefaultAsync(
            page => page.ProjectId == projectId && page.ParentId == null && page.Slug == "factory",
            cancellationToken);
        var now = clock.GetUtcNow();
        if (factory is null)
        {
            factory = NewPage(organizationId, projectId, null, "Factory", "factory", actorId, now,
                await NextPositionAsync(projectId, null, cancellationToken));
            var revision = NewRevision(factory, "# Factory\n\nInstructions used by automated runs.", actorId, now);
            factory.CurrentRevisionId = revision.Id;
            db.Pages.Add(factory);
            db.PageRevisions.Add(revision);
        }

        if (await db.Pages.AnyAsync(
                page => page.ProjectId == projectId && page.ParentId == factory.Id && page.Slug == "implement",
                cancellationToken))
            return null;

        var implement = NewPage(organizationId, projectId, factory.Id, "Implement", "implement", actorId, now,
            await NextPositionAsync(projectId, factory.Id, cancellationToken));
        var implementRevision = NewRevision(implement, markdown, actorId, now);
        implement.CurrentRevisionId = implementRevision.Id;
        db.Pages.Add(implement);
        db.PageRevisions.Add(implementRevision);
        await db.SaveChangesAsync(cancellationToken);
        await access.InvalidateAsync(projectId, cancellationToken);
        return implement.Id;
    }

    private static WikiPage NewPage(
        Guid organizationId, Guid projectId, Guid? parentId, string title, string slug,
        string actorId, DateTimeOffset now, int position) => new()
    {
        OrganizationId = organizationId,
        ProjectId = projectId,
        ParentId = parentId,
        Title = title,
        Slug = slug,
        Position = position,
        CreatedBy = actorId,
        CreatedAt = now,
        UpdatedAt = now,
    };

    private static WikiPageRevision NewRevision(WikiPage page, string markdown, string actorId, DateTimeOffset now) => new()
    {
        OrganizationId = page.OrganizationId,
        PageId = page.Id,
        Number = 1,
        ContentMarkdown = markdown,
        ContentHtml = WikiPageEndpoints.Render(markdown),
        AuthorId = actorId,
        At = now,
    };

    private async Task<int> NextPositionAsync(Guid projectId, Guid? parentId, CancellationToken cancellationToken) =>
        (await db.Pages.Where(page => page.ProjectId == projectId && page.ParentId == parentId)
            .Select(page => (int?)page.Position).MaxAsync(cancellationToken) ?? -1) + 1;
}

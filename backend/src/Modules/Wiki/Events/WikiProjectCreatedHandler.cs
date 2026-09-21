using Microsoft.EntityFrameworkCore;
using Aictiq.Modules.Tenancy.Contracts;
using Aictiq.Modules.Wiki.Contracts;
using Aictiq.SharedKernel.Events;
using Aictiq.SharedKernel.Outbox;
using Aictiq.SharedKernel.Tenancy;

namespace Aictiq.Modules.Wiki.Events;

/// <summary>The root slug index makes this safe when an outbox delivery is replayed.</summary>
public sealed class WikiProjectCreatedHandler(WikiDbContext db, ICurrentTenant currentTenant) : IDomainEventHandler<ProjectCreated>
{
    public async Task HandleAsync(ProjectCreated @event, CancellationToken cancellationToken)
    {
        using var tenant = currentTenant is AmbientCurrentTenant ambient ? ambient.Use(@event.OrganizationId) : null;
        var pageId = Guid.CreateVersion7();
        var revisionId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        // One statement, and the page names its revision as it is inserted. Every part of a
        // statement sees the same snapshot, so a trailing UPDATE cannot find the page a CTE
        // just inserted — it matched nothing and left Home without a current revision.
        // The row count is the revision insert's: 1 when the page was created, 0 on replay.
        var created = await db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH created_page AS (
                INSERT INTO wiki.pages (id, organization_id, project_id, parent_id, slug, title, position, current_revision_id, created_by, created_at, updated_at)
                VALUES ({pageId}, {@event.OrganizationId}, {@event.ProjectId}, NULL, 'home', 'Home', 0, {revisionId}, {@event.CreatedBy}, {now}, {now})
                ON CONFLICT DO NOTHING
                RETURNING id
            )
            INSERT INTO wiki.page_revisions (id, organization_id, page_id, number, content_markdown, content_html, author_id, at)
            SELECT {revisionId}, {@event.OrganizationId}, id, 1, '', '', {@event.CreatedBy}, {now} FROM created_page;
            """, cancellationToken);
        if (created == 1)
        {
            db.Set<OutboxMessage>().Add(OutboxMessage.From(
                new WikiPageUpdated(@event.OrganizationId, @event.ProjectId, pageId, @event.CreatedBy, "created")));
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}

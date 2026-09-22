using System.Text.Json;
using Aictiq.Modules.Integrations.Contracts;
using Aictiq.Modules.WorkItems.Domain;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Events;
using Aictiq.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Aictiq.Modules.WorkItems.Events;

/// <summary>Materializes a GitHub commit reference. The unique item-link index is the replay guard.</summary>
public sealed class CommitReferencedItemHandler(
    WorkItemsDbContext db, ICurrentTenant currentTenant, IExternalLoginLookup externalLogins, TimeProvider clock)
    : IDomainEventHandler<CommitReferencedItem>
{
    public async Task HandleAsync(CommitReferencedItem @event, CancellationToken cancellationToken)
    {
        using var tenant = currentTenant is AmbientCurrentTenant ambient ? ambient.Use(@event.OrganizationId) : null;
        var item = await db.Items.SingleOrDefaultAsync(x => x.ProjectId == @event.ProjectId && x.Number == @event.ItemNumber, cancellationToken);
        if (item is null) return; // An old push may name an item that has since been removed.

        if (await db.ItemLinks.AnyAsync(x => x.ItemId == item.Id && x.Provider == "github" && x.Kind == ItemLinkKind.Commit && x.ExternalId == @event.Sha, cancellationToken)) return;
        var actorId = await externalLogins.FindGitHubUserIdAsync(@event.AuthorLogin, @event.AuthorEmail, cancellationToken)
            ?? $"github:{@event.AuthorLogin ?? "commit"}";
        if (actorId.Length > 64) actorId = actorId[..64];
        var now = clock.GetUtcNow();
        db.ItemLinks.Add(new ItemLink
        {
            OrganizationId = @event.OrganizationId, ItemId = item.Id, Kind = ItemLinkKind.Commit,
            Provider = "github", ExternalId = @event.Sha, Url = @event.Url, Title = @event.Message,
            Meta = JsonSerializer.Serialize(new { @event.AuthorName, @event.AuthorLogin, @event.AuthorEmail, @event.Branch }),
            CreatedAt = now,
        });
        db.ItemHistory.Add(new ItemHistory
        {
            OrganizationId = @event.OrganizationId, ItemId = item.Id, ActorId = actorId, At = now,
            Field = "commit-linked", OldValue = null,
            NewValue = JsonSerializer.Serialize(new { @event.Sha, @event.Message, @event.AuthorName, @event.AuthorLogin, @event.Branch }),
            EventId = @event.EventId,
        });
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // A concurrent/redelivered event won the unique item-link race. Its matching
            // history row committed in the same transaction, so this retry is complete.
        }
    }
}

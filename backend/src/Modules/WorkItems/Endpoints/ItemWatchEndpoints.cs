using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Aictiq.Modules.WorkItems.Domain;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Contracts;

namespace Aictiq.Modules.WorkItems.Endpoints;

public sealed record ItemWatcherView(UserSummary User, ItemWatchReason Reason);

public static class ItemWatchEndpoints
{
    public static IEndpointRouteBuilder MapItemWatchEndpoints(this IEndpointRouteBuilder api)
    {
        var items = api.MapGroup("/orgs/{orgSlug}/items").WithTags("Watchers").RequireAuthorization();
        items.MapGet("/{itemKey}/watch", List).RequireOrgRole(OrgRole.Guest).RequireScope(Scopes.Read);
        items.MapPut("/{itemKey}/watch", Watch).RequireOrgRole(OrgRole.Member).RequireScope(Scopes.Write);
        items.MapDelete("/{itemKey}/watch", Unwatch).RequireOrgRole(OrgRole.Member).RequireScope(Scopes.Write);
        return api;
    }

    private static async Task<IResult> List(string itemKey, WorkItemsDbContext db, IProjectAccess access,
        ICurrentUser user, IUserDirectory directory, CancellationToken ct)
    {
        var item = await WorkItemEndpoints.FindVisible(db, access, user, itemKey, ct);
        if (item is null) return Results.NotFound();
        var watchers = await db.ItemWatchers.AsNoTracking().Where(x => x.ItemId == item.Id && x.MutedAt == null)
            .OrderBy(x => x.CreatedAt).ThenBy(x => x.UserId).ToListAsync(ct);
        var people = await directory.GetAsync(watchers.Select(x => x.UserId).ToArray(), ct);
        return Results.Ok(watchers.Select(x => new ItemWatcherView(
            people.GetValueOrDefault(x.UserId, new UserSummary(x.UserId, "Unknown user", null, false)), x.Reason)));
    }

    private static async Task<IResult> Watch(string itemKey, WorkItemsDbContext db, IProjectAccess access,
        ICurrentUser user, TimeProvider clock, CancellationToken ct)
    {
        var item = await WorkItemEndpoints.FindVisible(db, access, user, itemKey, ct);
        if (item is null) return Results.NotFound();
        var watcher = await db.ItemWatchers.SingleOrDefaultAsync(x => x.ItemId == item.Id && x.UserId == user.UserId, ct);
        if (watcher is null)
            db.ItemWatchers.Add(new ItemWatcher { OrganizationId = item.OrganizationId, ItemId = item.Id, UserId = user.UserId!, Reason = ItemWatchReason.Manual, CreatedAt = clock.GetUtcNow() });
        else { watcher.MutedAt = null; watcher.Reason = ItemWatchReason.Manual; }
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> Unwatch(string itemKey, WorkItemsDbContext db, IProjectAccess access,
        ICurrentUser user, TimeProvider clock, CancellationToken ct)
    {
        var item = await WorkItemEndpoints.FindVisible(db, access, user, itemKey, ct);
        if (item is null) return Results.NotFound();
        var watcher = await db.ItemWatchers.SingleOrDefaultAsync(x => x.ItemId == item.Id && x.UserId == user.UserId, ct);
        if (watcher is null)
            db.ItemWatchers.Add(new ItemWatcher { OrganizationId = item.OrganizationId, ItemId = item.Id, UserId = user.UserId!, Reason = ItemWatchReason.Manual, CreatedAt = clock.GetUtcNow(), MutedAt = clock.GetUtcNow() });
        else watcher.MutedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }
}

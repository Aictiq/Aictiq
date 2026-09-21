using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Aictiq.Modules.WorkItems.Domain;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Http;

namespace Aictiq.Modules.WorkItems.Endpoints;

public enum ItemRelationDirection : short { Related, Blocks, BlockedBy, Duplicates, DuplicatedBy }
public sealed record ItemRelationView(string TargetKey, string TargetTitle, ItemRelationKind Kind, ItemRelationDirection Direction);
public sealed record PutItemRelationRequest(string? TargetKey, ItemRelationKind Kind, bool MarkSourceDuplicate = false);
public sealed record ItemLinkView(Guid Id, ItemLinkKind Kind, string Provider, string ExternalId, string Url, string? Title, string? FaviconUrl, string? State, DateTimeOffset CreatedAt, string? AuthorName = null, string? Branch = null);
public sealed record CreateItemLinkRequest(string? Url);

/// <summary>Relations and user-created web links. Integration writers use the same
/// <see cref="ItemLink"/> table directly in their own modules in later tickets.</summary>
public static class ItemRelationsEndpoints
{
    public static IEndpointRouteBuilder MapItemRelationsEndpoints(this IEndpointRouteBuilder api)
    {
        var items = api.MapGroup("/orgs/{orgSlug}/items").WithTags("Work items").RequireAuthorization();
        items.MapGet("/{itemKey}/relations", GetRelations).RequireOrgRole(OrgRole.Guest).RequireScope(Scopes.Read);
        items.MapPut("/{itemKey}/relations", PutRelation).RequireOrgRole(OrgRole.Member).RequireItemProjectWritable().RequireScope(Scopes.Write);
        items.MapDelete("/{itemKey}/relations", DeleteRelation).RequireOrgRole(OrgRole.Member).RequireItemProjectWritable().RequireScope(Scopes.Write);
        items.MapGet("/{itemKey}/links", GetLinks).RequireOrgRole(OrgRole.Guest).RequireScope(Scopes.Read);
        items.MapPost("/{itemKey}/links", CreateLink).RequireOrgRole(OrgRole.Member).RequireItemProjectWritable().RequireScope(Scopes.Write);
        items.MapDelete("/{itemKey}/links/{linkId:guid}", DeleteLink).RequireOrgRole(OrgRole.Member).RequireItemProjectWritable().RequireScope(Scopes.Write);
        return api;
    }

    private static async Task<IResult> GetRelations(string itemKey, WorkItemsDbContext db, IProjectAccess access, ICurrentUser user, CancellationToken ct)
    {
        var item = await WorkItemEndpoints.FindVisible(db, access, user, itemKey, ct);
        if (item is null) return Results.NotFound();
        var rows = await db.ItemRelations.AsNoTracking()
            .Where(r => r.SourceId == item.Id || r.TargetId == item.Id).ToListAsync(ct);
        var otherIds = rows.Select(r => r.SourceId == item.Id ? r.TargetId : r.SourceId).Distinct().ToArray();
        var others = await db.Items.AsNoTracking().Where(i => otherIds.Contains(i.Id))
            .ToDictionaryAsync(i => i.Id, ct);
        return Results.Ok(rows.Where(r => others.ContainsKey(r.SourceId == item.Id ? r.TargetId : r.SourceId))
            .Select(r =>
            {
                var outbound = r.SourceId == item.Id;
                var target = others[outbound ? r.TargetId : r.SourceId];
                var direction = r.Kind switch
                {
                    ItemRelationKind.Related => ItemRelationDirection.Related,
                    ItemRelationKind.Blocks when outbound => ItemRelationDirection.Blocks,
                    ItemRelationKind.Blocks => ItemRelationDirection.BlockedBy,
                    ItemRelationKind.Duplicates when outbound => ItemRelationDirection.Duplicates,
                    _ => ItemRelationDirection.DuplicatedBy,
                };
                return new ItemRelationView(target.Key, target.Title, r.Kind, direction);
            }).OrderBy(r => r.Direction).ThenBy(r => r.TargetKey, StringComparer.Ordinal).ToList());
    }

    private static async Task<IResult> PutRelation(string itemKey, PutItemRelationRequest request, WorkItemsDbContext db, IProjectAccess access, ICurrentUser user, Aictiq.SharedKernel.Tenancy.ICurrentTenant tenant, TimeProvider clock, CancellationToken ct)
    {
        var source = await WorkItemEndpoints.FindWritable(db, access, user, itemKey, ct);
        if (source is null) return Results.NotFound();
        if (string.IsNullOrWhiteSpace(request.TargetKey)) return Invalid("targetKey", "A target item key is required.");
        var target = await WorkItemEndpoints.FindVisible(db, access, user, request.TargetKey, ct);
        if (target is null || target.ProjectId != source.ProjectId) return Results.NotFound();
        if (target.Id == source.Id) return Invalid("targetKey", "An item cannot be related to itself.");
        if (request.MarkSourceDuplicate && request.Kind != ItemRelationKind.Duplicates)
            return Invalid("markSourceDuplicate", "Only a duplicate relation can resolve the source as duplicate.");

        var sourceId = source.Id;
        var targetId = target.Id;
        if (request.Kind == ItemRelationKind.Related && sourceId.CompareTo(targetId) > 0) (sourceId, targetId) = (targetId, sourceId);

        if (request.MarkSourceDuplicate)
        {
            var removed = await db.WorkflowStates.Where(s => s.Category == WorkflowStateCategory.Removed &&
                    db.Workflows.Any(w => w.Id == s.WorkflowId && w.ProjectId == source.ProjectId))
                .Select(s => s.Id).FirstOrDefaultAsync(ct);
            if (removed == Guid.Empty) return Results.Problem("The project workflow has no Removed state.", statusCode: StatusCodes.Status409Conflict);
            var now = clock.GetUtcNow();
            source.Transition(removed, WorkflowStateCategory.Removed, user.UserId!, now);
            source.Resolution = "duplicate";
            await db.SaveChangesAsync(ct);
        }

        // An upsert makes repeated PUTs (including concurrent retries from agents) truly
        // idempotent while the PK remains the database's deduplication guarantee.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO work.item_relations (source_id, target_id, kind, organization_id)
            VALUES ({sourceId}, {targetId}, {(short)request.Kind}, {tenant.OrganizationId!.Value})
            ON CONFLICT (source_id, target_id, kind) DO NOTHING
            """, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteRelation(string itemKey, [FromBody] PutItemRelationRequest request, WorkItemsDbContext db, IProjectAccess access, ICurrentUser user, CancellationToken ct)
    {
        var source = await WorkItemEndpoints.FindWritable(db, access, user, itemKey, ct);
        if (source is null) return Results.NotFound();
        if (string.IsNullOrWhiteSpace(request.TargetKey)) return Invalid("targetKey", "A target item key is required.");
        var target = await WorkItemEndpoints.FindVisible(db, access, user, request.TargetKey, ct);
        if (target is null || target.ProjectId != source.ProjectId) return Results.NotFound();
        var sourceId = source.Id;
        var targetId = target.Id;
        if (request.Kind == ItemRelationKind.Related && sourceId.CompareTo(targetId) > 0) (sourceId, targetId) = (targetId, sourceId);
        await db.ItemRelations.Where(r => r.SourceId == sourceId && r.TargetId == targetId && r.Kind == request.Kind).ExecuteDeleteAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> GetLinks(string itemKey, WorkItemsDbContext db, IProjectAccess access, ICurrentUser user, CancellationToken ct)
    {
        var item = await WorkItemEndpoints.FindVisible(db, access, user, itemKey, ct);
        if (item is null) return Results.NotFound();
        var links = await db.ItemLinks.AsNoTracking().Where(link => link.ItemId == item.Id)
            .OrderByDescending(link => link.CreatedAt).ToListAsync(ct);
        return Results.Ok(links.Select(ToView).ToList());
    }

    private static async Task<IResult> CreateLink(string itemKey, CreateItemLinkRequest request, WorkItemsDbContext db, IProjectAccess access, ICurrentUser user, Aictiq.SharedKernel.Tenancy.ICurrentTenant tenant, LinkPreviewFetcher previews, TimeProvider clock, CancellationToken ct)
    {
        var item = await WorkItemEndpoints.FindWritable(db, access, user, itemKey, ct);
        if (item is null) return Results.NotFound();
        if (!LinkPreviewFetcher.TryParsePublicUrl(request.Url, out var url, out var error)) return Invalid("url", error!);
        var publicUrl = url!;
        if (!await previews.IsPublicHostAsync(publicUrl, ct)) return Invalid("url", "The URL must resolve only to public internet addresses.");

        var existing = await db.ItemLinks.FirstOrDefaultAsync(l => l.ItemId == item.Id && l.Provider == "url" && l.Kind == ItemLinkKind.Url && l.ExternalId == publicUrl.AbsoluteUri, ct);
        if (existing is not null) return Results.Ok(ToView(existing));
        var preview = await previews.FetchAsync(publicUrl, ct);
        var link = new ItemLink
        {
            OrganizationId = tenant.OrganizationId!.Value, ItemId = item.Id, Kind = ItemLinkKind.Url,
            Provider = "url", ExternalId = publicUrl.AbsoluteUri, Url = publicUrl.AbsoluteUri, Title = preview.Title,
            Meta = preview.FaviconUrl is null ? null : JsonSerializer.Serialize(new { faviconUrl = preview.FaviconUrl }),
            CreatedAt = clock.GetUtcNow(),
        };
        db.ItemLinks.Add(link);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            // A concurrent identical POST won. Return that durable row rather than making
            // an idempotent client retry look like an error.
            var concurrent = await db.ItemLinks.FirstOrDefaultAsync(l => l.ItemId == item.Id && l.Provider == "url" && l.Kind == ItemLinkKind.Url && l.ExternalId == publicUrl.AbsoluteUri, ct);
            if (concurrent is not null) return Results.Ok(ToView(concurrent));
            throw;
        }
        return Results.Created($"/api/v1/orgs/{item.OrganizationId}/items/{item.Key}/links/{link.Id}", ToView(link));
    }

    private static async Task<IResult> DeleteLink(string itemKey, Guid linkId, WorkItemsDbContext db, IProjectAccess access, ICurrentUser user, CancellationToken ct)
    {
        var item = await WorkItemEndpoints.FindWritable(db, access, user, itemKey, ct);
        if (item is null) return Results.NotFound();
        await db.ItemLinks.Where(l => l.Id == linkId && l.ItemId == item.Id).ExecuteDeleteAsync(ct);
        return Results.NoContent();
    }

    internal static ItemLinkView ToView(ItemLink link)
    {
        var (authorName, branch) = CommitMeta(link.Meta);
        return new(link.Id, link.Kind, link.Provider, link.ExternalId, link.Url, link.Title, Favicon(link.Meta), link.State, link.CreatedAt, authorName, branch);
    }
    private static string? Favicon(string? meta)
    {
        if (string.IsNullOrWhiteSpace(meta)) return null;
        try { return JsonDocument.Parse(meta).RootElement.TryGetProperty("faviconUrl", out var value) ? value.GetString() : null; }
        catch (JsonException) { return null; }
    }
    private static (string? AuthorName, string? Branch) CommitMeta(string? meta)
    {
        if (string.IsNullOrWhiteSpace(meta)) return (null, null);
        try
        {
            var root = JsonDocument.Parse(meta).RootElement;
            return (root.TryGetProperty("AuthorName", out var author) ? author.GetString() : null,
                root.TryGetProperty("Branch", out var branch) ? branch.GetString() : null);
        }
        catch (JsonException) { return (null, null); }
    }
    private static IResult Invalid(string field, string error) => Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [error] });
}

public sealed record LinkPreview(string? Title, string? FaviconUrl);

/// <summary>Small, deliberately non-browser page fetcher. It accepts only HTTP(S), rejects
/// private/reserved address space before connecting, does not follow redirects, and caps a
/// preview request at three seconds.</summary>
public sealed class LinkPreviewFetcher(IHttpClientFactory clients)
{
    private const int MaxPageBytes = 256 * 1024;
    private static readonly Regex Title = new("<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex Icon = new("<link[^>]+(?:rel=[\"'][^\"']*(?:icon|shortcut icon)[^\"']*[\"'])[^>]*href=[\"'](?<href>[^\"']+)[\"']|<link[^>]+href=[\"'](?<href2>[^\"']+)[\"'][^>]*rel=[\"'][^\"']*(?:icon|shortcut icon)[^\"']*[\"'])", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool TryParsePublicUrl(string? value, out Uri? url, out string? error)
    {
        url = null;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) || string.IsNullOrWhiteSpace(parsed.Host) || !string.IsNullOrEmpty(parsed.UserInfo))
        { error = "A valid HTTP or HTTPS URL is required."; return false; }
        if (IPAddress.TryParse(parsed.Host, out var address) && !IsPublic(address))
        { error = "The URL must not target a private or reserved address."; return false; }
        url = parsed; error = null; return true;
    }

    public async Task<bool> IsPublicHostAsync(Uri url, CancellationToken ct) =>
        (await PublicNetworkGuard.ResolvePublicAsync(url.DnsSafeHost, ct)).Length > 0;

    public async Task<LinkPreview> FetchAsync(Uri url, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            using var response = await clients.CreateClient("aictiq-link-preview").GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType is not { } mediaType || !mediaType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase)) return new(null, null);
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var bytes = new byte[MaxPageBytes]; var offset = 0;
            while (offset < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(offset), timeout.Token);
                if (read == 0) break;
                offset += read;
            }
            var html = System.Text.Encoding.UTF8.GetString(bytes, 0, offset);
            var title = Title.Match(html) is { Success: true } titleMatch ? WebUtility.HtmlDecode(titleMatch.Groups[1].Value).Trim() : null;
            if (title?.Length > 500) title = title[..500];
            var icon = Icon.Match(html);
            var favicon = icon.Success ? icon.Groups["href"].Success ? icon.Groups["href"].Value : icon.Groups["href2"].Value : null;
            return new(title, Uri.TryCreate(url, favicon, out var absoluteIcon) && absoluteIcon.Scheme is "http" or "https" ? absoluteIcon.AbsoluteUri : null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(null, null); }
        catch (HttpRequestException) { return new(null, null); }
    }

    private static bool IsPublic(IPAddress address) => PublicNetworkGuard.IsPublic(address);
}

using Aictiq.Modules.Tenancy;
using System.Text;
using System.Text.RegularExpressions;
using DiffPlex;
using DiffPlex.Chunkers;
using DiffPlex.DiffBuilder;
using Markdig;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Aictiq.Modules.Wiki.Domain;
using Aictiq.Modules.Wiki.Contracts;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Tenancy;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Outbox;
using Aictiq.SharedKernel.Persistence;
using Aictiq.SharedKernel.References;
using Aictiq.Modules.Wiki.Search;

namespace Aictiq.Modules.Wiki.Endpoints;

public sealed record WikiTreePageView(Guid Id, string Slug, string Title, Guid? ParentId, int Position, DateTimeOffset UpdatedAt);
public sealed record WikiPageView(Guid Id, Guid ProjectId, string Slug, string Title, Guid? ParentId, int Position,
    string ContentMarkdown, string ContentHtml, int RevisionNumber, string? Summary, DateTimeOffset UpdatedAt, uint Version);
public sealed record CreateWikiPageRequest(Guid? ParentId, string? Title, string? ContentMd);
public sealed record UpdateWikiPageRequest(string? Title, string? ContentMd, uint Version, string? Summary);
public sealed record MoveWikiPageRequest(Guid? ParentId, int Position, uint Version);
public sealed record WikiRevisionView(int Number, string AuthorId, string? Summary, DateTimeOffset At, int SizeDelta, bool IsCurrent, UserSummary Author);
public sealed record WikiRevisionDetailView(int Number, string ContentMarkdown, string ContentHtml, string AuthorId, DateTimeOffset At, string? Summary);
public sealed record WikiDiffLine(string Kind, string Text, int? OldLine, int? NewLine);
public sealed record WikiDiffHunk(int OldStart, int OldLines, int NewStart, int NewLines, IReadOnlyList<WikiDiffLine> Lines);
public sealed record WikiDiffView(int From, int To, IReadOnlyList<WikiDiffHunk> Hunks);
public sealed record WikiItemBacklinkView(Guid PageId, string Title, string Slug, int RevisionNumber, DateTimeOffset UpdatedAt);
public sealed record WikiPagePermissionView(WikiPermissionSubjectKind SubjectKind, string SubjectId, WikiPermissionAccess Access);
public sealed record ReplaceWikiPagePermissionsRequest(IReadOnlyList<WikiPagePermissionView>? Rules);

public static partial class WikiPageEndpoints
{
    private const int MaxDepth = 10;
    /// <summary>The wiki's hard page limit. Content is retained forever in revisions.</summary>
    public const int MaxMarkdownLength = 1_048_576;
    public const int MaxHtmlLength = 2_097_152;
    private static readonly MarkdownPipeline Markdown = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseMathematics()
        .DisableHtml()
        .Build();

    public static IEndpointRouteBuilder MapWikiPageEndpoints(this IEndpointRouteBuilder api)
    {
        var project = api.MapGroup("/orgs/{orgSlug}/projects/{projectKey}/wiki").WithTags("Wiki").RequireAuthorization();
        project.MapGet("/tree", Tree).RequireProjectRole(ProjectRole.Guest).RequireScope(Scopes.Read);
        project.MapPost("/pages", Create).RequireProjectRole(ProjectRole.Member).RequireProjectWritable().RequireScope(Scopes.Write);

        var pages = api.MapGroup("/orgs/{orgSlug}/wiki/pages").WithTags("Wiki").RequireAuthorization();
        pages.MapGet("/{pageId:guid}", Get).RequireOrgRole(OrgRole.Guest).RequireScope(Scopes.Read);
        pages.MapPatch("/{pageId:guid}", Update).RequireOrgRole(OrgRole.Member).RequireScope(Scopes.Write);
        pages.MapPost("/{pageId:guid}/move", Move).RequireOrgRole(OrgRole.Member).RequireScope(Scopes.Write);
        pages.MapDelete("/{pageId:guid}", Delete).RequireOrgRole(OrgRole.Member).RequireScope(Scopes.Write);
        pages.MapGet("/{pageId:guid}/revisions", Revisions).RequireOrgRole(OrgRole.Guest).RequireScope(Scopes.Read);
        pages.MapGet("/{pageId:guid}/revisions/{number:int}", Revision).RequireOrgRole(OrgRole.Guest).RequireScope(Scopes.Read);
        pages.MapGet("/{pageId:guid}/diff", Diff).RequireOrgRole(OrgRole.Guest).RequireScope(Scopes.Read);
        pages.MapPost("/{pageId:guid}/revisions/{number:int}/restore", RestoreRevision).RequireOrgRole(OrgRole.Member).RequireScope(Scopes.Write);
        pages.MapGet("/{pageId:guid}/permissions", Permissions).RequireOrgRole(OrgRole.Guest).RequireScope(Scopes.Read);
        pages.MapPut("/{pageId:guid}/permissions", ReplacePermissions).RequireOrgRole(OrgRole.Member).RequireScope(Scopes.Write);
        api.MapGroup("/orgs/{orgSlug}/items").WithTags("Wiki").RequireAuthorization()
            .MapGet("/{itemKey}/wiki-backlinks", Backlinks).RequireOrgRole(OrgRole.Guest).RequireScope(Scopes.Read);
        return api;
    }

    private static async Task<IResult> Tree(HttpContext http, WikiDbContext db, ICurrentUser user, IWikiPageAccess access, CancellationToken ct)
    {
        var projectId = http.ResolvedProjectId()!.Value;
        var readable = await access.VisiblePageIdsAsync(projectId, user.UserId!, false, ct);
        var pages = await db.Pages.AsNoTracking().Where(x => x.ProjectId == projectId)
            .OrderBy(x => x.ParentId).ThenBy(x => x.Position).ThenBy(x => x.Id)
            .Select(x => new WikiTreePageView(x.Id, x.Slug, x.Title, x.ParentId, x.Position, x.UpdatedAt)).ToListAsync(ct);
        return Results.Ok(pages.Where(page => readable.Contains(page.Id) && (page.ParentId is null || readable.Contains(page.ParentId.Value))));
    }

    private static async Task<IResult> Create(CreateWikiPageRequest request, HttpContext http, WikiDbContext db,
        ICurrentTenant tenant, ICurrentUser user, IWikiPageAccess access, TimeProvider clock, IWorkItemLookup items, CancellationToken ct)
    {
        var project = http.ResolvedProject()!;
        if (ValidateTitle(request.Title) is { } invalidTitle) return invalidTitle;
        if (ValidateContent(request.ContentMd) is { } invalidContent) return invalidContent;
        if (request.ParentId is { } parentId)
        {
            var parent = await db.Pages.FirstOrDefaultAsync(x => x.Id == parentId && x.ProjectId == project.Id, ct);
            if (parent is null) return Results.ValidationProblem(new Dictionary<string, string[]> { ["parentId"] = ["The parent page does not exist in this project."] });
            if (!await access.CanWriteAsync(parentId, project.Id, user.UserId!, ct)) return Results.NotFound();
            if (await DepthAsync(db, parentId, ct) >= MaxDepth) return Results.ValidationProblem(new Dictionary<string, string[]> { ["parentId"] = [$"Pages can be nested at most {MaxDepth} levels."] });
        }
        var title = request.Title!.Trim();
        var slug = await UniqueSlugAsync(db, project.Id, request.ParentId, title, null, ct);
        var now = clock.GetUtcNow();
        var page = new WikiPage
        {
            OrganizationId = tenant.OrganizationId!.Value, ProjectId = project.Id, ParentId = request.ParentId,
            Slug = slug, Title = title, Position = await NextPositionAsync(db, project.Id, request.ParentId, ct),
            CreatedBy = user.UserId!, CreatedAt = now, UpdatedAt = now
        };
        var revision = NewRevision(page, 1, request.ContentMd!.Trim(), user.UserId!, now, null);
        page.CurrentRevisionId = revision.Id;
        page.Updated(user.UserId!, "created");
        db.Pages.Add(page); db.PageRevisions.Add(revision);
        await ReplaceItemLinksAsync(db, page, revision, items, ct);
        await db.SaveChangesAsync(ct);
        // Access is cached per person for the whole tree, and a page it has never seen reads
        // as invisible: without this a sub-page under a page made a moment ago is a 404.
        await access.InvalidateAsync(project.Id, ct);
        return Results.Created($"/api/v1/orgs/{http.Request.RouteValues["orgSlug"]}/wiki/pages/{page.Id}", View(page, revision));
    }

    private static async Task<IResult> Get(Guid pageId, WikiDbContext db, ICurrentUser user,
        IWikiPageAccess access, CancellationToken ct)
    {
        var page = await FindVisibleAsync(db, access, user.UserId!, pageId, ct);
        if (page is null) return Results.NotFound();
        var revision = await db.PageRevisions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == page.CurrentRevisionId, ct);
        return revision is null ? Results.NotFound() : Results.Ok(View(page, revision));
    }

    private static async Task<IResult> Update(Guid pageId, UpdateWikiPageRequest request, WikiDbContext db,
        ICurrentUser user, IWikiPageAccess access, TimeProvider clock, IWorkItemLookup items, TenancyDbContext tenancy, HttpContext http, CancellationToken ct)
    {
        var page = await FindWritableAsync(db, access, user.UserId!, pageId, ct);
        if (page is null) return Results.NotFound();
        if (await WriteRefusalAsync(http, tenancy, page.ProjectId, ct) is { } readOnly) return readOnly;
        if (page.Version != request.Version) return Conflict();
        if (request.Title is null && request.ContentMd is null) return Results.ValidationProblem(new Dictionary<string, string[]> { ["request"] = ["Provide a title or contentMd."] });
        if (request.Title is not null && ValidateTitle(request.Title) is { } invalidTitle) return invalidTitle;
        if (request.ContentMd is not null && ValidateContent(request.ContentMd) is { } invalidContent) return invalidContent;
        if (request.Summary?.Length > 500) return Results.ValidationProblem(new Dictionary<string, string[]> { ["summary"] = ["Use 500 characters or fewer."] });

        var current = await db.PageRevisions.SingleAsync(x => x.Id == page.CurrentRevisionId, ct);
        var content = request.ContentMd?.Trim() ?? current.ContentMarkdown;
        if (request.Title is { } requestedTitle)
        {
            page.Title = requestedTitle.Trim();
            page.Slug = await UniqueSlugAsync(db, page.ProjectId, page.ParentId, page.Title, page.Id, ct);
        }
        var now = clock.GetUtcNow();
        var revision = NewRevision(page, current.Number + 1, content, user.UserId!, now, request.Summary?.Trim());
        page.CurrentRevisionId = revision.Id; page.UpdatedAt = now; page.Updated(user.UserId!, "updated");
        db.Entry(page).Property(x => x.Version).OriginalValue = request.Version;
        db.PageRevisions.Add(revision);
        await ReplaceItemLinksAsync(db, page, revision, items, ct);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return Conflict(); }
        return Results.Ok(View(page, revision));
    }

    private static async Task<IResult> Move(Guid pageId, MoveWikiPageRequest request, WikiDbContext db,
        ICurrentUser user, IWikiPageAccess access, IFactoryPages factory, TimeProvider clock, TenancyDbContext tenancy, HttpContext http, CancellationToken ct)
    {
        var page = await FindWritableAsync(db, access, user.UserId!, pageId, ct);
        if (page is null) return Results.NotFound();
        if (await WriteRefusalAsync(http, tenancy, page.ProjectId, ct) is { } readOnly) return readOnly;
        if (page.Version != request.Version) return Conflict();
        if (request.ParentId == page.Id) return Results.ValidationProblem(new Dictionary<string, string[]> { ["parentId"] = ["A page cannot be its own parent."] });
        if (request.ParentId is { } parentId)
        {
            var parent = await db.Pages.FirstOrDefaultAsync(x => x.Id == parentId && x.ProjectId == page.ProjectId, ct);
            if (parent is null) return Results.ValidationProblem(new Dictionary<string, string[]> { ["parentId"] = ["The parent page does not exist in this project."] });
            if (!await access.CanWriteAsync(parentId, page.ProjectId, user.UserId!, ct)) return Results.NotFound();
            if (await IsDescendantAsync(db, page.Id, parentId, ct)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["parentId"] = ["A page cannot be moved below one of its descendants."] });
            if (await DepthAsync(db, parentId, ct) >= MaxDepth || await SubtreeDepthAsync(db, page.Id, ct) + await DepthAsync(db, parentId, ct) > MaxDepth)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["parentId"] = [$"Pages can be nested at most {MaxDepth} levels."] });
        }
        if (!page.IsFactorySection && !await InFactorySectionAsync(db, page.ProjectId, request.ParentId, ct))
        {
            var playbookPages = await factory.ListPlaybookPageIdsAsync(page.ProjectId, ct);
            if (playbookPages.Count > 0 && (await SubtreeIdsAsync(db, page.Id, ct)).Any(playbookPages.Contains))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["parentId"] = ["A playbook's page stays in the Factory section."] });
        }
        var siblings = await db.Pages.Where(x => x.ProjectId == page.ProjectId && x.ParentId == request.ParentId && x.Id != page.Id)
            .OrderBy(x => x.Position).ThenBy(x => x.Id).ToListAsync(ct);
        siblings.Insert(Math.Clamp(request.Position, 0, siblings.Count), page);
        page.ParentId = request.ParentId;
        var now = clock.GetUtcNow();
        foreach (var sibling in siblings) { sibling.Position = siblings.IndexOf(sibling); sibling.UpdatedAt = now; }
        page.Updated(user.UserId!, "moved");
        db.Entry(page).Property(x => x.Version).OriginalValue = request.Version;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return Conflict(); }
        // A new parent means new inherited permissions.
        await access.InvalidateAsync(page.ProjectId, ct);
        var revision = await db.PageRevisions.AsNoTracking().SingleAsync(x => x.Id == page.CurrentRevisionId, ct);
        return Results.Ok(View(page, revision));
    }

    /// <summary>
    /// Deletes the page and every page below it, for good: revisions, permissions and item
    /// links go with them, and the page's attachments follow through the outbox. There is no
    /// trash - a restore is something a revision offers, not a deleted page.
    /// </summary>
    private static async Task<IResult> Delete(Guid pageId, WikiDbContext db, ICurrentUser user,
        IWikiPageAccess access, TimeProvider clock, TenancyDbContext tenancy, HttpContext http, CancellationToken ct)
    {
        var page = await FindWritableAsync(db, access, user.UserId!, pageId, ct);
        if (page is null) return Results.NotFound();
        if (await WriteRefusalAsync(http, tenancy, page.ProjectId, ct) is { } readOnly) return readOnly;
        // Deleting a page deletes what is below it, so each of those must be yours to delete
        // too - a subpage restricted to others is not removed by removing its parent.
        var subtree = await SubtreeIdsAsync(db, page.Id, ct);
        var writable = await access.VisiblePageIdsAsync(page.ProjectId, user.UserId!, true, ct);
        if (subtree.Any(id => !writable.Contains(id)))
            return Results.Problem(title: "This page has subpages you are not allowed to delete.", statusCode: StatusCodes.Status403Forbidden);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var deleted = await db.Database.SqlQuery<Guid>($"""SELECT wiki.delete_page_subtree({page.Id}) AS "Value" """).ToListAsync(ct);
        foreach (var id in deleted)
            db.Set<OutboxMessage>().Add(OutboxMessage.From(new WikiPageUpdated(page.OrganizationId, page.ProjectId, id, user.UserId!, "deleted")));
        db.Set<OutboxMessage>().Add(OutboxMessage.From(new WikiPagesDeleted(page.OrganizationId, page.ProjectId, deleted)));
        // An audit row carries the title a page had, so the trail goes with the page.
        await AuditPurge.EntitiesAsync(db, page.OrganizationId, AuditEntityTypes.WikiPage, deleted, ct);
        db.Set<AuditLogEntry>().Add(AuditPurge.Tombstone(page.OrganizationId, AuditEntityTypes.WikiPage, page.Id,
            deleted.Count == 1 ? page.Slug : $"{page.Slug} (+{deleted.Count - 1} below)", user.UserId, clock.GetUtcNow()));
        db.Entry(page).State = EntityState.Detached;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        await access.InvalidateAsync(page.ProjectId, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> Revisions(Guid pageId, int? limit, int? offset, WikiDbContext db, ICurrentUser user,
        IWikiPageAccess access, IUserDirectory directory, CancellationToken ct)
    {
        var page = await FindVisibleAsync(db, access, user.UserId!, pageId, ct);
        if (page is null) return Results.NotFound();
        var take = Math.Clamp(limit ?? 50, 1, 200);
        var skip = Math.Max(offset ?? 0, 0);
        var revisions = await db.PageRevisions.AsNoTracking().Where(x => x.PageId == page.Id)
            .OrderByDescending(x => x.Number).Select(x => new { x.Number, x.AuthorId, x.Summary, x.At, Size = x.ContentMarkdown.Length, x.Id })
            .Skip(skip).Take(take).ToListAsync(ct);
        var sizes = await db.PageRevisions.AsNoTracking().Where(x => x.PageId == page.Id)
            .Select(x => new { x.Number, Size = x.ContentMarkdown.Length }).ToDictionaryAsync(x => x.Number, x => x.Size, ct);
        var authors = await directory.GetAsync(revisions.Select(x => x.AuthorId).Distinct().ToList(), ct);
        return Results.Ok(revisions.Select(x => new WikiRevisionView(x.Number, x.AuthorId, x.Summary, x.At,
            x.Size - sizes.GetValueOrDefault(x.Number - 1, 0), x.Id == page.CurrentRevisionId,
            authors.TryGetValue(x.AuthorId, out var author) ? author : new UserSummary(x.AuthorId, "Unknown user", null, false))));
    }

    private static async Task<IResult> Revision(Guid pageId, int number, WikiDbContext db, ICurrentUser user,
        IWikiPageAccess access, CancellationToken ct)
    {
        var page = await FindVisibleAsync(db, access, user.UserId!, pageId, ct);
        if (page is null) return Results.NotFound();
        var revision = await db.PageRevisions.AsNoTracking().SingleOrDefaultAsync(x => x.PageId == page.Id && x.Number == number, ct);
        return revision is null ? Results.NotFound() : Results.Ok(RevisionView(revision));
    }

    private static async Task<IResult> Diff(Guid pageId, int from, int to, WikiDbContext db, ICurrentUser user,
        IWikiPageAccess access, CancellationToken ct)
    {
        var page = await FindVisibleAsync(db, access, user.UserId!, pageId, ct);
        if (page is null) return Results.NotFound();
        if (from == to) return Results.ValidationProblem(new Dictionary<string, string[]> { ["to"] = ["Choose two different revisions."] });
        var revisions = await db.PageRevisions.AsNoTracking().Where(x => x.PageId == page.Id && (x.Number == from || x.Number == to))
            .ToDictionaryAsync(x => x.Number, ct);
        if (!revisions.TryGetValue(from, out var before) || !revisions.TryGetValue(to, out var after)) return Results.NotFound();
        return Results.Ok(new WikiDiffView(from, to, UnifiedDiff(before.ContentMarkdown, after.ContentMarkdown)));
    }

    private static async Task<IResult> RestoreRevision(Guid pageId, int number, WikiDbContext db, ICurrentUser user,
        IWikiPageAccess access, TimeProvider clock, IWorkItemLookup items, TenancyDbContext tenancy, HttpContext http, CancellationToken ct)
    {
        var page = await FindWritableAsync(db, access, user.UserId!, pageId, ct);
        if (page is null) return Results.NotFound();
        if (await WriteRefusalAsync(http, tenancy, page.ProjectId, ct) is { } readOnly) return readOnly;
        var revisions = await db.PageRevisions.Where(x => x.PageId == page.Id && (x.Number == number || x.Id == page.CurrentRevisionId))
            .ToDictionaryAsync(x => x.Number, ct);
        if (!revisions.TryGetValue(number, out var source) || !revisions.Values.Any(x => x.Id == page.CurrentRevisionId)) return Results.NotFound();
        var current = revisions.Values.Single(x => x.Id == page.CurrentRevisionId);
        if (source.Id == current.Id) return Conflict("The requested revision is already the page head.");
        var now = clock.GetUtcNow();
        var restored = NewRevision(page, current.Number + 1, source.ContentMarkdown, user.UserId!, now, $"Restored from #{number}");
        page.CurrentRevisionId = restored.Id; page.UpdatedAt = now; page.Updated(user.UserId!, "restored revision");
        db.PageRevisions.Add(restored);
        await ReplaceItemLinksAsync(db, page, restored, items, ct);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return Conflict(); }
        return Results.Ok(View(page, restored));
    }

    private static async Task<IResult> Backlinks(string itemKey, WikiDbContext db, ICurrentTenant tenant, ICurrentUser user,
        IProjectAccess access, IWikiPageAccess pageAccess, IWorkItemLookup items, CancellationToken ct)
    {
        var dash = itemKey.LastIndexOf('-');
        if (dash <= 0 || !int.TryParse(itemKey[(dash + 1)..], out _)) return Results.NotFound();
        var project = await access.FindProjectAsync(tenant.OrganizationId!.Value, itemKey[..dash], ct);
        if (project is null || await access.GetProjectRoleAsync(user.UserId!, project.Id, ct) is null) return Results.NotFound();
        var item = (await items.FindByKeysAsync(project.Id, [itemKey], ct)).SingleOrDefault();
        if (item is null) return Results.NotFound();
        var links = await db.PageItemLinks.AsNoTracking().Where(x => x.ItemId == item.Id)
            .Join(db.Pages.AsNoTracking(), link => link.PageId, page => page.Id, (link, page) => new { link, page })
            .Join(db.PageRevisions.AsNoTracking(), row => row.link.RevisionId, revision => revision.Id, (row, revision) => new { row.page, revision })
            .OrderByDescending(x => x.page.UpdatedAt)
            .Select(x => new WikiItemBacklinkView(x.page.Id, x.page.Title, x.page.Slug, x.revision.Number, x.page.UpdatedAt)).ToListAsync(ct);
        var readable = await pageAccess.VisiblePageIdsAsync(project.Id, user.UserId!, false, ct);
        return Results.Ok(links.Where(link => readable.Contains(link.PageId)));
    }

    private static async Task<IResult> Permissions(Guid pageId, WikiDbContext db, ICurrentUser user,
        IWikiPageAccess access, CancellationToken ct)
    {
        var page = await FindVisibleAsync(db, access, user.UserId!, pageId, ct);
        if (page is null) return Results.NotFound();
        var rules = await db.PagePermissions.AsNoTracking().Where(rule => rule.PageId == page.Id)
            .OrderBy(rule => rule.SubjectKind).ThenBy(rule => rule.SubjectId)
            .Select(rule => new WikiPagePermissionView(rule.SubjectKind, rule.SubjectId, rule.Access)).ToListAsync(ct);
        return Results.Ok(rules);
    }

    private static async Task<IResult> ReplacePermissions(Guid pageId, ReplaceWikiPagePermissionsRequest request,
        WikiDbContext db, ICurrentUser user, IProjectAccess projects, WikiPageAccess access, TenancyDbContext tenancy, HttpContext http, CancellationToken ct)
    {
        var page = await db.Pages.FirstOrDefaultAsync(x => x.Id == pageId, ct);
        if (page is null) return Results.NotFound();
        var role = await projects.GetProjectRoleAsync(user.UserId!, page.ProjectId, ct);
        if (role is null) return Results.NotFound();
        if (role != ProjectRole.Admin && (page.CreatedBy != user.UserId || !await access.CanWriteAsync(page.Id, page.ProjectId, user.UserId!, ct))) return Results.NotFound();
        if (await WriteRefusalAsync(http, tenancy, page.ProjectId, ct) is { } readOnly) return readOnly;
        var rules = request.Rules ?? [];
        if (rules.Count > 100 || rules.Any(rule => !ValidRule(rule)) || rules.Select(rule => (rule.SubjectKind, rule.SubjectId)).Distinct().Count() != rules.Count)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["rules"] = ["Rules must have unique valid subjects (team, user, or guest/member/admin role)."] });
        db.PagePermissions.RemoveRange(db.PagePermissions.Where(rule => rule.PageId == page.Id));
        foreach (var rule in rules) db.PagePermissions.Add(new WikiPagePermission
        {
            OrganizationId = page.OrganizationId, PageId = page.Id, SubjectKind = rule.SubjectKind,
            SubjectId = rule.SubjectId.Trim(), Access = rule.Access,
        });
        await db.SaveChangesAsync(ct);
        await access.InvalidateAsync(page.ProjectId, ct);
        return Results.Ok(rules);
    }

    private static bool ValidRule(WikiPagePermissionView rule) => !string.IsNullOrWhiteSpace(rule.SubjectId) && rule.SubjectId.Trim().Length <= 64
        && Enum.IsDefined(rule.SubjectKind) && Enum.IsDefined(rule.Access)
        && (rule.SubjectKind != WikiPermissionSubjectKind.ProjectRole || rule.SubjectId.Trim().ToLowerInvariant() is "guest" or "member" or "admin");

    /// <summary>
    /// Page routes name a page, not a project, so <c>RequireProjectWritable</c> cannot run on
    /// them; this is the same refusal for the project the page belongs to.
    /// </summary>
    private static async Task<IResult?> WriteRefusalAsync(HttpContext http, TenancyDbContext tenancy, Guid projectId, CancellationToken ct)
    {
        var project = await tenancy.Projects.AsNoTracking().Where(x => x.Id == projectId)
            .Select(x => new { x.Key, IsArchived = x.ArchivedAt != null }).FirstOrDefaultAsync(ct);
        return project is null ? Results.NotFound() : await AuthorizationFilters.ProjectWriteRefusalAsync(http, project.Key, project.IsArchived);
    }

    private static async Task<WikiPage?> FindVisibleAsync(WikiDbContext db, IWikiPageAccess access,
        string userId, Guid pageId, CancellationToken ct)
    {
        var page = await db.Pages.FirstOrDefaultAsync(x => x.Id == pageId, ct);
        return page is not null && await access.CanReadAsync(page.Id, page.ProjectId, userId, ct) ? page : null;
    }

    private static async Task<WikiPage?> FindWritableAsync(WikiDbContext db, IWikiPageAccess access,
        string userId, Guid pageId, CancellationToken ct)
    {
        var page = await db.Pages.FirstOrDefaultAsync(x => x.Id == pageId, ct);
        return page is not null && await access.CanWriteAsync(page.Id, page.ProjectId, userId, ct) ? page : null;
    }

    /// <summary>Whether a page placed under <paramref name="parentId"/> would be in the Factory section.</summary>
    private static async Task<bool> InFactorySectionAsync(WikiDbContext db, Guid projectId, Guid? parentId, CancellationToken ct)
    {
        if (parentId is null) return false;
        var pages = await db.Pages.AsNoTracking().Where(x => x.ProjectId == projectId)
            .Select(x => new { x.Id, x.ParentId, x.IsFactorySection }).ToDictionaryAsync(x => x.Id, ct);
        for (Guid? current = parentId; current is { } id && pages.TryGetValue(id, out var row); current = row.ParentId)
            if (row.IsFactorySection) return true;
        return false;
    }

    internal static WikiPageRevision NewRevision(WikiPage page, int number, string markdown, string authorId, DateTimeOffset at, string? summary) => new()
    {
        OrganizationId = page.OrganizationId, PageId = page.Id, Number = number, ContentMarkdown = markdown,
        ContentHtml = Render(markdown), AuthorId = authorId, At = at, Summary = summary
    };

    private static WikiPageView View(WikiPage page, WikiPageRevision revision) => new(page.Id, page.ProjectId, page.Slug, page.Title,
        page.ParentId, page.Position, revision.ContentMarkdown, revision.ContentHtml, revision.Number, revision.Summary, page.UpdatedAt, page.Version);
    private static WikiRevisionDetailView RevisionView(WikiPageRevision revision) => new(revision.Number, revision.ContentMarkdown,
        revision.ContentHtml, revision.AuthorId, revision.At, revision.Summary);

    private static IResult? ValidateTitle(string? title) => string.IsNullOrWhiteSpace(title) || title.Trim().Length > 500
        ? Results.ValidationProblem(new Dictionary<string, string[]> { ["title"] = ["A title of 1-500 characters is required."] }) : null;
    private static IResult? ValidateContent(string? content) => content is null
        ? Results.ValidationProblem(new Dictionary<string, string[]> { ["contentMd"] = ["Markdown is required."] })
        : content.Length > MaxMarkdownLength
            ? Results.Problem("Markdown pages may not exceed 1 MB.", statusCode: StatusCodes.Status413PayloadTooLarge)
            : null;
    private static IResult Conflict(string? detail = null) => Results.Problem(detail ?? "The page was modified by someone else - refresh and try again.", statusCode: StatusCodes.Status409Conflict, type: ProblemTypes.Conflict);

    private static IReadOnlyList<WikiDiffHunk> UnifiedDiff(string before, string after)
    {
        var model = InlineDiffBuilder.Diff(before, after, false, false, LineChunker.Instance);
        var oldLine = 1; var newLine = 1;
        var lines = new List<WikiDiffLine>(model.Lines.Count);
        foreach (var line in model.Lines)
        {
            var kind = line.Type.ToString().ToLowerInvariant();
            int? oldNumber = kind is "inserted" ? null : oldLine++;
            int? newNumber = kind is "deleted" ? null : newLine++;
            lines.Add(new WikiDiffLine(kind, line.Text, oldNumber, newNumber));
        }
        return lines.Count == 0 ? [] : [new WikiDiffHunk(1, oldLine - 1, 1, newLine - 1, lines)];
    }

    internal static string Render(string markdown)
    {
        var html = Markdig.Markdown.ToHtml(markdown, Markdown);
        return WorkItemKey().Replace(html, match => $"<span data-item=\"{match.Value}\">{match.Value}</span>");
    }

    [GeneratedRegex("(?<![A-Za-z0-9-])[A-Z][A-Z0-9]{1,9}-[1-9][0-9]*(?![A-Za-z0-9-])", RegexOptions.Compiled)]
    private static partial Regex WorkItemKey();

    internal static async Task ReplaceItemLinksAsync(WikiDbContext db, WikiPage page, WikiPageRevision revision, IWorkItemLookup items, CancellationToken ct)
    {
        var keys = WorkItemReferenceParser.Parse(revision.ContentMarkdown).Select(x => $"{x.ProjectKey}-{x.ItemNumber}").ToArray();
        var found = await items.FindByKeysAsync(page.ProjectId, keys, ct);
        db.PageItemLinks.RemoveRange(db.PageItemLinks.Where(x => x.PageId == page.Id));
        foreach (var item in found) db.PageItemLinks.Add(new WikiPageItemLink { OrganizationId = page.OrganizationId, PageId = page.Id, ItemId = item.Id, RevisionId = revision.Id });
        page.LinkedItems(revision.Id, found.Select(x => x.Id).ToArray());
    }

    internal static async Task<string> UniqueSlugAsync(WikiDbContext db, Guid projectId, Guid? parentId, string title, Guid? exceptId, CancellationToken ct)
    {
        var stem = Slugify(title); var used = await db.Pages.Where(x => x.ProjectId == projectId && x.ParentId == parentId && x.Id != exceptId)
            .Select(x => x.Slug).ToListAsync(ct); var set = used.ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var n = 1; ; n++) { var candidate = n == 1 ? stem : $"{stem}-{n}"; if (!set.Contains(candidate)) return candidate; }
    }

    internal static string Slugify(string title)
    {
        var normalized = title.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormKD);
        var builder = new StringBuilder();
        foreach (var c in normalized) if (char.IsLetterOrDigit(c) && c <= 127) builder.Append(c); else if (builder.Length > 0 && builder[^1] != '-') builder.Append('-');
        var slug = builder.ToString().Trim('-'); return string.IsNullOrEmpty(slug) ? "page" : slug[..Math.Min(150, slug.Length)];
    }

    private static async Task<int> NextPositionAsync(WikiDbContext db, Guid projectId, Guid? parentId, CancellationToken ct) =>
        (await db.Pages.Where(x => x.ProjectId == projectId && x.ParentId == parentId)
            .Select(x => (int?)x.Position).MaxAsync(ct) ?? -1) + 1;
    private static Task<int> DepthAsync(WikiDbContext db, Guid pageId, CancellationToken ct) => db.Database.SqlQuery<int>($"""
        WITH RECURSIVE ancestors AS (SELECT id, parent_id, 1 AS depth FROM wiki.pages WHERE id = {pageId}
        UNION ALL SELECT p.id, p.parent_id, a.depth + 1 FROM wiki.pages p JOIN ancestors a ON p.id = a.parent_id)
        SELECT COALESCE(MAX(depth), 0) AS "Value" FROM ancestors
        """).SingleAsync(ct);
    private static Task<bool> IsDescendantAsync(WikiDbContext db, Guid ancestorId, Guid candidateId, CancellationToken ct) => db.Database.SqlQuery<bool>($"""
        WITH RECURSIVE descendants AS (SELECT id FROM wiki.pages WHERE parent_id = {ancestorId}
        UNION ALL SELECT p.id FROM wiki.pages p JOIN descendants d ON p.parent_id = d.id)
        SELECT EXISTS(SELECT 1 FROM descendants WHERE id = {candidateId}) AS "Value"
        """).SingleAsync(ct);
    private static Task<int> SubtreeDepthAsync(WikiDbContext db, Guid pageId, CancellationToken ct) => db.Database.SqlQuery<int>($"""
        WITH RECURSIVE descendants AS (SELECT id, 1 AS depth FROM wiki.pages WHERE id = {pageId}
        UNION ALL SELECT p.id, d.depth + 1 FROM wiki.pages p JOIN descendants d ON p.parent_id = d.id)
        SELECT COALESCE(MAX(depth), 0) AS "Value" FROM descendants
        """).SingleAsync(ct);
    private static Task<List<Guid>> SubtreeIdsAsync(WikiDbContext db, Guid pageId, CancellationToken ct) => db.Database.SqlQuery<Guid>($"""
        WITH RECURSIVE descendants AS (SELECT id FROM wiki.pages WHERE id = {pageId}
        UNION ALL SELECT p.id FROM wiki.pages p JOIN descendants d ON p.parent_id = d.id)
        SELECT id AS "Value" FROM descendants
        """).ToListAsync(ct);
}

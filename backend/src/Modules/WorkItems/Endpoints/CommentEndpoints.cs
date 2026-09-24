using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Aictiq.Modules.WorkItems.Domain;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Paging;
using Aictiq.SharedKernel.Tenancy;

namespace Aictiq.Modules.WorkItems.Endpoints;

public sealed record CommentAuthorView(string Id, string DisplayName, string? AvatarKey, bool IsAgent);
public sealed record CommentReactionView(string Emoji, int Count, bool ReactedByMe);
public sealed record CommentRevisionView(Guid Id, string BodyMarkdown, string BodyHtml, string EditedBy, DateTimeOffset EditedAt);
public sealed record CommentView(Guid Id, CommentAuthorView Author, string BodyMarkdown, string BodyHtml,
    DateTimeOffset CreatedAt, DateTimeOffset? EditedAt, DateTimeOffset? DeletedAt,
    IReadOnlyList<string> Mentions, IReadOnlyList<CommentReactionView> Reactions,
    IReadOnlyList<CommentRevisionView> Revisions, Guid? ParentCommentId = null);
/// <param name="ParentCommentId">The comment being answered. A reply to a reply joins the
/// same thread, under its first comment.</param>
public sealed record CreateCommentRequest(string? BodyMarkdown, Guid? ParentCommentId = null);
public sealed record UpdateCommentRequest(string? BodyMarkdown);
public sealed record ReactToCommentRequest(string? Emoji);

public static class CommentEndpoints
{
    // Letters in any script: a mention of @IvanaKovačević must not stop at the "č".
    private static readonly Regex Mention = new(@"(?<![\w@])@(?<name>[\p{L}\p{N}][\p{L}\p{N}-]{0,63})", RegexOptions.Compiled);
    private static readonly Regex NotMentionable = new(@"[^\p{L}\p{N}-]+", RegexOptions.Compiled);

    public static IEndpointRouteBuilder MapCommentEndpoints(this IEndpointRouteBuilder api)
    {
        var comments = api.MapGroup("/orgs/{orgSlug}/items/{itemKey}/comments").WithTags("Comments").RequireAuthorization();
        comments.MapGet("/", List).RequireOrgRole(OrgRole.Guest).RequireScope(Scopes.Read);
        comments.MapPost("/", Create).RequireOrgRole(OrgRole.Guest).RequireScope(Scopes.Write);
        comments.MapPatch("/{commentId:guid}", Edit).RequireOrgRole(OrgRole.Guest).RequireScope(Scopes.Write);
        comments.MapDelete("/{commentId:guid}", Delete).RequireOrgRole(OrgRole.Guest).RequireScope(Scopes.Write);
        comments.MapPut("/{commentId:guid}/reactions", React).RequireOrgRole(OrgRole.Guest).RequireScope(Scopes.Write);
        comments.MapDelete("/{commentId:guid}/reactions/{emoji}", Unreact).RequireOrgRole(OrgRole.Guest).RequireScope(Scopes.Write);
        return api;
    }

    private static async Task<IResult> List(string itemKey, WorkItemsDbContext db, IProjectAccess access,
        ICurrentUser user, IUserDirectory directory, int page = 1, int pageSize = 50, CancellationToken ct = default)
    {
        var item = await WorkItemEndpoints.FindVisible(db, access, user, itemKey, ct);
        if (item is null) return Results.NotFound();
        var take = Math.Clamp(pageSize == 0 ? 50 : pageSize, 1, 100);
        var onItem = db.Comments.AsNoTracking().Where(x => x.ItemId == item.Id);
        var hidden = await FactoryVisibility.HiddenAuthorsAsync(access, directory, user.UserId!, item.OrganizationId, onItem, ct);
        var query = onItem.WithoutFactory(db, hidden).OrderBy(x => x.CreatedAt).ThenBy(x => x.Id);
        var total = await query.CountAsync(ct);
        var comments = await query.Skip(Math.Max(0, page - 1) * take).Take(take).ToListAsync(ct);
        return Results.Ok(new PagedResult<CommentView>(await ViewsAsync(db, directory, user.UserId!, comments, ct), Math.Max(page, 1), take, total));
    }

    private static async Task<IResult> Create(string itemKey, CreateCommentRequest request, WorkItemsDbContext db,
        IProjectAccess access, ICurrentUser user, IUserDirectory directory, ICurrentTenant tenant, TimeProvider clock, CancellationToken ct)
    {
        var item = await WorkItemEndpoints.FindVisible(db, access, user, itemKey, ct);
        if (item is null) return Results.NotFound();
        if (await ArchivedAsync(access, item, ct) is { } archived) return archived;
        if (Validate(request.BodyMarkdown) is { } invalid) return invalid;
        Comment? thread = null;
        if (request.ParentCommentId is { } parentId)
        {
            var parent = await db.Comments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == parentId && x.ItemId == item.Id, ct);
            if (parent is null || await FactoryVisibility.HiddenAsync(access, directory, db, user.UserId!, parent, ct))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["parentCommentId"] = ["The comment being replied to is not on this item."] });
            thread = parent.ParentCommentId is { } rootId
                ? await db.Comments.AsNoTracking().FirstAsync(x => x.Id == rootId, ct)
                : parent;
            if (thread.DeletedAt is not null)
                return Results.Problem("A deleted comment's thread cannot be replied to.", statusCode: StatusCodes.Status409Conflict, type: ProblemTypes.Conflict);
        }
        var markdown = request.BodyMarkdown!.Trim();
        var mentions = await ResolveMentionsAsync(markdown, item.ProjectId, access, directory, ct);
        var now = clock.GetUtcNow();
        var comment = new Comment { OrganizationId = tenant.OrganizationId!.Value, ItemId = item.Id, ParentCommentId = thread?.Id, AuthorId = user.UserId!, BodyMarkdown = markdown, BodyHtml = WorkItemEndpoints.Render(markdown), MentionedUserIds = mentions.ToArray(), CreatedAt = now };
        comment.Added(item, mentions, thread?.AuthorId, now);
        db.Comments.Add(comment);
        await ItemWatcherRules.AddAsync(db, item, user.UserId, ItemWatchReason.Commenter, now, ct);
        foreach (var mentionedUserId in mentions)
            await ItemWatcherRules.AddAsync(db, item, mentionedUserId, ItemWatchReason.Mentioned, now, ct);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/v1/orgs/{item.OrganizationId}/items/{item.Key}/comments/{comment.Id}", (await ViewsAsync(db, directory, user.UserId!, [comment], ct))[0]);
    }

    private static async Task<IResult> Edit(string itemKey, Guid commentId, UpdateCommentRequest request,
        WorkItemsDbContext db, IProjectAccess access, ICurrentUser user, IUserDirectory directory, TimeProvider clock, CancellationToken ct)
    {
        var item = await WorkItemEndpoints.FindVisible(db, access, user, itemKey, ct);
        if (item is null) return Results.NotFound();
        if (await ArchivedAsync(access, item, ct) is { } archived) return archived;
        var comment = await db.Comments.FirstOrDefaultAsync(x => x.Id == commentId && x.ItemId == item.Id, ct);
        if (comment is null || await FactoryVisibility.HiddenAsync(access, directory, db, user.UserId!, comment, ct)) return Results.NotFound();
        var role = await access.GetProjectRoleAsync(user.UserId!, item.ProjectId, ct);
        if (comment.AuthorId != user.UserId && (role is not { } projectRole || !projectRole.Satisfies(ProjectRole.Admin))) return Results.Forbid();
        if (comment.DeletedAt is not null) return Results.Problem("A deleted comment cannot be edited.", statusCode: StatusCodes.Status409Conflict, type: ProblemTypes.Conflict);
        if (Validate(request.BodyMarkdown) is { } invalid) return invalid;
        var markdown = request.BodyMarkdown!.Trim();
        if (markdown != comment.BodyMarkdown)
        {
            var now = clock.GetUtcNow();
            db.CommentRevisions.Add(new CommentRevision { CommentId = comment.Id, BodyMarkdown = comment.BodyMarkdown, BodyHtml = comment.BodyHtml, EditedBy = user.UserId!, EditedAt = now });
            comment.BodyMarkdown = markdown; comment.BodyHtml = WorkItemEndpoints.Render(markdown);
            var mentions = await ResolveMentionsAsync(markdown, item.ProjectId, access, directory, ct);
            comment.Mentioned(item, mentions.Except(comment.MentionedUserIds, StringComparer.Ordinal).ToArray(), now);
            comment.MentionedUserIds = mentions.ToArray();
            foreach (var mentionedUserId in mentions)
                await ItemWatcherRules.AddAsync(db, item, mentionedUserId, ItemWatchReason.Mentioned, now, ct);
            comment.EditedAt = now;
            await db.SaveChangesAsync(ct);
        }
        return Results.Ok((await ViewsAsync(db, directory, user.UserId!, [comment], ct))[0]);
    }

    private static async Task<IResult> Delete(string itemKey, Guid commentId, WorkItemsDbContext db, IProjectAccess access,
        ICurrentUser user, IUserDirectory directory, TimeProvider clock, CancellationToken ct)
    {
        var item = await WorkItemEndpoints.FindVisible(db, access, user, itemKey, ct);
        if (item is null) return Results.NotFound();
        if (await ArchivedAsync(access, item, ct) is { } archived) return archived;
        var comment = await db.Comments.FirstOrDefaultAsync(x => x.Id == commentId && x.ItemId == item.Id, ct);
        if (comment is null || await FactoryVisibility.HiddenAsync(access, directory, db, user.UserId!, comment, ct)) return Results.NotFound();
        var role = await access.GetProjectRoleAsync(user.UserId!, item.ProjectId, ct);
        if (comment.AuthorId != user.UserId && (role is not { } projectRole || !projectRole.Satisfies(ProjectRole.Admin))) return Results.Forbid();
        if (comment.DeletedAt is null) { comment.DeletedAt = clock.GetUtcNow(); await db.SaveChangesAsync(ct); }
        return Results.NoContent();
    }

    private static async Task<IResult> React(string itemKey, Guid commentId, ReactToCommentRequest request, WorkItemsDbContext db,
        IProjectAccess access, ICurrentUser user, IUserDirectory directory, ICurrentTenant tenant, TimeProvider clock, CancellationToken ct)
    {
        var item = await WorkItemEndpoints.FindVisible(db, access, user, itemKey, ct);
        if (item is null) return Results.NotFound();
        if (await ArchivedAsync(access, item, ct) is { } archived) return archived;
        if (string.IsNullOrWhiteSpace(request.Emoji) || request.Emoji.Trim().Length > 32) return Results.ValidationProblem(new Dictionary<string, string[]> { ["emoji"] = ["An emoji of 1-32 characters is required."] });
        var comment = await db.Comments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == commentId && x.ItemId == item.Id, ct);
        if (comment is null || await FactoryVisibility.HiddenAsync(access, directory, db, user.UserId!, comment, ct)) return Results.NotFound();
        db.CommentReactions.Add(new CommentReaction { OrganizationId = tenant.OrganizationId!.Value, CommentId = commentId, UserId = user.UserId!, Emoji = request.Emoji.Trim(), CreatedAt = clock.GetUtcNow() });
        try { await db.SaveChangesAsync(ct); } catch (DbUpdateException) { db.ChangeTracker.Clear(); }
        return Results.NoContent();
    }

    private static async Task<IResult> Unreact(string itemKey, Guid commentId, string emoji, WorkItemsDbContext db,
        IProjectAccess access, ICurrentUser user, IUserDirectory directory, CancellationToken ct)
    {
        var item = await WorkItemEndpoints.FindVisible(db, access, user, itemKey, ct);
        if (item is null) return Results.NotFound();
        var comment = await db.Comments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == commentId && x.ItemId == item.Id, ct);
        if (comment is null || await FactoryVisibility.HiddenAsync(access, directory, db, user.UserId!, comment, ct)) return Results.NotFound();
        var reaction = await db.CommentReactions.FirstOrDefaultAsync(x => x.CommentId == commentId && x.UserId == user.UserId && x.Emoji == emoji, ct);
        if (reaction is not null) { db.CommentReactions.Remove(reaction); await db.SaveChangesAsync(ct); }
        return Results.NoContent();
    }

    private static IResult? Validate(string? markdown) => string.IsNullOrWhiteSpace(markdown) || markdown.Trim().Length > 20_000
        ? Results.ValidationProblem(new Dictionary<string, string[]> { ["bodyMarkdown"] = ["Comment text of 1-20,000 characters is required."] }) : null;

    private static async Task<IResult?> ArchivedAsync(IProjectAccess access, WorkItem item, CancellationToken ct)
    {
        var project = await access.FindProjectAsync(item.OrganizationId, item.ProjectKey, ct);
        return project is { IsArchived: true }
            ? Results.Problem(title: "This project is archived.", detail: $"{project.Key} is read-only until it is un-archived.", type: ProblemTypes.ProjectArchived, statusCode: StatusCodes.Status409Conflict)
            : null;
    }

    private static async Task<IReadOnlyList<string>> ResolveMentionsAsync(string markdown, Guid projectId,
        IProjectAccess access, IUserDirectory directory, CancellationToken ct)
    {
        var tokens = Mention.Matches(markdown).Select(x => x.Groups["name"].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (tokens.Length == 0) return [];
        var people = await directory.GetAsync(await access.ListProjectMemberIdsAsync(projectId, ct), ct);
        var matched = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in tokens)
        {
            // The whole name first: "@Ana" is Ana when there is one, even beside an Ana Horvat.
            var candidates = people.Values.Where(person => MatchesMention(MentionToken(person.DisplayName), token)).Select(person => person.Id).Distinct().ToArray();
            if (candidates.Length == 0)
                candidates = people.Values.Where(person => MatchesMention(FirstNameToken(person.DisplayName), token)).Select(person => person.Id).Distinct().ToArray();
            if (candidates.Length == 1) matched.Add(candidates[0]);
        }
        return matched.Order(StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// A token names someone by their whole display name with everything but letters, digits
    /// and hyphens dropped (the picker writes "@AnaKovač" for "Ana Kovač"), or by their first
    /// name alone. The caller drops a token that fits more than one person.
    /// </summary>
    private static string MentionToken(string displayName) => NotMentionable.Replace(displayName, "");

    private static string FirstNameToken(string displayName) =>
        MentionToken(displayName.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "");

    private static bool MatchesMention(string candidate, string token) =>
        candidate.Length > 0 && string.Equals(candidate, token, StringComparison.OrdinalIgnoreCase);

    private static async Task<List<CommentView>> ViewsAsync(WorkItemsDbContext db, IUserDirectory directory, string currentUserId, IReadOnlyList<Comment> comments, CancellationToken ct)
    {
        if (comments.Count == 0) return [];
        var ids = comments.Select(x => x.Id).ToArray();
        var authors = await directory.GetAsync(comments.Select(x => x.AuthorId).Distinct().ToArray(), ct);
        var reactions = await db.CommentReactions.AsNoTracking().Where(x => ids.Contains(x.CommentId)).ToListAsync(ct);
        var revisions = await db.CommentRevisions.AsNoTracking().Where(x => ids.Contains(x.CommentId)).OrderBy(x => x.EditedAt).ToListAsync(ct);
        return comments.Select(comment =>
        {
            var author = authors.TryGetValue(comment.AuthorId, out var summary) ? summary : new UserSummary(comment.AuthorId, "Unknown user", null, false);
            var grouped = reactions.Where(x => x.CommentId == comment.Id).GroupBy(x => x.Emoji).OrderBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => new CommentReactionView(x.Key, x.Count(), x.Any(y => y.UserId == currentUserId))).ToArray();
            var history = revisions.Where(x => x.CommentId == comment.Id).Select(x => new CommentRevisionView(x.Id, x.BodyMarkdown, x.BodyHtml, x.EditedBy, x.EditedAt)).ToArray();
            return comment.DeletedAt is null
                ? new CommentView(comment.Id, new CommentAuthorView(author.Id, author.DisplayName, author.AvatarKey, author.IsAgent), comment.BodyMarkdown, comment.BodyHtml, comment.CreatedAt, comment.EditedAt, null, comment.MentionedUserIds, grouped, history, comment.ParentCommentId)
                : new CommentView(comment.Id, new CommentAuthorView(author.Id, author.DisplayName, author.AvatarKey, author.IsAgent), "", "", comment.CreatedAt, comment.EditedAt, comment.DeletedAt, [], grouped, history, comment.ParentCommentId);
        }).ToList();
    }
}

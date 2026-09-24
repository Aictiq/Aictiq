using System.ComponentModel;
using Aictiq.Modules.WorkItems.Domain;
using Aictiq.Modules.WorkItems.Endpoints;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Mcp;
using Aictiq.SharedKernel.Tenancy;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Aictiq.Modules.WorkItems.Mcp;

public sealed record McpItem(string Key, string Title, WorkItemType Type, string State, uint Version,
    string? AssigneeId, string? ClaimedBy, DateTimeOffset? ClaimHeartbeatAt, decimal? RemainingHours);

/// <summary>
/// Agent-facing work-item operations.  They deliberately use the same contexts and tenant
/// filter as HTTP endpoints, so a PAT cannot use MCP to reach a different organization.
///
/// Within the organization the rules are the REST rules, not weaker ones: a project is
/// addressed through <see cref="IProjectAccess"/> so a private project the agent is not on
/// is "not found", a write needs the project Member role and an un-archived project, and
/// an assignee has to be someone who can see the project. The tenant filter alone would
/// let any member of the organization read and write any project in it.
/// </summary>
[McpServerToolType]
public sealed class WorkItemMcpTools(WorkItemsDbContext db, ICurrentTenant tenant, ICurrentUser user,
    IProjectAccess access, IWikiPageAccess pages, IUserDirectory directory, IBlobStorage storage, TimeProvider clock, IConfiguration configuration)
{
    private const int MaxMcpTextAttachmentBytes = 64 * 1024;
    [McpServerTool(Name = "search_items", ReadOnly = true)]
    [Description("Searches work items in a project. Filter uses Aictiq's normal item-filter grammar.")]
    public async Task<IReadOnlyList<McpItem>> SearchItems(string project, string? filter = null, string? q = null, int limit = 25, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 200) throw new McpException("limit must be between 1 and 200.");
        // A bad filter is an error the agent can act on, not an empty result it would read as
        // "nothing matches". Filtering and searching happen before the limit, never after it:
        // filtering the first page would miss every match beyond it.
        var parsed = ItemFilter.Parse(filter); if (parsed.Error is not null) throw new McpException(parsed.Error);
        var visible = await ProjectAsync(project, write: false, cancellationToken); if (visible is null) return [];
        var (query, error) = await ItemQueries.ApplyFilterAsync(db.Items.AsNoTracking().Where(x => x.ProjectId == visible.Id), parsed, visible.Id, db, directory, user.UserId, cancellationToken);
        if (error is not null) throw new McpException(error);
        query = await ItemQueries.ApplySearchAsync(query, q, visible.Id, db, cancellationToken);
        var rows = await query.OrderBy(x => x.Rank).ThenBy(x => x.Number).Take(limit).ToListAsync(cancellationToken);
        return await Views(rows, cancellationToken);
    }

    /// <summary>
    /// The first call an agent makes after <c>whoami</c>: what could I pick up right now.
    ///
    /// "Ready" is unfinished work that nobody else holds and that is either unassigned or
    /// already mine - an agent resuming after a restart must find its own items, not an
    /// empty list. Blocked items are excluded: an agent that claims one is stuck by
    /// definition, and would then have to work that out from the relations itself.
    /// Sprint first, then rank: the team's own ordering is the answer to "what next".
    /// </summary>
    [McpServerTool(Name = "list_ready_work", ReadOnly = true)]
    [Description("Work in this project that is ready to pick up: unfinished, unblocked, unclaimed by anyone else, and either unassigned or assigned to you. Sprint work first, then backlog rank.")]
    public async Task<IReadOnlyList<McpItem>> ListReadyWork(string project, Guid? team = null, CancellationToken cancellationToken = default)
    {
        var me = user.UserId;
        var visible = await ProjectAsync(project, write: false, cancellationToken); if (visible is null) return [];
        var rows = await db.Items.AsNoTracking()
            .Where(x => x.ProjectId == visible.Id
                && (team == null || x.TeamId == team)
                && (x.AssigneeId == null || x.AssigneeId == me)
                && (x.ClaimedBy == null || x.ClaimedBy == me)
                && db.WorkflowStates.Any(s => s.Id == x.StateId
                    && (s.Category == WorkflowStateCategory.Proposed || s.Category == WorkflowStateCategory.Active))
                && !db.ItemRelations.Any(r => r.TargetId == x.Id && r.Kind == ItemRelationKind.Blocks))
            .OrderByDescending(x => x.SprintId != null).ThenBy(x => x.Rank).ThenByDescending(x => x.Priority)
            .Take(100)
            .ToListAsync(cancellationToken);
        return await Views(rows, cancellationToken);
    }

    [McpServerTool(Name = "get_item", ReadOnly = true)]
    public async Task<object?> GetItem(string key, string? range = null, CancellationToken cancellationToken = default)
    {
        var item = await Visible(key, cancellationToken); if (item is null) throw new McpAnswerException($"item '{key}' not found or no access");
        var chain = new List<string>();
        for (var parent = item.ParentId; parent is { } id && chain.Count < 10;)
        { var row = await db.Items.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken); if (row is null) break; chain.Add(row.Key); parent = row.ParentId; }
        var visibleComments = await VisibleCommentsAsync(item, cancellationToken);
        var comments = await visibleComments.Where(x => x.DeletedAt == null).OrderBy(x => x.CreatedAt).Take(200).Select(x => new { x.AuthorId, x.BodyMarkdown, x.CreatedAt }).ToListAsync(cancellationToken);
        var links = await db.ItemLinks.AsNoTracking().Where(x => x.ItemId == item.Id).Take(200).Select(x => new { x.Kind, x.Url, x.Title }).ToListAsync(cancellationToken);
        var attachments = await db.Attachments.AsNoTracking()
            .Where(x => x.Status == AttachmentStatus.Committed &&
                (x.ItemId == item.Id || x.CommentId != null && visibleComments.Any(comment => comment.Id == x.CommentId && comment.DeletedAt == null)))
            .OrderBy(x => x.CreatedAt)
            .Select(x => new { x.Id, x.FileName, x.ContentType, x.SizeBytes, x.CommentId })
            .ToListAsync(cancellationToken);
        var (start, length) = ParseRange(range); var body = item.DescriptionMarkdown;
        start = Math.Min(start, body.Length); var take = Math.Min(length ?? 20_000, body.Length - start);
        var truncated = start + take < body.Length;
        return new
        {
            contentStart = McpContentBoundary.Begin,
            item.Key, item.Title, descriptionMarkdown = body.Substring(start, take), truncated,
            range = truncated ? $"{start + take}:20000" : null,
            item.Version, parentChain = chain, comments, links, attachments,
            contentEnd = McpContentBoundary.End
        };
    }

    [McpServerTool(Name = "get_attachment", ReadOnly = true)]
    [Description("Returns an image attachment as image content, a small text attachment as text, or download instructions for other files.")]
    public async Task<CallToolResult> GetAttachment(Guid id, CancellationToken cancellationToken = default)
    {
        var attachment = await db.Attachments.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id && x.Status == AttachmentStatus.Committed, cancellationToken);
        if (attachment is null || await access.GetProjectRoleAsync(user.UserId!, attachment.ProjectId, cancellationToken) is not { } role ||
            !role.Satisfies(ProjectRole.Guest))
            throw new McpAnswerException("attachment not found or no access");
        // A page attachment is part of that page. Project membership alone must not turn an
        // opaque attachment id into a bypass for a restricted wiki subtree; this mirrors the
        // HTTP download endpoint rather than giving MCP a broader read surface.
        if (attachment.WikiPageId is { } pageId && !await pages.CanReadAsync(pageId, attachment.ProjectId, user.UserId!, cancellationToken))
            throw new McpAnswerException("attachment not found or no access");

        var blob = await storage.OpenReadAsync(attachment.ObjectKey, cancellationToken);
        if (blob is null) throw new McpAnswerException("attachment not found or no access");
        await using (blob.Content)
        {
            if (AttachmentImages.IsImage(attachment.ContentType))
            {
                using var bytes = new MemoryStream();
                await blob.Content.CopyToAsync(bytes, cancellationToken);
                return new CallToolResult { Content = [ImageContentBlock.FromBytes(bytes.ToArray(), attachment.ContentType)] };
            }

            if (attachment.ContentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) && attachment.SizeBytes <= MaxMcpTextAttachmentBytes)
            {
                using var reader = new StreamReader(blob.Content, detectEncodingFromByteOrderMarks: true, leaveOpen: false);
                var text = await reader.ReadToEndAsync(cancellationToken);
                return new CallToolResult { Content = [new TextContentBlock { Text = text }] };
            }
        }
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = $"{attachment.FileName} ({attachment.ContentType}, {attachment.SizeBytes} bytes). Download it with `aictiq attachment get {attachment.Id}`." }]
        };
    }

    [McpServerTool(Name = "claim_item")]
    public async Task<object?> ClaimItem(string key, uint version, CancellationToken cancellationToken = default)
    {
        var item = await WritableAsync(key, cancellationToken);
        var state = await db.WorkflowStates.Where(s => s.Category == WorkflowStateCategory.Active && db.Workflows.Any(w => w.Id == s.WorkflowId && w.ProjectId == item.ProjectId)).OrderBy(s => s.Position).Select(s => (Guid?)s.Id).FirstOrDefaultAsync(cancellationToken);
        if (state is null) return new { conflict = "No Active workflow state exists." };
        var now = clock.GetUtcNow(); var stale = TimeSpan.FromMinutes(Math.Max(1, configuration.GetValue("Claims:StaleAfterMinutes", 30)));
        var updated = await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE work.items SET claimed_by = {user.UserId!}, claimed_at = {now}, claim_heartbeat_at = {now}, assignee_id = {user.UserId!}, state_id = {state.Value}, updated_at = {now} WHERE id = {item.Id} AND xmin::text::bigint = {(long)version} AND (claimed_by IS NULL OR claim_heartbeat_at < {now - stale})""", cancellationToken);
        return updated == 1 ? new { claimed = true } : new { conflict = "already-claimed" };
    }

    [McpServerTool(Name = "release_item")]
    public async Task<bool> ReleaseItem(string key, CancellationToken cancellationToken = default)
    { var item = await TryWritableAsync(key, cancellationToken); if (item is null || item.ClaimedBy != user.UserId) return false; item.ClaimedBy = null; item.ClaimedAt = null; item.ClaimHeartbeatAt = null; item.UpdatedAt = clock.GetUtcNow(); await db.SaveChangesAsync(cancellationToken); return true; }

    [McpServerTool(Name = "heartbeat")]
    public async Task<bool> Heartbeat(string key, CancellationToken cancellationToken = default)
    { var item = await TryWritableAsync(key, cancellationToken); if (item is null || item.ClaimedBy != user.UserId) return false; item.ClaimHeartbeatAt = clock.GetUtcNow(); item.UpdatedAt = item.ClaimHeartbeatAt.Value; await db.SaveChangesAsync(cancellationToken); return true; }

    [McpServerTool(Name = "add_comment")]
    public async Task<object?> AddComment(string key, string bodyMarkdown, CancellationToken cancellationToken = default)
    { if (!ValidBody(bodyMarkdown)) throw new McpException("bodyMarkdown must contain 1 to 20,000 characters."); var item = await Visible(key, cancellationToken); if (item is null) throw new McpAnswerException($"item '{key}' not found or no access"); await RefuseArchivedAsync(item, cancellationToken); var comment = new Comment { OrganizationId = tenant.OrganizationId!.Value, ItemId = item.Id, AuthorId = user.UserId!, BodyMarkdown = bodyMarkdown.Trim(), BodyHtml = WorkItemEndpoints.Render(bodyMarkdown.Trim()), CreatedAt = clock.GetUtcNow() }; db.Comments.Add(comment); await db.SaveChangesAsync(cancellationToken); return new { comment.Id, comment.CreatedAt }; }

    [McpServerTool(Name = "list_comments", ReadOnly = true)]
    public async Task<IReadOnlyList<object>> ListComments(string key, int limit = 100, CancellationToken cancellationToken = default)
    { if (limit is < 1 or > 200) throw new McpException("limit must be between 1 and 200."); var item = await Visible(key, cancellationToken); if (item is null) return []; return await (await VisibleCommentsAsync(item, cancellationToken)).Where(x => x.DeletedAt == null).OrderBy(x => x.CreatedAt).Take(limit).Select(x => (object)new { contentStart = McpContentBoundary.Begin, x.Id, x.AuthorId, x.BodyMarkdown, x.CreatedAt, contentEnd = McpContentBoundary.End }).ToListAsync(cancellationToken); }

    /// <summary>The item's comments, less the factory's when the caller may not operate it (<see cref="FactoryVisibility"/>).</summary>
    private async Task<IQueryable<Comment>> VisibleCommentsAsync(WorkItem item, CancellationToken cancellationToken)
    {
        var onItem = db.Comments.AsNoTracking().Where(x => x.ItemId == item.Id);
        return onItem.WithoutFactory(db, await FactoryVisibility.HiddenAuthorsAsync(access, directory, user.UserId!, item.OrganizationId, onItem, cancellationToken));
    }

    /// <summary>
    /// Editing a comment is what makes an agent's progress reporting idempotent: it finds
    /// its own marked comment with <c>list_comments</c> and rewrites it, instead of adding
    /// a line to the thread on every loop. Like the REST endpoint, the previous body is
    /// kept as a revision - an agent overwriting a person's paragraph must still be
    /// recoverable - and only the author may edit, so one agent cannot rewrite another's.
    /// </summary>
    [McpServerTool(Name = "update_comment")]
    [Description("Replaces the body of a comment this identity wrote. Use with a marker such as <!-- aictiq:progress --> to keep one editable progress comment per item instead of a new comment per step.")]
    public async Task<object?> UpdateComment(string key, Guid commentId, string bodyMarkdown, CancellationToken cancellationToken = default)
    {
        var item = await Visible(key, cancellationToken);
        if (!ValidBody(bodyMarkdown)) throw new McpException("bodyMarkdown must contain 1 to 20,000 characters.");
        if (item is null) throw new McpAnswerException($"item '{key}' not found or no access");
        await RefuseArchivedAsync(item, cancellationToken);
        var comment = await db.Comments.FirstOrDefaultAsync(x => x.Id == commentId && x.ItemId == item.Id && x.DeletedAt == null, cancellationToken);
        if (comment is null || comment.AuthorId != user.UserId) throw new McpAnswerException("comment not found or no access");
        var markdown = bodyMarkdown.Trim();
        if (markdown == comment.BodyMarkdown) return new { comment.Id, comment.EditedAt, unchanged = true };
        var now = clock.GetUtcNow();
        db.CommentRevisions.Add(new CommentRevision { CommentId = comment.Id, BodyMarkdown = comment.BodyMarkdown, BodyHtml = comment.BodyHtml, EditedBy = user.UserId!, EditedAt = now });
        comment.BodyMarkdown = markdown;
        comment.BodyHtml = WorkItemEndpoints.Render(markdown);
        comment.EditedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return new { comment.Id, comment.EditedAt, unchanged = false };
    }

    [McpServerTool(Name = "transition_item")]
    public async Task<object?> TransitionItem(string key, uint version, string toState, CancellationToken cancellationToken = default)
    { var item = await WritableAsync(key, cancellationToken); if (item.Version != version) throw new McpAnswerException("conflict: version changed"); var target = await db.WorkflowStates.FirstOrDefaultAsync(x => x.Name.ToLower() == toState.ToLower() && db.Workflows.Any(w => w.Id == x.WorkflowId && w.ProjectId == item.ProjectId), cancellationToken); if (target is null) throw new McpAnswerException($"state '{toState}' not found");
        // The workflow's transition rules bind an agent exactly as they bind the REST client.
        var rules = db.WorkflowTransitions.Where(x => x.WorkflowId == target.WorkflowId);
        if (await rules.AnyAsync(cancellationToken) && !await rules.AnyAsync(x => x.ToStateId == target.Id && (x.FromStateId == null || x.FromStateId == item.StateId), cancellationToken)) throw new McpAnswerException($"conflict: the workflow does not allow a transition to '{target.Name}' from the current state");
        item.Transition(target.Id, target.Category, user.UserId!, clock.GetUtcNow()); await db.SaveChangesAsync(cancellationToken); return new { item.Key, item.Version }; }

    [McpServerTool(Name = "set_remaining_hours")]
    public async Task<bool> SetRemainingHours(string key, uint version, decimal hours, CancellationToken cancellationToken = default)
    { var item = await TryWritableAsync(key, cancellationToken); if (item is null || item.Version != version || hours < 0) return false; item.RemainingHours = hours; item.UpdatedAt = clock.GetUtcNow(); await db.SaveChangesAsync(cancellationToken); return true; }

    // These compact aliases keep the complete documented v1 work-item tool surface
    // available to clients; richer validation remains in the matching REST endpoint.
    [McpServerTool(Name = "create_item")] public Task<object?> CreateItem(string project, string title, CancellationToken cancellationToken = default) => CreateBasic(project, title, null, cancellationToken);
    [McpServerTool(Name = "create_subtask")] public Task<object?> CreateSubtask(string parentKey, string title, CancellationToken cancellationToken = default) => CreateBasic(null, title, parentKey, cancellationToken);
    [McpServerTool(Name = "update_item")] public async Task<bool> UpdateItem(string key, uint version, string? title = null, string? description = null, CancellationToken cancellationToken = default) { if (title is { Length: > 500 }) throw new McpException("title must be 500 characters or fewer."); if (description is { Length: > 20_000 }) throw new McpException("description must be 20,000 characters or fewer."); var item = await TryWritableAsync(key, cancellationToken); if (item is null || item.Version != version) return false; if (!string.IsNullOrWhiteSpace(title)) item.Title = title.Trim(); if (description is not null) { item.DescriptionMarkdown = description; item.DescriptionHtml = WorkItemEndpoints.Render(description); } item.UpdatedAt = clock.GetUtcNow(); await db.SaveChangesAsync(cancellationToken); return true; }
    // http(s) only, like the REST endpoint: a stored javascript: or file: URL is rendered as a
    // link on the item page, and a private address is a way to point a person at a neighbour.
    [McpServerTool(Name = "link_item")] public async Task<bool> LinkItem(string key, string url, CancellationToken cancellationToken = default) { var item = await TryWritableAsync(key, cancellationToken); if (item is null || !LinkPreviewFetcher.TryParsePublicUrl(url, out var parsed, out _)) return false; var uri = parsed!; db.ItemLinks.Add(new ItemLink { OrganizationId = tenant.OrganizationId!.Value, ItemId = item.Id, Kind = ItemLinkKind.Url, Provider = "url", ExternalId = uri.AbsoluteUri, Url = uri.AbsoluteUri, CreatedAt = clock.GetUtcNow() }); try { await db.SaveChangesAsync(cancellationToken); return true; } catch (DbUpdateException) { return true; } }
    [McpServerTool(Name = "bulk_update")] public async Task<int> BulkUpdate(string project, IReadOnlyList<string> keys, string? assigneeId = null, CancellationToken cancellationToken = default) { var writable = await ProjectAsync(project, write: true, cancellationToken) ?? throw new McpAnswerException($"project '{project}' not found or no access"); if (!await WorkItemEndpoints.AssignableAsync(access, writable.Id, assigneeId, cancellationToken)) throw new McpAnswerException("assigneeId must be someone who can see this project"); var numbers = keys.Select(WorkItemEndpoints.ParseKey).Where(k => k.Number is not null && k.ProjectKey == writable.Key).Select(k => k.Number!.Value).Distinct().ToArray(); var rows = numbers.Length == 0 ? [] : await db.Items.Where(x => x.ProjectId == writable.Id && numbers.Contains(x.Number)).ToListAsync(cancellationToken); foreach (var row in rows) { row.AssigneeId = assigneeId; row.UpdatedAt = clock.GetUtcNow(); } await db.SaveChangesAsync(cancellationToken); return rows.Count; }

    private async Task<object?> CreateBasic(string? project, string title, string? parentKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Trim().Length > 500) throw new McpException("title must contain 1 to 500 characters.");
        // Creating needs the Member role on the project - resolved through Tenancy, never by
        // finding some item that happens to carry the key - and a parent needs to be writable.
        WorkItem? parent = parentKey is null ? null : await WritableAsync(parentKey, ct);
        var writable = parent is null ? await ProjectAsync(project, write: true, ct) : await access.FindProjectAsync(parent.OrganizationId, parent.ProjectKey, ct);
        if (writable is null) throw new McpAnswerException($"project '{project}' not found or no access");
        var projectKey = writable.Key; Guid? projectId = writable.Id;
        var state = await db.WorkflowStates.Where(s => s.IsInitial && db.Workflows.Any(w => w.Id == s.WorkflowId && w.ProjectId == projectId)).Select(s => s.Id).FirstOrDefaultAsync(ct); if (state == Guid.Empty) throw new McpAnswerException($"project '{projectKey}' has no initial workflow state");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var number = await WorkItemEndpoints.NextNumberAsync(db, tenant.OrganizationId!.Value, projectId.Value, ct);
        var type = parent is null ? WorkItemType.Bug : WorkItemType.Task;
        var item = new WorkItem { OrganizationId = tenant.OrganizationId!.Value, ProjectId = projectId.Value, ProjectKey = projectKey, Number = number, Type = type, Title = title.Trim(), StateId = state, ParentId = parent?.Id, Rank = "m", CreatedBy = user.UserId!, CreatedAt = clock.GetUtcNow(), UpdatedAt = clock.GetUtcNow() }; db.Items.Add(item); await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); return new { item.Key, item.Version };
    }
    private async Task<WorkItem?> Visible(string key, CancellationToken ct) { var item = await WorkItemEndpoints.FindVisible(db, access, user, key, ct); return item; }

    /// <summary>The project, if the caller may see it - or write to it, which also means it is not archived.</summary>
    private async Task<ProjectRef?> ProjectAsync(string? project, bool write, CancellationToken ct)
    {
        var key = project?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(key) || tenant.OrganizationId is not { } organizationId) return null;
        var found = await access.FindProjectAsync(organizationId, key, ct);
        if (found is null || await access.GetProjectRoleAsync(user.UserId!, found.Id, ct) is not { } role) return null;
        if (!write) return found;
        if (!role.Satisfies(ProjectRole.Member)) return null;
        if (found.IsArchived) throw new McpAnswerException($"project '{found.Key}' is archived and read-only");
        return found;
    }

    /// <summary>Visible, writable (project Member) and not in an archived project; answers in words otherwise.</summary>
    private async Task<WorkItem> WritableAsync(string key, CancellationToken ct)
    {
        var item = await WorkItemEndpoints.FindWritable(db, access, user, key, ct) ?? throw new McpAnswerException($"item '{key}' not found or no access");
        await RefuseArchivedAsync(item, ct);
        return item;
    }

    /// <summary>For the boolean tools, whose contract is false rather than a message.</summary>
    private async Task<WorkItem?> TryWritableAsync(string key, CancellationToken ct)
    {
        var item = await WorkItemEndpoints.FindWritable(db, access, user, key, ct);
        if (item is null) return null;
        var project = await access.FindProjectAsync(item.OrganizationId, item.ProjectKey, ct);
        return project is { IsArchived: true } ? null : item;
    }

    private async Task RefuseArchivedAsync(WorkItem item, CancellationToken ct)
    {
        var project = await access.FindProjectAsync(item.OrganizationId, item.ProjectKey, ct);
        if (project is { IsArchived: true }) throw new McpAnswerException($"project '{item.ProjectKey}' is archived and read-only");
    }
    private async Task<IReadOnlyList<McpItem>> Views(IReadOnlyList<WorkItem> rows, CancellationToken ct) { var ids = rows.Select(x => x.StateId).Distinct().ToArray(); var states = await db.WorkflowStates.AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Name, ct); return rows.Select(x => new McpItem(x.Key, x.Title, x.Type, states.GetValueOrDefault(x.StateId, "Unknown"), x.Version, x.AssigneeId, x.ClaimedBy, x.ClaimHeartbeatAt, x.RemainingHours)).ToList(); }
    private static (int Start, int? Length) ParseRange(string? range)
    {
        if (string.IsNullOrWhiteSpace(range)) return (0, null);
        var parts = range.Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length is < 1 or > 2 || !int.TryParse(parts[0], out var start) || start < 0 || parts.Length == 2 && (!int.TryParse(parts[1], out var length) || length is < 1 or > 20_000)) throw new McpException("range must be start:length, with a length from 1 to 20,000.");
        return (start, parts.Length == 2 ? int.Parse(parts[1]) : null);
    }
    private static bool ValidBody(string? body) => !string.IsNullOrWhiteSpace(body) && body.Trim().Length <= 20_000;
}

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Aictiq.Modules.WorkItems.Domain;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Http;
using Aictiq.SharedKernel.Storage;
using Aictiq.SharedKernel.Tenancy;

namespace Aictiq.Modules.WorkItems.Endpoints;

public sealed record CommitAttachmentRequest(Guid? ItemId, Guid? CommentId, Guid? PageId = null);
public sealed record AttachmentView(Guid Id, Guid ProjectId, Guid? ItemId, Guid? CommentId, Guid? PageId, string FileName,
    string ContentType, long SizeBytes, string UploadedBy, DateTimeOffset CreatedAt);

public static class AttachmentEndpoints
{
    public static IEndpointRouteBuilder MapAttachmentEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapGroup("/orgs/{orgSlug}/projects/{projectKey}/attachments").WithTags("Attachments").RequireAuthorization()
            // Multipart from the SPA, CLI and agents alike; CSRF for cookie callers is
            // CookieCsrfMiddleware's X-Aictiq-Request rule, not an antiforgery token.
            .MapPost("/", Upload).RequireProjectRole(ProjectRole.Member).RequireProjectWritable().RequireScope(Scopes.Write)
            .RequireOperationRateLimit(OperationRateLimiter.Uploads).DisableAntiforgery();
        var attachments = api.MapGroup("/orgs/{orgSlug}/attachments").WithTags("Attachments").RequireAuthorization();
        attachments.MapPost("/{attachmentId:guid}/commit", Commit).RequireOrgRole(OrgRole.Member).RequireScope(Scopes.Write)
            .RequireOperationRateLimit(OperationRateLimiter.Uploads);
        // An attachment id names immutable bytes, so an <img> may keep them for a while
        // rather than re-download on every render. Private: it is behind a login.
        attachments.MapGet("/{attachmentId:guid}/download", Download).RequireOrgRole(OrgRole.Guest).RequireScope(Scopes.Read)
            .WithMetadata(new CacheableResponseAttribute());
        attachments.MapDelete("/{attachmentId:guid}", Delete).RequireOrgRole(OrgRole.Member).RequireScope(Scopes.Write);
        api.MapGroup("/orgs/{orgSlug}/items/{itemKey}/attachments").WithTags("Attachments").RequireAuthorization()
            .MapGet("/", List).RequireOrgRole(OrgRole.Guest).RequireScope(Scopes.Read);
        return api;
    }

    /// <summary>
    /// Stores a file as a <em>pending</em> attachment; <c>commit</c> gives it an owner. Images
    /// are re-encoded as WebP no wider than <see cref="AttachmentsOptions.MaxImageWidth"/>, so
    /// what is stored and served is the processed file, never the upload.
    /// </summary>
    private static async Task<IResult> Upload(HttpContext http, string projectKey, IFormFile? file,
        WorkItemsDbContext db, ICurrentTenant tenant, ICurrentUser user, IBlobStorage storage, IPlanLimits planLimits,
        IOptions<AttachmentsOptions> options, IProjectAccess access, TimeProvider clock, CancellationToken ct)
    {
        var policy = options.Value;
        var projectId = http.ResolvedProjectId()!.Value;
        var error = Validate(file, policy, out var fileName, out var contentType);
        if (error is not null) return error;

        byte[] bytes;
        await using (var upload = file!.OpenReadStream())
        {
            using var buffer = new MemoryStream((int)file.Length);
            await upload.CopyToAsync(buffer, ct);
            bytes = buffer.ToArray();
        }
        if (AttachmentImages.IsImage(contentType!))
        {
            switch (AttachmentImages.Process(bytes, policy))
            {
                case AttachmentImages.Rejected rejected:
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = [rejected.Reason] });
                case AttachmentImages.Encoded encoded:
                    bytes = encoded.Bytes;
                    contentType = AttachmentImages.WebpContentType;
                    fileName = AttachmentImages.WebpFileName(fileName!);
                    break;
            }
        }

        var plan = await planLimits.CanStoreBytesAsync(tenant.OrganizationId!.Value, bytes.LongLength, ct);
        if (!plan.Allowed) return Results.Problem(plan.Reason, title: "Plan limit reached.", type: ProblemTypes.PlanLimit,
            statusCode: StatusCodes.Status402PaymentRequired,
            extensions: new Dictionary<string, object?> { ["limit"] = plan.Limit, ["upgradeUrl"] = plan.UpgradeUrl });
        var project = await access.FindProjectAsync(tenant.OrganizationId!.Value, projectKey, ct);
        if (project is null || project.Id != projectId || project.IsArchived) return Results.NotFound();

        var attachmentId = Guid.CreateVersion7();
        var attachment = new Attachment
        {
            Id = attachmentId,
            OrganizationId = tenant.OrganizationId.Value, ProjectId = projectId, FileName = fileName!, ContentType = contentType!,
            SizeBytes = bytes.LongLength, UploadedBy = user.UserId!, CreatedAt = clock.GetUtcNow(),
            ObjectKey = ObjectKey(tenant.OrganizationId.Value, projectId, attachmentId, fileName!)
        };
        // The row first: a store write that fails leaves a pending row the sweeper removes,
        // where the other order would leave an object no row knows about.
        db.Attachments.Add(attachment);
        await db.SaveChangesAsync(ct);
        try
        {
            using var content = new MemoryStream(bytes, writable: false);
            await storage.PutAsync(attachment.ObjectKey, content, attachment.ContentType, ct);
        }
        catch
        {
            db.Attachments.Remove(attachment);
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
        return Results.Ok(View(attachment));
    }

    /// <summary>
    /// Gives a pending upload its owner, and is where the attachment allowance is actually
    /// enforced. The check at upload is the fast refusal; this one is the
    /// guarantee, because <c>storage_bytes</c> counts committed attachments only and two
    /// uploads that each fit can still not both fit. The sum and the commit happen in one
    /// transaction under a per-organization advisory lock, so concurrent commits are
    /// serialised and the second one sees the first. An organization already over its
    /// allowance keeps reading, downloading and exporting everything it has; only new
    /// commitments stop, and deleting attachments makes room again.
    /// </summary>
    private static async Task<IResult> Commit(Guid attachmentId, CommitAttachmentRequest request, WorkItemsDbContext db,
        ICurrentUser user, IProjectAccess access, IWikiPageAccess pages, IBlobStorage storage, IOptions<AttachmentsOptions> options,
        IPlanLimits planLimits, TimeProvider clock, CancellationToken ct)
    {
        if ((request.ItemId is not null ? 1 : 0) + (request.CommentId is not null ? 1 : 0) + (request.PageId is not null ? 1 : 0) != 1) return OwnerProblem();
        var attachment = await db.Attachments.FirstOrDefaultAsync(x => x.Id == attachmentId, ct);
        if (attachment is null || attachment.Status != AttachmentStatus.Pending || attachment.UploadedBy != user.UserId) return Results.NotFound();
        if (await access.GetProjectRoleAsync(user.UserId!, attachment.ProjectId, ct) is not { } role || !role.Satisfies(ProjectRole.Member)) return Results.NotFound();
        var policy = options.Value;
        var blob = await storage.HeadAsync(attachment.ObjectKey, ct);
        if (blob is null || blob.ContentLength != attachment.SizeBytes || blob.ContentLength > policy.MaxBytes ||
            !string.Equals(blob.ContentType, attachment.ContentType, StringComparison.OrdinalIgnoreCase))
        {
            // A malicious/failed PUT is never allowed to leave a paid orphan behind.
            await storage.DeleteAsync(attachment.ObjectKey, ct);
            db.Attachments.Remove(attachment);
            await db.SaveChangesAsync(ct);
            return Results.Problem("The uploaded object did not match the approved file.", statusCode: StatusCodes.Status409Conflict, type: ProblemTypes.Conflict);
        }
        if (request.ItemId is { } itemId)
        {
            if (!await db.Items.AnyAsync(x => x.Id == itemId && x.ProjectId == attachment.ProjectId, ct)) return Results.NotFound();
            attachment.ItemId = itemId;
        }
        else if (request.CommentId is not null)
        {
            if (!await db.Comments.AnyAsync(x => x.Id == request.CommentId && db.Items.Any(i => i.Id == x.ItemId && i.ProjectId == attachment.ProjectId), ct)) return Results.NotFound();
            attachment.CommentId = request.CommentId;
        }
        else if (await pages.CanWriteAsync(request.PageId!.Value, attachment.ProjectId, user.UserId!, ct)) attachment.WikiPageId = request.PageId;
        else return Results.NotFound();

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // The lock, then the sum, then the commit - all three inside one transaction. Asking
        // first and writing after would be a race the database cannot see, which is exactly
        // how an organization ends up over an allowance nobody ever exceeded on their own.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtext({attachment.OrganizationId.ToString()}))", ct);
        var plan = await planLimits.CanStoreBytesAsync(attachment.OrganizationId, attachment.SizeBytes, ct);
        if (!plan.Allowed)
        {
            // The upload stays pending - the sweeper collects it - so freeing space and
            // committing again is the remedy, and nothing already stored was touched.
            return Results.Problem(plan.Reason, title: "Plan limit reached.", type: ProblemTypes.PlanLimit,
                statusCode: StatusCodes.Status402PaymentRequired,
                extensions: new Dictionary<string, object?> { ["limit"] = plan.Limit, ["upgradeUrl"] = plan.UpgradeUrl });
        }
        attachment.Status = AttachmentStatus.Committed;
        attachment.CommittedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(View(attachment));
    }

    private static async Task<IResult> Download(HttpContext http, Guid attachmentId, WorkItemsDbContext db, ICurrentUser user,
        IProjectAccess access, IWikiPageAccess pages, IUserDirectory directory, IBlobStorage storage, CancellationToken ct)
    {
        // A pending upload is visible to its uploader only: a comment has no id until it is
        // posted, so the image being pasted into it is still pending while it is previewed.
        var attachment = await db.Attachments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == attachmentId &&
            (x.Status == AttachmentStatus.Committed || x.UploadedBy == user.UserId), ct);
        if (attachment is null || await access.GetProjectRoleAsync(user.UserId!, attachment.ProjectId, ct) is not { } role || !role.Satisfies(ProjectRole.Guest)) return Results.NotFound();
        // A page's attachment is part of the page: a project member the page's permissions
        // hide it from must not read its images by id.
        if (attachment.WikiPageId is { } pageId && !await pages.CanReadAsync(pageId, attachment.ProjectId, user.UserId!, ct)) return Results.NotFound();
        // The same for a comment's, when the comment is the factory's and the caller may not see it.
        if (attachment.CommentId is { } commentId
            && await db.Comments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == commentId, ct) is { } comment
            && await FactoryVisibility.HiddenAsync(access, directory, db, user.UserId!, comment, ct)) return Results.NotFound();
        var blob = await storage.OpenReadAsync(attachment.ObjectKey, ct);
        if (blob is null) return Results.NotFound();

        // Images render in place; anything else downloads. nosniff (set globally) keeps a
        // browser from reading a "text/plain" upload as HTML.
        var disposition = new ContentDispositionHeaderValue(AttachmentImages.IsImage(attachment.ContentType) ? "inline" : "attachment")
            { FileNameStar = attachment.FileName };
        http.Response.Headers.ContentDisposition = disposition.ToString();
        http.Response.Headers.CacheControl = "private, max-age=3600";
        return Results.Stream(blob.Content, attachment.ContentType);
    }

    private static async Task<IResult> Delete(Guid attachmentId, WorkItemsDbContext db, ICurrentUser user,
        IProjectAccess access, CancellationToken ct)
    {
        var attachment = await db.Attachments.FirstOrDefaultAsync(x => x.Id == attachmentId, ct);
        if (attachment is null || await access.GetProjectRoleAsync(user.UserId!, attachment.ProjectId, ct) is not { } role || !role.Satisfies(ProjectRole.Member)) return Results.NotFound();
        if (attachment.UploadedBy != user.UserId && !role.Satisfies(ProjectRole.Admin)) return Results.Forbid();
        attachment.Deleted();
        db.Attachments.Remove(attachment);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> List(string itemKey, string? include, WorkItemsDbContext db, IProjectAccess access, ICurrentUser user,
        IUserDirectory directory, CancellationToken ct)
    {
        var item = await WorkItemEndpoints.FindVisible(db, access, user, itemKey, ct);
        if (item is null) return Results.NotFound();
        var includeComments = string.Equals(include, "comments", StringComparison.OrdinalIgnoreCase);
        var onItem = db.Comments.AsNoTracking().Where(x => x.ItemId == item.Id);
        var comments = includeComments
            ? onItem.WithoutFactory(db, await FactoryVisibility.HiddenAuthorsAsync(access, directory, user.UserId!, item.OrganizationId, onItem, ct))
            : onItem;
        // An attachment pasted into a comment belongs to the comment rather than the item.
        // The opt-in keeps the existing item's-files panel concise while runners can ask for
        // all visual context with `?include=comments`.
        var attachments = await db.Attachments.AsNoTracking()
            .Where(x => x.Status == AttachmentStatus.Committed &&
                (x.ItemId == item.Id || includeComments && x.CommentId != null &&
                    comments.Any(comment => comment.Id == x.CommentId && comment.DeletedAt == null)))
            .OrderBy(x => x.CreatedAt)
            .ToListAsync(ct);
        return Results.Ok(attachments.Select(View));
    }

    private static IResult? Validate(IFormFile? file, AttachmentsOptions policy, out string? fileName, out string? contentType)
    {
        fileName = file?.FileName.Trim(); contentType = file?.ContentType?.Trim().ToLowerInvariant();
        if (file is null) return Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = ["A file is required."] });
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > 255 || fileName.IndexOfAny(['/', '\\', '\0']) >= 0) errors["fileName"] = ["A plain filename of 1-255 characters is required."];
        if (string.IsNullOrWhiteSpace(contentType) || !policy.AllowedContentTypes.Contains(contentType, StringComparer.OrdinalIgnoreCase)) errors["contentType"] = ["This content type is not allowed."];
        if (file.Length <= 0 || file.Length > policy.MaxBytes) errors["sizeBytes"] = [$"File size must be between 1 and {policy.MaxBytes} bytes."];
        return errors.Count == 0 ? null : Results.ValidationProblem(errors);
    }

    private static IResult OwnerProblem() => Results.ValidationProblem(new Dictionary<string, string[]> { ["owner"] = ["Exactly one of itemId, commentId, or pageId is required."] });
    private static AttachmentView View(Attachment x) => new(x.Id, x.ProjectId, x.ItemId, x.CommentId, x.WikiPageId, x.FileName, x.ContentType, x.SizeBytes, x.UploadedBy, x.CreatedAt);
    private static string ObjectKey(Guid orgId, Guid projectId, Guid attachmentId, string fileName) =>
        $"org/{orgId}/project/{projectId}/attachments/{attachmentId}/{Uri.EscapeDataString(fileName)}";
}

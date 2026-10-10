using Aictiq.Modules.Notifications.Domain;
using Aictiq.Modules.Notifications.Templates;
using Aictiq.Modules.WorkItems.Contracts;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Aictiq.Modules.Notifications.Delivery;

/// <summary>Turns a saved in-app notification into an independently deliverable email.
/// The notification id is also the queue id, giving immediate mail the same replay
/// protection as the integration-event email path.</summary>
public sealed class NotificationEmailService(
    NotificationsDbContext db, IUserDirectory users, IOrganizationLookup organizations, INotificationPresence presence,
    IProjectWorkflowAccess workflows, IProjectAccess access,
    EmailTemplateRenderer renderer, NotificationUnsubscribeTokens unsubscribe,
    IOptions<NotificationEmailOptions> options, IOptions<EmailOptions> emailOptions, TimeProvider clock)
{
    public async Task QueueImmediateAsync(IReadOnlyList<Notification> notifications, CancellationToken ct,
        WorkItemTransitioned? transition = null)
    {
        if (!options.Value.EmailEnabled || notifications.Count == 0) return;
        var ids = notifications.Select(n => n.UserId).Distinct().ToArray();
        var profiles = await users.GetDeliveryProfilesAsync(ids, ct);
        var actor = transition is null ? null : (await users.GetAsync([transition.ActorId], ct)).GetValueOrDefault(transition.ActorId);
        var states = transition is null ? new Dictionary<Guid, string>() :
            await workflows.GetStateNamesAsync(transition.ProjectId, [transition.FromStateId, transition.ToStateId], ct);
        var modes = await NotificationModes.LoadAsync(db, ids, notifications.Select(n => n.OrganizationId).Distinct().ToArray(), ct);
        var organizationDetails = new Dictionary<Guid, OrganizationRef?>();
        foreach (var notification in notifications)
        {
            var isRun = ChatNotificationService.IsRunKind(notification.Kind);
            if (isRun && !await access.CanOperateFactoryAsync(notification.UserId, notification.OrganizationId, ct))
                continue;
            if (!profiles.TryGetValue(notification.UserId, out var recipient) || recipient.IsAgent ||
                await presence.IsActiveAsync(notification.UserId, TimeSpan.FromMinutes(options.Value.PresenceMinutes), ct))
                continue;
            if (modes.Email(notification.UserId, notification.OrganizationId, notification.Kind) != EmailNotificationMode.Immediate)
                continue;
            if (transition is not null && await access.GetProjectRoleAsync(notification.UserId, transition.ProjectId, ct) is null)
                continue;
            if (!organizationDetails.TryGetValue(notification.OrganizationId, out var organization))
            {
                organization = await organizations.FindByIdAsync(notification.OrganizationId, ct);
                organizationDetails[notification.OrganizationId] = organization;
            }
            var variables = Variables(notification, recipient, unsubscribe.Create(notification.UserId, notification.Kind),
                emailOptions.Value.BaseUrl, organization?.Slug);
            var template = "notification";
            // Run notifications reach only Factory operators and open the run itself.
            if (notification.RunId is { } runId && isRun && organization is not null
                && !string.IsNullOrWhiteSpace(emailOptions.Value.BaseUrl))
                variables["notificationUrl"] = $"{emailOptions.Value.BaseUrl.TrimEnd('/')}/o/{Uri.EscapeDataString(organization.Slug)}/{ChatNotificationService.RunPath(runId)}";
            if (transition?.ItemTitle is not null)
            {
                template = "transition";
                variables["organizationName"] = organization?.Name ?? "";
                variables["actorName"] = actor?.DisplayName ?? "Someone";
                variables["itemTitle"] = transition.ItemTitle;
                variables["excerpt"] = transition.ItemExcerpt ?? "";
                variables["itemType"] = transition.ItemType ?? "";
                variables["itemPriority"] = transition.ItemPriority ?? "";
                variables["fromState"] = states.GetValueOrDefault(transition.FromStateId) ?? "Previous state";
                variables["toState"] = states.GetValueOrDefault(transition.ToStateId) ?? "New state";
                if (transition.Run is { } run)
                {
                    variables["runId"] = run.Id.ToString();
                    variables["runOutcome"] = run.Outcome.Replace('_', ' ');
                    variables["runSummary"] = Excerpt(run.Summary);
                    variables["pullRequestUrl"] = SafeWebUrl(run.PullRequestUrl);
                    if (organization is not null && !string.IsNullOrWhiteSpace(emailOptions.Value.BaseUrl)
                        && await access.CanOperateFactoryAsync(notification.UserId, notification.OrganizationId, ct))
                        variables["runUrl"] = $"{emailOptions.Value.BaseUrl.TrimEnd('/')}/o/{Uri.EscapeDataString(organization.Slug)}/{ChatNotificationService.RunPath(run.Id)}";
                }
            }
            var rendered = renderer.Render(template, variables);
            db.EmailOutbox.Add(new EmailOutboxMessage
            {
                Id = notification.Id, ToAddress = recipient.Email, Template = template,
                Subject = rendered.Subject, BodyHtml = rendered.Html, BodyText = rendered.Text,
                SendAfter = clock.GetUtcNow(), CreatedAt = clock.GetUtcNow()
            });
        }
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { db.ChangeTracker.Clear(); } // notification replay already queued it
    }

    private static string Excerpt(string? text) =>
        text is null ? "" : text.Length <= 600 ? text : text[..600].TrimEnd() + "…";

    private static string SafeWebUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" ? uri.AbsoluteUri : "";

    /// <summary>
    /// Mail for someone a comment was addressed to: tagged in it, or answered in their own
    /// thread. It says who wrote what on which item and links straight to the comment.
    /// Unlike the general path it is not held back while the recipient is online - a person
    /// who was asked something by name should find it in their mail regardless - but their
    /// own "email me about this" choice still decides.
    /// </summary>
    public async Task QueueCommentAsync(IReadOnlyList<Notification> notifications, CommentEmail comment, CancellationToken ct)
    {
        if (!options.Value.EmailEnabled || notifications.Count == 0) return;
        var ids = notifications.Select(n => n.UserId).Append(comment.AuthorId).Distinct().ToArray();
        var profiles = await users.GetDeliveryProfilesAsync(ids, ct);
        var modes = await NotificationModes.LoadAsync(db, ids, [comment.OrganizationId], ct);
        var organization = await organizations.FindByIdAsync(comment.OrganizationId, ct);
        var actorName = profiles.TryGetValue(comment.AuthorId, out var author) ? author.DisplayName : "Someone";
        var itemUrl = CommentLink(organization?.Slug, comment, emailOptions.Value.BaseUrl);
        var now = clock.GetUtcNow();
        foreach (var notification in notifications)
        {
            if (!profiles.TryGetValue(notification.UserId, out var recipient) || recipient.IsAgent) continue;
            if (modes.Email(notification.UserId, notification.OrganizationId, notification.Kind) != EmailNotificationMode.Immediate)
                continue;
            var template = notification.Kind switch
            {
                NotificationKind.Replied => "comment-reply",
                NotificationKind.Reacted => "reaction",
                _ => "mention"
            };
            var rendered = renderer.Render(template, new Dictionary<string, string>
            {
                ["organizationName"] = organization?.Name ?? "",
                ["recipientName"] = recipient.DisplayName,
                ["actorName"] = actorName,
                ["emoji"] = comment.Emoji ?? "",
                ["itemKey"] = comment.ItemKey ?? "",
                ["itemTitle"] = comment.ItemTitle ?? "",
                ["excerpt"] = comment.Excerpt ?? "",
                ["itemUrl"] = itemUrl,
                ["unsubscribeUrl"] = unsubscribe.Create(notification.UserId, notification.Kind)
            });
            db.EmailOutbox.Add(new EmailOutboxMessage
            {
                Id = notification.Id, ToAddress = recipient.Email, Template = template,
                Subject = rendered.Subject, BodyHtml = rendered.Html, BodyText = rendered.Text,
                SendAfter = now, CreatedAt = now
            });
        }
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { db.ChangeTracker.Clear(); } // notification replay already queued it
    }

    /// <summary>The item page, scrolled to the comment. Falls back to the inbox when an older
    /// event did not carry enough to build it.</summary>
    internal static string CommentLink(string? organizationSlug, CommentEmail comment, string? emailBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(emailBaseUrl) || organizationSlug is null || comment.ProjectKey is null || comment.ItemKey is null)
            return Link(null, emailBaseUrl);
        return $"{emailBaseUrl.TrimEnd('/')}/o/{Uri.EscapeDataString(organizationSlug)}/p/{Uri.EscapeDataString(comment.ProjectKey)}" +
            $"/items/{Uri.EscapeDataString(comment.ItemKey)}#comment-{comment.CommentId}";
    }

    internal static Dictionary<string, string> Variables(Notification notification, UserDeliveryProfile recipient, string unsubscribeUrl,
        string? emailBaseUrl, string? organizationSlug) =>
        new()
        {
            ["recipientName"] = recipient.DisplayName,
            ["message"] = notification.Message,
            ["itemKey"] = notification.ItemKey ?? "",
            ["notificationUrl"] = Link(notification.ItemKey, emailBaseUrl, organizationSlug),
            ["unsubscribeUrl"] = unsubscribeUrl
        };

    /// <summary>Open the item's project board with its detail dialog. Notifications without
    /// item context (including older events) still have a working inbox destination.</summary>
    internal static string Link(string? itemKey, string? emailBaseUrl, string? organizationSlug = null)
    {
        if (string.IsNullOrWhiteSpace(emailBaseUrl)) return "/inbox";
        var baseUrl = emailBaseUrl.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(organizationSlug) || string.IsNullOrWhiteSpace(itemKey))
            return $"{baseUrl}/inbox";
        var separator = itemKey.LastIndexOf('-');
        if (separator < 1 || separator == itemKey.Length - 1)
            return $"{baseUrl}/inbox";
        var projectKey = itemKey[..separator];
        return $"{baseUrl}/o/{Uri.EscapeDataString(organizationSlug)}/p/{Uri.EscapeDataString(projectKey)}" +
            $"/board?item={Uri.EscapeDataString(itemKey)}";
    }
}

/// <summary>What a comment email says, gathered from the comment's event.</summary>
public sealed record CommentEmail(
    Guid OrganizationId, Guid CommentId, string AuthorId,
    string? ProjectKey, string? ItemKey, string? ItemTitle, string? Excerpt, string? Emoji = null);

using Aictiq.Modules.Integrations.Contracts;
using Aictiq.Modules.Integrations.Domain;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Events;
using Aictiq.SharedKernel.Outbox;
using Aictiq.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Aictiq.Modules.Integrations.Workers;

/// <summary>
/// Turns a durable push inbox row into one durable event per commit/reference pair.
/// </summary>
public sealed class GitHubDeliveryReceivedHandler(
    IntegrationsDbContext db, IProjectAccess projects, ICurrentTenant currentTenant)
    : IDomainEventHandler<GitHubDeliveryReceived>
{
    public async Task HandleAsync(GitHubDeliveryReceived domainEvent, CancellationToken cancellationToken)
    {
        var delivery = await db.GitHubDeliveries.IgnoreQueryFilters().AsNoTracking()
            .SingleOrDefaultAsync(x => x.DeliveryId == domainEvent.DeliveryId, cancellationToken);
        if (delivery?.OrganizationId is not { } organizationId) return;

        using var tenant = currentTenant is AmbientCurrentTenant ambient ? ambient.Use(organizationId) : null;
        using var payload = JsonDocument.Parse(delivery.Payload);
        var root = payload.RootElement;
        if (string.Equals(delivery.EventType, "pull_request", StringComparison.OrdinalIgnoreCase))
        {
            await HandlePullRequestAsync(delivery.DeliveryId, organizationId, root, cancellationToken);
            return;
        }
        if (!string.Equals(delivery.EventType, "push", StringComparison.OrdinalIgnoreCase)) return;
        if (!TryRepositoryId(root, out var repoId) || !root.TryGetProperty("commits", out var commits) || commits.ValueKind != JsonValueKind.Array) return;

        var bindings = await db.RepoBindings.AsNoTracking().Where(x => x.RepoId == repoId).ToListAsync(cancellationToken);
        if (bindings.Count == 0) return;
        var branch = root.TryGetProperty("ref", out var refNode) ? Branch(refNode.GetString()) : "";
        var repositoryUrl = root.TryGetProperty("repository", out var repository) && repository.TryGetProperty("html_url", out var htmlUrl)
            ? htmlUrl.GetString() : null;
        var emitted = new HashSet<Guid>();

        foreach (var commit in commits.EnumerateArray())
        {
            var sha = Text(commit, "id");
            var message = Text(commit, "message");
            if (string.IsNullOrWhiteSpace(sha) || string.IsNullOrWhiteSpace(message)) continue;
            var author = commit.TryGetProperty("author", out var authorNode) ? authorNode : default;
            var authorName = Text(author, "name") ?? "GitHub";
            var authorEmail = Text(author, "email");
            var authorLogin = Text(author, "username") ?? (commit.TryGetProperty("committer", out var committer) ? Text(committer, "username") : null);
            var url = Text(commit, "url") ?? (repositoryUrl is null ? "" : $"{repositoryUrl}/commit/{sha}");
            if (string.IsNullOrWhiteSpace(url)) continue;
            var parsed = ReferenceParser.Parse(message, bindings.Select(x => x.ProjectId).Distinct().Count() == 1);
            foreach (var reference in parsed)
            {
                Guid? projectId = null;
                if (reference.ProjectKey is null) projectId = bindings[0].ProjectId;
                else
                {
                    var project = await projects.FindProjectAsync(organizationId, reference.ProjectKey, cancellationToken);
                    if (project is not null && bindings.Any(x => x.ProjectId == project.Id)) projectId = project.Id;
                }
                if (projectId is null) continue;
                var eventId = EventId(sha, projectId.Value, reference.ItemNumber);
                if (!emitted.Add(eventId)) continue;
                var @event = new CommitReferencedItem(organizationId, projectId.Value, reference.ItemNumber, sha,
                    FirstLine(message), authorName, authorLogin, authorEmail, url, branch) { EventId = eventId };
                db.Set<OutboxMessage>().Add(OutboxMessage.From(@event));
            }
        }
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Replaying the inbox event collides with its deterministic outbox ids. All
            // commit/reference pairs were already committed together, so that is success.
        }
    }

    private async Task HandlePullRequestAsync(string deliveryId, Guid organizationId, JsonElement root, CancellationToken cancellationToken)
    {
        var action = Text(root, "action");
        if (action is not ("opened" or "edited" or "synchronize" or "ready_for_review" or "closed" or "reopened")) return;
        if (!TryRepositoryId(root, out var repoId) || !root.TryGetProperty("pull_request", out var pullRequest)) return;
        var bindings = await db.RepoBindings.AsNoTracking().Where(x => x.RepoId == repoId).ToListAsync(cancellationToken);
        if (bindings.Count == 0) return;

        var id = Number(pullRequest, "id");
        var numberValue = Number(root, "number");
        var url = Text(pullRequest, "html_url");
        if (id <= 0 || numberValue is <= 0 or > int.MaxValue || string.IsNullOrWhiteSpace(url)) return;
        var number = (int)numberValue;
        var title = Text(pullRequest, "title") ?? $"Pull request #{number}";
        var body = Text(pullRequest, "body") ?? "";
        var headBranch = pullRequest.TryGetProperty("head", out var head) ? Text(head, "ref") ?? "" : "";
        var merged = pullRequest.TryGetProperty("merged", out var mergedNode) && mergedNode.ValueKind == JsonValueKind.True;
        var draft = pullRequest.TryGetProperty("draft", out var draftNode) && draftNode.ValueKind == JsonValueKind.True;
        var state = merged ? "merged" : string.Equals(action, "closed", StringComparison.Ordinal) ? "closed" : draft ? "draft" : "open";
        var author = pullRequest.TryGetProperty("user", out var user) ? Text(user, "login") : null;
        var reviewers = pullRequest.TryGetProperty("requested_reviewers", out var reviewerNodes) && reviewerNodes.ValueKind == JsonValueKind.Array
            ? reviewerNodes.EnumerateArray().Select(x => Text(x, "login")).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().ToArray()
            : [];
        var allowLocal = bindings.Select(x => x.ProjectId).Distinct().Count() == 1;
        var references = ReferenceParser.Parse($"{title}\n{body}", allowLocal)
            .Concat(ReferenceParser.ParseBranch(headBranch))
            .DistinctBy(x => $"{x.ProjectKey ?? "#"}-{x.ItemNumber}", StringComparer.OrdinalIgnoreCase);
        var closingText = $"{title}\n{body}";
        var emitted = new HashSet<Guid>();

        foreach (var reference in references)
        {
            Guid? projectId = null;
            if (reference.ProjectKey is null) projectId = bindings[0].ProjectId;
            else
            {
                var project = await projects.FindProjectAsync(organizationId, reference.ProjectKey, cancellationToken);
                if (project is not null && bindings.Any(x => x.ProjectId == project.Id)) projectId = project.Id;
            }
            if (projectId is null) continue;
            var binding = bindings.First(x => x.ProjectId == projectId.Value);
            var eventId = EventId(deliveryId, projectId.Value, reference.ItemNumber);
            if (!emitted.Add(eventId)) continue;
            var @event = new PullRequestReferencedItem(organizationId, projectId.Value, reference.ItemNumber, id, number, title, url,
                state, author, reviewers, headBranch, string.Equals(action, "opened", StringComparison.Ordinal), merged,
                HasClosingKeyword(closingText, reference), binding.OnPullRequestOpenedStateId, binding.OnPullRequestMergedStateId)
            { EventId = eventId };
            db.Set<OutboxMessage>().Add(OutboxMessage.From(@event));
        }
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // A GitHub delivery is immutable. A repeated inbox delivery therefore has
            // already staged every work-item event it could produce.
        }
    }

    private static bool TryRepositoryId(JsonElement root, out long id)
    {
        id = 0;
        return root.TryGetProperty("repository", out var repository) && repository.TryGetProperty("id", out var node) && node.TryGetInt64(out id);
    }
    private static string? Text(JsonElement element, string property) => element.ValueKind != JsonValueKind.Undefined && element.TryGetProperty(property, out var value) ? value.GetString() : null;
    private static long Number(JsonElement element, string property) => element.TryGetProperty(property, out var value) && value.TryGetInt64(out var number) ? number : 0;
    private static string FirstLine(string message) => message.Split(['\r', '\n'], StringSplitOptions.None)[0].Trim();
    private static string Branch(string? value) => value?.StartsWith("refs/heads/", StringComparison.Ordinal) == true ? value[11..] : value ?? "";
    private static Guid EventId(string sha, Guid projectId, int itemNumber)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{sha}\0{projectId:N}\0{itemNumber}"));
        return new Guid(bytes[..16]);
    }
    private static bool HasClosingKeyword(string text, ReferenceParser.Reference reference)
    {
        var item = reference.ProjectKey is null ? $"#{reference.ItemNumber}" : $"{reference.ProjectKey}-{reference.ItemNumber}";
        return Regex.IsMatch(text, $@"(?i)\b(?:fix(?:es|ed)?|close(?:s|d)?|resolve(?:s|d)?)\s+{Regex.Escape(item)}\b");
    }
}

using System.Globalization;
using System.Text;
using System.Text.Json;
using Aictiq.Modules.Tenancy;
using Aictiq.Modules.WorkItems.Access;
using Aictiq.Modules.WorkItems.Domain;
using Aictiq.Modules.WorkItems.Endpoints;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Aictiq.Modules.WorkItems.Jira;

/// <summary>
/// Runs one queued Jira import. Issues are read oldest first and each one is its own unit of
/// work: an issue that fails is reported and the rest carry on, and an issue whose key is already
/// an item in the project is skipped, so a retry after a partial run creates nothing twice.
/// Existing items are never updated.
///
/// Imported rows are written directly rather than through the comment and item endpoints, the
/// way <see cref="Workers.CsvImportWorker"/> does: a migration of hundreds of issues must not mail
/// every watcher, mention and channel as if each comment had just been posted.
/// </summary>
public sealed class JiraImporter(WorkItemsDbContext db, TenancyDbContext tenancy, JiraClient jira, JiraTokenProtector tokens,
    IUserDirectory directory, IProjectAccess access, IBlobStorage storage, IPlanLimits planLimits,
    IOptions<AttachmentsOptions> attachmentOptions, TimeProvider clock)
{
    public const int MaxFailures = 500;
    private AttachmentsOptions Policy => attachmentOptions.Value;

    /// <summary>The fields one search page asks for; custom fields are added per site.</summary>
    internal static IReadOnlyList<string> Fields(JiraFieldIds ids) =>
        [ "summary", "description", "issuetype", "status", "assignee", "reporter", "labels", "parent", "created", "resolutiondate",
          "timeoriginalestimate", "attachment", .. new[] { ids.StoryPoints, ids.Sprint, ids.EpicLink }.OfType<string>() ];

    internal static string Jql(string projectKey) => $"project = \"{projectKey.Replace("\\", "\\\\").Replace("\"", "\\\"")}\" ORDER BY created ASC, key ASC";

    public async Task RunAsync(JiraImportJob job, CancellationToken ct)
    {
        try
        {
            if (job.ProtectedToken is null) throw new JiraException("The Jira API token is no longer available. Start the import again.");
            var credentials = new JiraCredentials(new Uri(job.SiteUrl), job.Email, tokens.Unprotect(job.ProtectedToken));
            var run = await PrepareAsync(job, credentials, ct);
            string? next = null; var deferred = new List<JsonElement>();
            do
            {
                var page = await jira.SearchAsync(credentials, Jql(job.JiraProjectKey), Fields(run.FieldIds), next, ct);
                foreach (var issue in page.Issues)
                {
                    if (!await ImportIssueAsync(run, issue, final: false, ct)) { deferred.Add(issue); continue; }
                    await ProgressAsync(job, ct);
                }
                next = page.NextPageToken;
            } while (next is not null);
            // Tasks and Features cannot exist without a parent, so one read before its parent waits for it.
            foreach (var issue in deferred)
            {
                await ImportIssueAsync(run, issue, final: true, ct);
                await ProgressAsync(job, ct);
            }
            await LinkParentsAsync(run, ct);
            job.TotalIssues = job.ProcessedIssues;
            Finish(job, JiraImportStatus.Completed);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            db.ChangeTracker.Clear();
            var stored = await db.JiraImportJobs.SingleAsync(x => x.Id == job.Id, CancellationToken.None);
            AddFailure(stored, null, "job", exception is JiraException ? exception.Message : "The import stopped unexpectedly. Issues already imported are kept; run it again to continue.");
            Finish(stored, JiraImportStatus.Failed);
            await db.SaveChangesAsync(CancellationToken.None);
            if (exception is not JiraException) throw;
        }
    }

    private async Task ProgressAsync(JiraImportJob job, CancellationToken ct)
    {
        job.ProcessedIssues++; job.HeartbeatAt = clock.GetUtcNow();
        if (job.ProcessedIssues > job.TotalIssues) job.TotalIssues = job.ProcessedIssues;
        await db.SaveChangesAsync(ct);
    }

    private static void Finish(JiraImportJob job, JiraImportStatus status)
    {
        job.Status = status; job.CompletedAt = DateTimeOffset.UtcNow; job.ProtectedToken = null;
    }

    internal static void AddFailure(JiraImportJob job, string? issueKey, string kind, string reason)
    {
        if (job.Failures.Count >= MaxFailures) return;
        job.Failures = [.. job.Failures, new JiraImportFailure(issueKey, kind, reason.Length > 500 ? reason[..500] : reason)];
    }

    private sealed class Run
    {
        public required JiraImportJob Job { get; init; }
        public required JiraCredentials Credentials { get; init; }
        public required JiraFieldIds FieldIds { get; init; }
        public required List<WorkflowState> States { get; init; }
        public required WorkflowState Initial { get; init; }
        public required HashSet<string> MemberIds { get; init; }
        public required Guid? TeamId { get; init; }
        public required Dictionary<string, Label> Labels { get; init; }
        public Dictionary<string, string?> UserByAccount { get; } = new(StringComparer.Ordinal);
        /// <summary>Jira sprint id to the Aictiq sprint, or null for one that could not be created.</summary>
        public Dictionary<string, Guid?> Sprints { get; } = new(StringComparer.Ordinal);
        public List<(Guid ItemId, string IssueKey, string ParentKey)> Parents { get; } = [];
    }

    private async Task<Run> PrepareAsync(JiraImportJob job, JiraCredentials credentials, CancellationToken ct)
    {
        await WorkflowEndpoints.EnsureDefaultAsync(db, job.OrganizationId, job.ProjectId, ct);
        var states = await db.WorkflowStates.Where(s => db.Workflows.Any(w => w.Id == s.WorkflowId && w.ProjectId == job.ProjectId && w.IsDefault)).ToListAsync(ct);
        var fieldIds = await jira.FieldIdsAsync(credentials, ct);
        // Everyone who can see the project, as the CSV import has it: an address outside that set
        // stays unassigned, and an import is not a way to find out who has an account here.
        var members = (await access.ListProjectMemberIdsAsync(job.ProjectId, ct)).ToHashSet(StringComparer.Ordinal);
        var team = await tenancy.Teams.AsNoTracking().Where(t => t.ProjectId == job.ProjectId && t.IsDefault).Select(t => (Guid?)t.Id).FirstOrDefaultAsync(ct);
        var labels = await db.Labels.Where(l => l.ProjectId == job.ProjectId).ToDictionaryAsync(l => l.Name, StringComparer.OrdinalIgnoreCase, ct);
        return new Run { Job = job, Credentials = credentials, FieldIds = fieldIds, States = states, Initial = states.Single(s => s.IsInitial), MemberIds = members, TeamId = team, Labels = labels };
    }

    /// <returns>False when the issue must wait for the final pass, because its parent is not here yet.</returns>
    private async Task<bool> ImportIssueAsync(Run run, JsonElement issue, bool final, CancellationToken ct)
    {
        var job = run.Job;
        var key = JiraClient.Text(issue, "key") ?? "";
        var fields = issue.TryGetProperty("fields", out var f) ? f : default;
        if (key.Length == 0 || fields.ValueKind != JsonValueKind.Object) { AddFailure(job, key.Length == 0 ? null : key, "issue", "Jira returned this issue without its fields."); return true; }
        if (await db.Items.AnyAsync(x => x.ProjectId == job.ProjectId && x.ExternalRef == key, ct)) { job.SkippedIssues++; return true; }

        WorkItem? item;
        try { item = await CreateItemAsync(run, key, fields, final, ct); }
        catch (Exception exception) when (exception is DbUpdateException or JiraException or ArgumentException or InvalidOperationException)
        {
            Reset(run);
            AddFailure(job, key, "issue", exception is JiraException ? exception.Message : $"It could not be saved: {Root(exception).Message}");
            return true;
        }
        if (item is null) return false;

        try { await ImportCommentsAsync(run, item, key, ct); }
        catch (Exception exception) when (exception is DbUpdateException or JiraException)
        {
            Reset(run);
            AddFailure(job, key, "comment", exception is JiraException ? $"Comments were not imported: {exception.Message}" : "Comments could not be saved.");
        }

        if (fields.TryGetProperty("attachment", out var attachments) && attachments.ValueKind == JsonValueKind.Array && attachments.GetArrayLength() > 0)
            await ImportAttachmentsAsync(run, item.Id, key, attachments, ct);
        return true;
    }

    /// <summary>After a failed save the tracker holds the rows that failed; only the job is kept.</summary>
    private void Reset(Run run)
    {
        foreach (var entry in db.ChangeTracker.Entries().Where(e => !ReferenceEquals(e.Entity, run.Job)).ToList())
        {
            // A label first seen on the failed issue was never stored, so the next issue makes it again.
            if (entry is { State: EntityState.Added, Entity: Label label }) run.Labels.Remove(label.Name);
            entry.State = EntityState.Detached;
        }
    }

    private async Task<WorkItem?> CreateItemAsync(Run run, string key, JsonElement fields, bool final, CancellationToken ct)
    {
        var job = run.Job; var now = clock.GetUtcNow(); var notes = new List<string>();
        var title = (JiraClient.Text(fields, "summary") ?? "").Trim();
        if (title.Length == 0) title = key;
        if (title.Length > 500) { notes.Add($"The Jira summary was shortened; in full: {title}"); title = title[..497] + "..."; }

        var issueType = fields.TryGetProperty("issuetype", out var t) ? JiraClient.Text(t, "name") ?? "" : "";
        var type = job.IssueTypeMap.TryGetValue(issueType, out var mapped) ? mapped : SuggestType(issueType, t.ValueKind == JsonValueKind.Object && JiraClient.Bool(t, "subtask") == true);
        var status = fields.TryGetProperty("status", out var s) ? s : default;
        var statusName = JiraClient.Text(status, "name") ?? "";
        var state = job.StatusMap.TryGetValue(statusName, out var stateId) && run.States.FirstOrDefault(x => x.Id == stateId) is { } chosen
            ? chosen : SuggestState(run.States, statusName, status.ValueKind == JsonValueKind.Object && status.TryGetProperty("statusCategory", out var c) ? JiraClient.Text(c, "key") : null) ?? run.Initial;

        var parentKey = fields.TryGetProperty("parent", out var pr) ? JiraClient.Text(pr, "key") : null;
        parentKey ??= run.FieldIds.EpicLink is { } epicField && fields.TryGetProperty(epicField, out var epic) && epic.ValueKind == JsonValueKind.String ? epic.GetString() : null;
        parentKey = string.IsNullOrWhiteSpace(parentKey) ? null : parentKey.Trim();
        var parent = parentKey is null ? null : await db.Items.AsNoTracking().Where(x => x.ProjectId == job.ProjectId && x.ExternalRef == parentKey).Select(x => new { x.Id, x.Type, x.TeamId }).FirstOrDefaultAsync(ct);
        var fits = parent is not null && ItemHierarchy.Allows(parent.Type, type);
        if (ItemHierarchy.RequiresParent(type) && !fits)
        {
            if (!final) return null;
            // Its parent never arrived or cannot hold it: kept as a stand-alone Story rather than lost.
            AddFailure(job, key, "parent", parentKey is null
                ? $"A {type} needs a parent and this issue has none in Jira; it was imported as a Story."
                : parent is null ? $"Its parent {parentKey} is not in this import; it was imported as a Story without a parent."
                : $"A {type} cannot sit under {parentKey}, a {parent.Type}; it was imported as a Story without a parent.");
            type = WorkItemType.Story; parentKey = null; parent = null; fits = false;
        }

        var assignee = await MemberAsync(run, fields, "assignee", "Assignee", notes, ct);
        var reporter = await MemberAsync(run, fields, "reporter", "Reporter", notes, ct);

        var points = run.FieldIds.StoryPoints is { } pointsField && fields.TryGetProperty(pointsField, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetDecimal(out var pv) && pv >= 0 ? pv : (decimal?)null;
        var estimate = fields.TryGetProperty("timeoriginalestimate", out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var seconds) && seconds >= 0 ? Math.Round(seconds / 3600m, 2) : (decimal?)null;
        if (estimate > 9999.99m) { notes.Add($"Original estimate in Jira: {estimate}h"); estimate = null; }
        // The same type rules every item obeys: only Tasks and Bugs carry hours, Epics and Features no points.
        if (points is not null && type is WorkItemType.Epic or WorkItemType.Feature or WorkItemType.Task) { notes.Add($"Story points in Jira: {points.Value.ToString(CultureInfo.InvariantCulture)}"); points = null; }
        if (estimate is not null && type is not (WorkItemType.Task or WorkItemType.Bug)) { notes.Add($"Original estimate in Jira: {estimate.Value.ToString(CultureInfo.InvariantCulture)}h"); estimate = null; }

        var description = fields.TryGetProperty("description", out var d) ? AdfMarkdown.Convert(d) : "";
        var markdown = WithNotes(description, Footer(run, key, notes));
        // A Task follows its parent's team, as one created by hand does.
        var teamId = type is WorkItemType.Story or WorkItemType.Bug or WorkItemType.Task ? (type == WorkItemType.Task && fits ? parent!.TeamId ?? run.TeamId : run.TeamId) : null;
        var created = JiraClient.Date(fields, "created") ?? now;
        var resolved = JiraClient.Date(fields, "resolutiondate");

        // The sprint is saved on its own, before the item, so a sprint the team refuses never takes the item with it.
        Guid? sprintId = null;
        if (teamId is not null && run.FieldIds.Sprint is { } sprintField && fields.TryGetProperty(sprintField, out var sprints) && sprints.ValueKind == JsonValueKind.Array && Latest(sprints) is { } sprint)
        {
            sprintId = await SprintAsync(run, sprint, ct);
            if (sprintId is null) AddFailure(job, key, "sprint", $"Not placed in sprint \"{JiraClient.Text(sprint, "name")}\", which could not be imported.");
        }

        var item = new WorkItem
        {
            OrganizationId = job.OrganizationId, ProjectId = job.ProjectId, ProjectKey = job.ProjectKey, Number = await NextNumberAsync(job, ct),
            Type = type, Title = title, DescriptionMarkdown = markdown, DescriptionHtml = WorkItemEndpoints.Render(markdown), StateId = state.Id,
            Priority = WorkItemPriority.None, AssigneeId = assignee, TeamId = teamId, Rank = await ItemRanking.AfterLastAsync(db, job.ProjectId, ct),
            Points = points, EstimateHours = estimate, RemainingHours = estimate is not null && state.Category != WorkflowStateCategory.Completed ? estimate : null,
            ParentId = fits ? parent!.Id : null, ExternalRef = key, CreatedBy = reporter ?? job.RequestedBy, CreatedAt = created, UpdatedAt = now,
            ResolvedAt = state.Category == WorkflowStateCategory.Resolved ? resolved ?? now : null,
            CompletedAt = state.Category == WorkflowStateCategory.Completed ? resolved ?? now : null,
            RemovedAt = state.Category == WorkflowStateCategory.Removed ? resolved ?? now : null,
        };
        db.Items.Add(item);

        if (fields.TryGetProperty("labels", out var labels) && labels.ValueKind == JsonValueKind.Array)
            foreach (var name in labels.EnumerateArray().Select(x => x.GetString()?.Trim()).OfType<string>().Where(x => x.Length is > 0 and <= 50).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!run.Labels.TryGetValue(name, out var label)) { label = new Label { OrganizationId = job.OrganizationId, ProjectId = job.ProjectId, Name = name }; db.Labels.Add(label); run.Labels[name] = label; }
                db.ItemLabels.Add(new ItemLabel { OrganizationId = job.OrganizationId, ItemId = item.Id, LabelId = label.Id, AddedAt = now, AddedBy = job.RequestedBy });
            }

        if (sprintId is not null)
        {
            item.SprintId = sprintId;
            await SprintEndpoints.LogScopeAsync(db, item, null, sprintId, created, ct);
        }

        await db.SaveChangesAsync(ct);
        job.CreatedItems++;
        if (parentKey is not null && !fits)
        {
            if (parent is null) run.Parents.Add((item.Id, key, parentKey));
            else AddFailure(job, key, "parent", $"A {type} cannot sit under {parentKey}, a {parent.Type}; it was left without a parent.");
        }
        return item;
    }

    private static string Footer(Run run, string key, IReadOnlyList<string> notes)
    {
        var footer = new StringBuilder($"_Imported from Jira [{key}]({new Uri(run.Credentials.Site, $"browse/{Uri.EscapeDataString(key)}")})._");
        foreach (var note in notes) footer.Append("  \n").Append(note);
        return footer.ToString();
    }

    private static string WithNotes(string description, string footer) => description.Length == 0 ? footer : $"{description}\n\n---\n\n{footer}";

    /// <summary>
    /// The member a Jira user is, matched by email among the project's members. Anyone else is
    /// left out, with their name kept in the item's notes.
    /// </summary>
    private async Task<string?> MemberAsync(Run run, JsonElement fields, string field, string role, List<string> notes, CancellationToken ct)
    {
        if (!fields.TryGetProperty(field, out var value) || JiraClient.User(value) is not { } user) return null;
        var member = await MatchAsync(run, user, ct);
        if (member is null) notes.Add($"{role} in Jira: {Escape(user.DisplayName)} (not a member of this project)");
        return member;
    }

    private async Task<string?> MatchAsync(Run run, JiraUser user, CancellationToken ct)
    {
        if (run.UserByAccount.TryGetValue(user.AccountId, out var cached)) return cached;
        string? id = null;
        if (!string.IsNullOrWhiteSpace(user.Email) && run.MemberIds.Count > 0 && await directory.FindByEmailAsync(user.Email.Trim(), ct) is { } found && run.MemberIds.Contains(found.Id)) id = found.Id;
        run.UserByAccount[user.AccountId] = id;
        return id;
    }

    /// <summary>An issue can have been in several sprints; it is placed in its open one, else its last.</summary>
    private static JsonElement? Latest(JsonElement sprints)
    {
        var all = sprints.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object && JiraClient.Text(x, "id") is not null).ToList();
        if (all.Count == 0) return null;
        return all.FirstOrDefault(x => JiraClient.Text(x, "state") == "active") is { ValueKind: JsonValueKind.Object } active ? active
            : all.FirstOrDefault(x => JiraClient.Text(x, "state") == "future") is { ValueKind: JsonValueKind.Object } future ? future
            : all.OrderBy(x => JiraClient.Date(x, "completeDate") ?? JiraClient.Date(x, "endDate") ?? DateTimeOffset.MinValue).Last();
    }

    /// <summary>
    /// The Aictiq sprint for a Jira sprint, created the first time it is seen, keeping its name,
    /// dates and state. A sprint the team's rules refuse - two whose dates overlap, or a second
    /// active one - is reported once and its issues stay in the backlog.
    /// </summary>
    private async Task<Guid?> SprintAsync(Run run, JsonElement source, CancellationToken ct)
    {
        var jiraId = JiraClient.Text(source, "id")!;
        if (run.Sprints.TryGetValue(jiraId, out var known)) return known;
        var job = run.Job; var teamId = run.TeamId!.Value;
        var externalRef = $"jira:{run.Credentials.Site.Host}:{jiraId}";
        if (await db.Sprints.Where(x => x.TeamId == teamId && x.ExternalRef == externalRef).Select(x => (Guid?)x.Id).FirstOrDefaultAsync(ct) is { } existing)
            return run.Sprints[jiraId] = existing;

        var name = (JiraClient.Text(source, "name") ?? $"Sprint {jiraId}").Trim();
        if (name.Length > 100) name = name[..100];
        var state = JiraClient.Text(source, "state") switch { "active" => SprintState.Active, "closed" => SprintState.Completed, _ => SprintState.Planned };
        var start = JiraClient.Date(source, "startDate"); var end = JiraClient.Date(source, "endDate"); var complete = JiraClient.Date(source, "completeDate");
        var startsOn = DateOnly.FromDateTime((start ?? clock.GetUtcNow()).UtcDateTime);
        var endsOn = end is { } e ? DateOnly.FromDateTime(e.UtcDateTime) : startsOn.AddDays(14);
        if (endsOn <= startsOn) endsOn = startsOn.AddDays(1);
        var sprint = new Sprint
        {
            OrganizationId = job.OrganizationId, TeamId = teamId, Name = name, Goal = (JiraClient.Text(source, "goal") ?? "").Trim() is var goal && goal.Length > 2000 ? goal[..2000] : goal,
            StartsOn = startsOn, EndsOn = endsOn, State = state, ExternalRef = externalRef, CreatedAt = clock.GetUtcNow(),
            StartedAt = state is SprintState.Active or SprintState.Completed ? start ?? clock.GetUtcNow() : null,
            CompletedAt = state == SprintState.Completed ? complete ?? end ?? clock.GetUtcNow() : null,
        };
        sprint.Changed(job.ProjectId, job.RequestedBy);
        db.Sprints.Add(sprint);
        try
        {
            await db.SaveChangesAsync(ct);
            job.CreatedSprints++;
            return run.Sprints[jiraId] = sprint.Id;
        }
        catch (DbUpdateException exception) when (Root(exception) is PostgresException { SqlState: PostgresErrorCodes.ExclusionViolation or PostgresErrorCodes.UniqueViolation } postgres)
        {
            db.Entry(sprint).State = EntityState.Detached;
            AddFailure(job, null, "sprint", postgres.SqlState == PostgresErrorCodes.ExclusionViolation
                ? $"Sprint \"{name}\" ({startsOn:yyyy-MM-dd} to {endsOn:yyyy-MM-dd}) overlaps another sprint of the project's team."
                : $"Sprint \"{name}\" is active in Jira, but the project's team already has an active sprint.");
            return run.Sprints[jiraId] = null;
        }
    }

    /// <summary>Comments in order, keeping their dates; a non-member's are posted by the importer under their name.</summary>
    private async Task ImportCommentsAsync(Run run, WorkItem item, string key, CancellationToken ct)
    {
        var job = run.Job;
        var comments = await jira.CommentsAsync(run.Credentials, key, ct);
        if (comments.Count == 0) return;
        var added = 0;
        foreach (var comment in comments)
        {
            var body = AdfMarkdown.Convert(comment.Body);
            var author = comment.Author is { } user ? await MatchAsync(run, user, ct) : null;
            if (author is null)
                body = $"**{Escape(comment.Author?.DisplayName ?? "Unknown Jira user")}** wrote in Jira:\n\n{body}";
            if (body.Trim().Length == 0) continue;
            db.Comments.Add(new Comment
            {
                OrganizationId = job.OrganizationId, ItemId = item.Id, AuthorId = author ?? job.RequestedBy,
                BodyMarkdown = body, BodyHtml = WorkItemEndpoints.Render(body), CreatedAt = comment.Created,
            });
            added++;
        }
        await db.SaveChangesAsync(ct);
        job.CreatedComments += added;
    }

    /// <summary>
    /// Each attachment downloaded and stored as the item's own, under the same type, size and
    /// storage rules as an upload. What does not pass is skipped and reported; the item page
    /// shows the rest where its description links them.
    /// </summary>
    private async Task ImportAttachmentsAsync(Run run, Guid itemId, string key, JsonElement attachments, CancellationToken ct)
    {
        var job = run.Job; var stored = new List<ChatAttachmentCopies.Copied>();
        foreach (var source in attachments.EnumerateArray())
        {
            var fileName = SafeFileName(JiraClient.Text(source, "filename") ?? "attachment");
            try
            {
                if (JiraClient.Text(source, "content") is not { } content || !Uri.TryCreate(content, UriKind.Absolute, out var url)) { AddFailure(job, key, "attachment", $"{fileName}: Jira gave no download address."); continue; }
                var declared = source.TryGetProperty("size", out var size) && size.TryGetInt64(out var bytes) ? bytes : 0;
                if (declared > Policy.MaxBytes) { AddFailure(job, key, "attachment", $"{fileName}: {Megabytes(declared)} is over the {Megabytes(Policy.MaxBytes)} attachment limit."); continue; }
                var contentType = AttachmentDocuments.ResolveContentType(fileName, JiraClient.Text(source, "mimeType")?.Trim().ToLowerInvariant());
                if (string.IsNullOrWhiteSpace(contentType) || !Policy.AllowedContentTypes.Contains(contentType, StringComparer.OrdinalIgnoreCase)) { AddFailure(job, key, "attachment", $"{fileName}: this file type is not allowed as an attachment."); continue; }
                switch (await jira.DownloadAsync(run.Credentials, url, Policy.MaxBytes, ct))
                {
                    case JiraClient.TooLarge: AddFailure(job, key, "attachment", $"{fileName}: it is over the {Megabytes(Policy.MaxBytes)} attachment limit."); break;
                    case JiraClient.Downloaded downloaded when downloaded.Bytes.Length == 0: AddFailure(job, key, "attachment", $"{fileName}: the file is empty."); break;
                    case JiraClient.Downloaded downloaded:
                        var (attachment, failure) = await StoreAsync(job, itemId, fileName, contentType, downloaded.Bytes, ct);
                        if (attachment is not null) stored.Add(attachment); else AddFailure(job, key, "attachment", $"{fileName}: {failure}");
                        break;
                }
            }
            catch (JiraException exception) { AddFailure(job, key, "attachment", $"{fileName}: {exception.Message}"); }
        }
        if (stored.Count == 0) return;
        job.CreatedAttachments += stored.Count;
        var item = await db.Items.SingleAsync(x => x.Id == itemId, ct);
        var section = ChatAttachmentCopies.Section(job.OrganizationSlug, stored).Replace("**Files from project chat**", "**Attachments from Jira**", StringComparison.Ordinal);
        item.DescriptionMarkdown = $"{item.DescriptionMarkdown}\n\n{section}";
        item.DescriptionHtml = WorkItemEndpoints.Render(item.DescriptionMarkdown);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Stages the file as a pending attachment and commits it under the organization's storage
    /// lock, as <see cref="AttachmentEndpoints"/> does for an upload. Images are re-encoded the
    /// same way, so what is stored is never the raw download.
    /// </summary>
    private async Task<(ChatAttachmentCopies.Copied? Attachment, string? Failure)> StoreAsync(JiraImportJob job, Guid itemId, string fileName, string contentType, byte[] bytes, CancellationToken ct)
    {
        if (AttachmentImages.IsImage(contentType))
        {
            switch (AttachmentImages.Process(bytes, Policy))
            {
                case AttachmentImages.Rejected rejected: return (null, rejected.Reason);
                case AttachmentImages.Encoded encoded:
                    bytes = encoded.Bytes; contentType = AttachmentImages.WebpContentType; fileName = AttachmentImages.WebpFileName(fileName);
                    break;
            }
        }
        var id = Guid.CreateVersion7();
        var attachment = new Attachment
        {
            Id = id, OrganizationId = job.OrganizationId, ProjectId = job.ProjectId, FileName = fileName, ContentType = contentType,
            SizeBytes = bytes.LongLength, UploadedBy = job.RequestedBy, CreatedAt = clock.GetUtcNow(),
            ObjectKey = AttachmentEndpoints.ObjectKey(job.OrganizationId, job.ProjectId, id, fileName),
        };
        // The row first: a failed store write leaves a pending row the sweeper removes.
        db.Attachments.Add(attachment);
        await db.SaveChangesAsync(ct);
        try
        {
            using var content = new MemoryStream(bytes, writable: false);
            await storage.PutAsync(attachment.ObjectKey, content, contentType, ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await DiscardAsync(attachment);
            return (null, "it could not be stored.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtext({job.OrganizationId.ToString()}))", ct);
        var plan = await planLimits.CanStoreBytesAsync(job.OrganizationId, attachment.SizeBytes, ct);
        if (!plan.Allowed)
        {
            await transaction.RollbackAsync(ct);
            await DiscardAsync(attachment);
            return (null, plan.Reason ?? "the organization's storage allowance is used up.");
        }
        attachment.ItemId = itemId; attachment.Status = AttachmentStatus.Committed; attachment.CommittedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return (new ChatAttachmentCopies.Copied(attachment.Id, attachment.Id, attachment.FileName, attachment.ContentType, attachment.SizeBytes), null);
    }

    private async Task DiscardAsync(Attachment attachment)
    {
        await storage.DeleteAsync(attachment.ObjectKey, CancellationToken.None);
        db.Attachments.Remove(attachment);
        await db.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>Parent and epic links, once every issue exists - including those from an earlier run.</summary>
    private async Task LinkParentsAsync(Run run, CancellationToken ct)
    {
        if (run.Parents.Count == 0) return;
        var job = run.Job;
        var keys = run.Parents.Select(x => x.ParentKey).Distinct().ToArray();
        var parents = await db.Items.Where(x => x.ProjectId == job.ProjectId && x.ExternalRef != null && keys.Contains(x.ExternalRef)).ToDictionaryAsync(x => x.ExternalRef!, ct);
        foreach (var (itemId, issueKey, parentKey) in run.Parents)
        {
            if (!parents.TryGetValue(parentKey, out var parent)) { AddFailure(job, issueKey, "parent", $"Its parent {parentKey} is not in this import."); continue; }
            var item = await db.Items.SingleAsync(x => x.Id == itemId, ct);
            if (!ItemHierarchy.Allows(parent.Type, item.Type)) { AddFailure(job, issueKey, "parent", $"A {item.Type} cannot sit under {parentKey}, a {parent.Type}; it was left without a parent."); continue; }
            item.ParentId = parent.Id;
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException)
            {
                db.Entry(item).State = EntityState.Detached;
                AddFailure(job, issueKey, "parent", $"It could not be linked to its parent {parentKey}.");
            }
        }
    }

    /// <summary>The Aictiq type a Jira issue type most likely is; the user can change it in the preview.</summary>
    public static WorkItemType SuggestType(string name, bool subtask) => subtask ? WorkItemType.Task : name.Trim().ToLowerInvariant() switch
    {
        "epic" or "initiative" => WorkItemType.Epic,
        "feature" => WorkItemType.Feature,
        "story" or "user story" => WorkItemType.Story,
        "bug" or "defect" or "incident" or "problem" => WorkItemType.Bug,
        "sub-task" or "subtask" or "task" => WorkItemType.Task,
        // A Task needs a parent; a stand-alone Jira type is closer to a Story.
        _ => WorkItemType.Story,
    };

    /// <summary>The state of the same name, else the first of the matching category.</summary>
    public static WorkflowState? SuggestState(IReadOnlyList<WorkflowState> states, string name, string? category) =>
        states.FirstOrDefault(s => s.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)) ?? (category switch
        {
            "new" => states.FirstOrDefault(s => s.IsInitial) ?? states.FirstOrDefault(s => s.Category == WorkflowStateCategory.Proposed),
            "done" => states.FirstOrDefault(s => s.Category == WorkflowStateCategory.Completed),
            "indeterminate" => states.OrderBy(s => s.Position).FirstOrDefault(s => s.Category == WorkflowStateCategory.Active),
            _ => null,
        });

    private async Task<int> NextNumberAsync(JiraImportJob job, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO work.project_sequences (project_id, organization_id, next_number) VALUES ({job.ProjectId}, {job.OrganizationId}, 1) ON CONFLICT (project_id) DO NOTHING", ct);
        return (await db.Database.SqlQuery<int>($"UPDATE work.project_sequences SET next_number = next_number + 1 WHERE project_id = {job.ProjectId} RETURNING next_number - 1 AS \"Value\"").ToListAsync(ct)).Single();
    }

    private static string SafeFileName(string name)
    {
        var clean = string.Concat(name.Trim().Select(c => c is '/' or '\\' or '\0' || char.IsControl(c) ? '_' : c));
        if (clean.Length == 0) clean = "attachment";
        return clean.Length <= 255 ? clean : clean[..(255 - Path.GetExtension(clean).Length)] + Path.GetExtension(clean)[..Math.Min(Path.GetExtension(clean).Length, 20)];
    }

    private static string Megabytes(long bytes) => $"{bytes / 1024d / 1024d:0.#} MB";
    private static string Escape(string text) => string.Concat(text.Select(c => "\\`*_[]()<>!#|".Contains(c) ? $"\\{c}" : c.ToString()));
    private static Exception Root(Exception exception) { while (exception.InnerException is not null) exception = exception.InnerException; return exception; }
}

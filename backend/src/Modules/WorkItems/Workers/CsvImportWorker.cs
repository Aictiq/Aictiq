using System.Globalization;
using System.Text.Json;
using Aictiq.Modules.WorkItems.Domain;
using Aictiq.Modules.WorkItems.Endpoints;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aictiq.Modules.WorkItems.Workers;

/// <summary>Consumes one durable import at a time. Each flush is bounded so a malformed row
/// becomes a report entry and never loses the successful work before it.</summary>
public sealed class CsvImportWorker(IServiceScopeFactory scopes, TimeProvider clock, ILogger<CsvImportWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try { await RunOnceAsync(token); } catch (Exception ex) when (!token.IsCancellationRequested) { logger.LogWarning(ex, "CSV import worker sweep failed"); }
            try { await Task.Delay(TimeSpan.FromSeconds(2), clock, token); } catch (OperationCanceledException) { break; }
        }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<WorkItemsDbContext>();
        var job = await db.CsvImportJobs.IgnoreQueryFilters().Where(x => x.Status == CsvImportStatus.Pending).OrderBy(x => x.CreatedAt).FirstOrDefaultAsync(ct);
        if (job is null) return;
        job.Status = CsvImportStatus.Running; job.StartedAt = clock.GetUtcNow(); await db.SaveChangesAsync(ct);
        var tenant = scope.ServiceProvider.GetRequiredService<AmbientCurrentTenant>();
        using var current = tenant.Use(job.OrganizationId);
        try { await ProcessAsync(db, scope.ServiceProvider.GetRequiredService<IUserDirectory>(), scope.ServiceProvider.GetRequiredService<IProjectAccess>(), job, clock, ct); }
        catch (Exception ex)
        {
            job.Status = CsvImportStatus.Failed; job.Errors = [.. job.Errors.Append(ex.Message)]; job.CompletedAt = clock.GetUtcNow(); await db.SaveChangesAsync(ct); throw;
        }
    }

    internal static async Task ProcessAsync(WorkItemsDbContext db, IUserDirectory directory, IProjectAccess access, CsvImportJob job, TimeProvider clock, CancellationToken ct)
    {
        var mapping = JsonSerializer.Deserialize<CsvColumnMapping>(job.MappingJson, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new(null, null);
        var rows = CsvTable.Parse(job.Csv).Rows; var fields = mapping.Fields ?? new Dictionary<string, string>();
        await WorkflowEndpoints.EnsureDefaultAsync(db, job.OrganizationId, job.ProjectId, ct);
        var states = await db.WorkflowStates.Where(s => db.Workflows.Any(w => w.Id == s.WorkflowId && w.ProjectId == job.ProjectId)).ToListAsync(ct);
        var initial = states.Single(s => s.IsInitial); var errors = job.Errors.ToList(); var pendingParents = new List<(WorkItem Item, string ExternalRef, int Row)>();
        var labelsByName = await db.Labels.Where(l => l.ProjectId == job.ProjectId).ToDictionaryAsync(l => l.Name, StringComparer.OrdinalIgnoreCase, ct);
        // Everyone who can see the project, asked of Tenancy through the contract. An address
        // outside that set stays unassigned: a CSV column is not a way to discover which
        // addresses have accounts on this instance, nor to subscribe them to this project.
        var memberIds = (await access.ListProjectMemberIdsAsync(job.ProjectId, ct)).ToHashSet(StringComparer.Ordinal);
        for (var offset = 0; offset < rows.Count; offset += 200)
        {
            foreach (var (row, index) in rows.Skip(offset).Take(200).Select((row, index) => (row, index: offset + index + 2)))
            {
                var title = Get(row, fields, "title").Trim(); var external = Get(row, fields, "externalRef").Trim();
                if (string.IsNullOrWhiteSpace(title)) { errors.Add($"Row {index}: title is required."); job.SkippedRows++; continue; }
                if (title.Length > 500) { errors.Add($"Row {index}: title exceeds 500 characters."); job.SkippedRows++; continue; }
                if (!string.IsNullOrEmpty(external) && await db.Items.AnyAsync(x => x.ProjectId == job.ProjectId && x.ExternalRef == external, ct)) { job.SkippedRows++; continue; }
                var type = Type(Get(row, fields, "type")); var state = State(states, Get(row, fields, "state")) ?? initial;
                var assignee = await AssigneeAsync(directory, memberIds, Get(row, fields, "assigneeEmail"), ct);
                var points = Number(Get(row, fields, "points")); var estimate = Number(Get(row, fields, "estimateHours"));
                if (type is WorkItemType.Epic or WorkItemType.Feature) { points = null; estimate = null; }
                if (type == WorkItemType.Story) estimate = null;
                if (type == WorkItemType.Task) points = null;
                var now = clock.GetUtcNow();
                var item = new WorkItem { OrganizationId = job.OrganizationId, ProjectId = job.ProjectId, ProjectKey = job.ProjectKey, Number = await NextNumberAsync(db, job.ProjectId, job.OrganizationId, ct), Type = type, Title = title, DescriptionMarkdown = Get(row, fields, "description"), DescriptionHtml = "", StateId = state.Id, Priority = WorkItemPriority.None, AssigneeId = assignee, Points = points, EstimateHours = estimate, RemainingHours = type is WorkItemType.Task or WorkItemType.Bug ? estimate : null, ExternalRef = string.IsNullOrEmpty(external) ? null : external, CreatedBy = job.RequestedBy, CreatedAt = Date(Get(row, fields, "createdAt")) ?? now, UpdatedAt = now };
                db.Items.Add(item); job.CreatedRows++; var parent = Get(row, fields, "parentExternalRef").Trim(); if (!string.IsNullOrEmpty(parent)) pendingParents.Add((item, parent, index));
                foreach (var name in Get(row, fields, "labels").Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!labelsByName.TryGetValue(name, out var label)) { label = new Label { OrganizationId = job.OrganizationId, ProjectId = job.ProjectId, Name = name }; db.Labels.Add(label); labelsByName[name] = label; }
                    db.ItemLabels.Add(new ItemLabel { OrganizationId = job.OrganizationId, ItemId = item.Id, LabelId = label.Id, AddedAt = now, AddedBy = job.RequestedBy });
                }
            }
            job.ProcessedRows = Math.Min(offset + 200, rows.Count); job.Errors = [.. errors.Take(200)]; await db.SaveChangesAsync(ct); db.ChangeTracker.Clear();
            // Reattach the durable job after clearing item/label tracking state.
            job = await db.CsvImportJobs.SingleAsync(x => x.Id == job.Id, ct);
        }
        var externalRefs = pendingParents.Select(x => x.ExternalRef).Distinct().ToArray();
        var parentByRef = externalRefs.Length == 0 ? new Dictionary<string, WorkItem>() : await db.Items.Where(x => x.ProjectId == job.ProjectId && x.ExternalRef != null && externalRefs.Contains(x.ExternalRef)).ToDictionaryAsync(x => x.ExternalRef!, ct);
        foreach (var (item, external, row) in pendingParents)
        {
            if (!parentByRef.TryGetValue(external, out var parent)) { errors.Add($"Row {row}: parent '{external}' was not found."); continue; }
            var loaded = await db.Items.SingleAsync(x => x.Id == item.Id, ct);
            if (Compatible(parent.Type, loaded.Type)) loaded.ParentId = parent.Id; else errors.Add($"Row {row}: parent '{external}' is not compatible with {loaded.Type}.");
        }
        job.Errors = [.. errors.Take(200)]; job.Status = CsvImportStatus.Completed; job.CompletedAt = clock.GetUtcNow(); await db.SaveChangesAsync(ct);
    }

    private static async Task<string?> AssigneeAsync(IUserDirectory directory, IReadOnlySet<string> members, string email, CancellationToken ct) { if (string.IsNullOrWhiteSpace(email) || members.Count == 0) return null; var user = await directory.FindByEmailAsync(email.Trim(), ct); return user is not null && members.Contains(user.Id) ? user.Id : null; }
    private static string Get(IReadOnlyDictionary<string, string> row, IReadOnlyDictionary<string, string> fields, string field) => fields.TryGetValue(field, out var column) && row.TryGetValue(column, out var value) ? value : "";
    private static WorkItemType Type(string value) => value.Trim().ToLowerInvariant() switch { "epic" => WorkItemType.Epic, "feature" => WorkItemType.Feature, "story" or "user story" or "product backlog item" => WorkItemType.Story, "bug" or "defect" => WorkItemType.Bug, _ => WorkItemType.Task };
    private static WorkflowState? State(IEnumerable<WorkflowState> states, string value) => states.FirstOrDefault(s => s.Name.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase)) ?? Category(states, value);
    private static WorkflowState? Category(IEnumerable<WorkflowState> states, string value) => value.Trim().ToLowerInvariant() switch { "new" or "to do" or "todo" => states.FirstOrDefault(s => s.Category == WorkflowStateCategory.Proposed), "done" or "closed" or "completed" => states.FirstOrDefault(s => s.Category == WorkflowStateCategory.Completed), "resolved" => states.FirstOrDefault(s => s.Category == WorkflowStateCategory.Resolved), _ => states.FirstOrDefault(s => s.Category == WorkflowStateCategory.Active) };
    private static decimal? Number(string value) => decimal.TryParse(value.Replace("h", "", StringComparison.OrdinalIgnoreCase).Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var number) && number >= 0 ? number : null;
    private static DateTimeOffset? Date(string value) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : null;
    private static bool Compatible(WorkItemType parent, WorkItemType child) => ItemHierarchy.Allows(parent, child);
    private static async Task<int> NextNumberAsync(WorkItemsDbContext db, Guid projectId, Guid organizationId, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO work.project_sequences (project_id, organization_id, next_number) VALUES ({projectId}, {organizationId}, 1) ON CONFLICT (project_id) DO NOTHING", ct);
        await using var command = db.Database.GetDbConnection().CreateCommand(); command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction(); command.CommandText = "UPDATE work.project_sequences SET next_number = next_number + 1 WHERE project_id = @projectId RETURNING next_number - 1"; var parameter = command.CreateParameter(); parameter.ParameterName = "projectId"; parameter.Value = projectId; command.Parameters.Add(parameter); return Convert.ToInt32(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }
}

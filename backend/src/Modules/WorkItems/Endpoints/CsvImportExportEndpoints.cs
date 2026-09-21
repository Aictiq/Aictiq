using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Aictiq.Modules.WorkItems.Domain;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Tenancy;

namespace Aictiq.Modules.WorkItems.Endpoints;

public sealed record CsvColumnMapping(string? Preset, IReadOnlyDictionary<string, string>? Fields);
public sealed record CsvImportPreview(IReadOnlyList<string> Columns, string Preset, IReadOnlyDictionary<string, string> SuggestedMapping, int Rows, IReadOnlyList<IReadOnlyDictionary<string, string>> SampleRows);
public sealed record CsvImportJobView(Guid Id, CsvImportStatus Status, int TotalRows, int ProcessedRows, int CreatedRows, int SkippedRows, IReadOnlyList<string> Errors, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt);

/// <summary>CSV is intentionally parsed here rather than through a spreadsheet dependency: exports from Jira,
/// Azure DevOps and Aictiq are RFC-4180 text, and malformed records are retained as row errors rather than
/// aborting an otherwise useful import.</summary>
public static class CsvImportExportEndpoints
{
    private const long MaxBytes = 20 * 1024 * 1024;

    public static IEndpointRouteBuilder MapCsvImportExportEndpoints(this IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/orgs/{orgSlug}/projects/{projectKey}").WithTags("CSV import and export").RequireAuthorization();
        group.MapPost("/import/csv", Upload).RequireProjectRole(ProjectRole.Member).RequireProjectWritable().RequireScope(Scopes.Write);
        group.MapGet("/imports/{jobId:guid}", Status).RequireProjectRole(ProjectRole.Guest).RequireScope(Scopes.Read);
        group.MapGet("/export/csv", Export).RequireProjectRole(ProjectRole.Guest).RequireScope(Scopes.Read);
        return api;
    }

    private static async Task<IResult> Upload(HttpRequest request, HttpContext http, WorkItemsDbContext db, ICurrentUser user, ICurrentTenant tenant, TimeProvider clock, CancellationToken ct)
    {
        if (!request.HasFormContentType) return Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = ["Upload a CSV file as multipart/form-data."] });
        var form = await request.ReadFormAsync(ct); var file = form.Files.GetFile("file");
        if (file is null || file.Length == 0) return Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = ["A non-empty CSV file is required."] });
        if (file.Length > MaxBytes) return Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = ["CSV files are limited to 20 MB."] });
        string csv;
        await using (var stream = file.OpenReadStream()) using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true)) csv = await reader.ReadToEndAsync(ct);
        var table = CsvTable.Parse(csv);
        if (table.Columns.Count == 0) return Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = ["The CSV must include a header row."] });
        var mapping = ReadMapping(form["mapping"].FirstOrDefault(), table.Columns);
        var preview = new CsvImportPreview(table.Columns, mapping.Preset ?? "custom", mapping.Fields!, table.Rows.Count,
            table.Rows.Take(10).Select(row => (IReadOnlyDictionary<string, string>)row).ToList());
        if (!string.Equals(form["commit"].FirstOrDefault(), "true", StringComparison.OrdinalIgnoreCase)) return Results.Ok(preview);
        if (!mapping.Fields!.TryGetValue("title", out var titleColumn) || !table.Columns.Contains(titleColumn, StringComparer.OrdinalIgnoreCase))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["mapping.title"] = ["Map a title column before importing."] });
        var project = http.ResolvedProject()!;
        var job = new CsvImportJob { OrganizationId = tenant.OrganizationId!.Value, ProjectId = project.Id, ProjectKey = project.Key, RequestedBy = user.UserId!, Csv = csv, MappingJson = JsonSerializer.Serialize(mapping), Status = CsvImportStatus.Pending, TotalRows = table.Rows.Count, CreatedAt = clock.GetUtcNow() };
        db.CsvImportJobs.Add(job); await db.SaveChangesAsync(ct);
        return Results.Accepted($"/api/v1/orgs/{http.Request.RouteValues["orgSlug"]}/projects/{project.Key}/imports/{job.Id}", new { job = ToView(job), preview });
    }

    private static async Task<IResult> Status(Guid jobId, HttpContext http, WorkItemsDbContext db, CancellationToken ct)
    {
        var projectId = http.ResolvedProjectId()!.Value;
        var job = await db.CsvImportJobs.AsNoTracking().FirstOrDefaultAsync(x => x.Id == jobId && x.ProjectId == projectId, ct);
        return job is null ? Results.NotFound() : Results.Ok(ToView(job));
    }

    private static async Task Export(HttpContext http, HttpResponse response, WorkItemsDbContext db, IUserDirectory directory, ICurrentUser user, string? filter, CancellationToken ct)
    {
        var projectId = http.ResolvedProjectId()!.Value; var parsed = ItemFilter.Parse(filter);
        if (parsed.Error is not null) { response.StatusCode = StatusCodes.Status422UnprocessableEntity; await response.WriteAsJsonAsync(new { errors = new Dictionary<string, string[]> { ["filter"] = [parsed.Error] } }, ct); return; }
        var (query, filterError) = await ItemQueries.ApplyFilterAsync(db.Items.AsNoTracking().Where(x => x.ProjectId == projectId), parsed, projectId, db, directory, user.UserId, ct);
        if (filterError is not null) { response.StatusCode = StatusCodes.Status422UnprocessableEntity; await response.WriteAsJsonAsync(new { errors = new Dictionary<string, string[]> { ["filter"] = [filterError] } }, ct); return; }
        var rows = await query.OrderBy(x => x.Number).ToListAsync(ct); var ids = rows.Select(x => x.Id).ToArray();
        var labels = await db.ItemLabels.Where(x => ids.Contains(x.ItemId)).Join(db.Labels, l => l.LabelId, l => l.Id, (link, label) => new { link.ItemId, label.Name }).ToListAsync(ct);
        var states = await db.WorkflowStates.Where(s => rows.Select(i => i.StateId).Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        response.ContentType = "text/csv; charset=utf-8"; response.Headers.ContentDisposition = "attachment; filename=work-items.csv";
        await using var writer = new StreamWriter(response.Body, Encoding.UTF8, 1024, leaveOpen: true);
        await writer.WriteLineAsync("externalRef,parentExternalRef,type,title,description,state,assigneeEmail,labels,points,estimateHours,createdAt");
        foreach (var item in rows)
        {
            var parent = item.ParentId is null ? null : rows.FirstOrDefault(x => x.Id == item.ParentId)?.ExternalRef;
            var values = new[] { item.ExternalRef, parent, item.Type.ToString(), item.Title, item.DescriptionMarkdown, states.GetValueOrDefault(item.StateId), null, string.Join(';', labels.Where(x => x.ItemId == item.Id).Select(x => x.Name)), item.Points?.ToString(CultureInfo.InvariantCulture), item.EstimateHours?.ToString(CultureInfo.InvariantCulture), item.CreatedAt.ToString("O") };
            await writer.WriteLineAsync(string.Join(',', values.Select(CsvTable.Escape)));
        }
    }

    internal static CsvColumnMapping ReadMapping(string? json, IReadOnlyList<string> columns)
    {
        CsvColumnMapping? supplied = null;
        try { if (!string.IsNullOrWhiteSpace(json)) supplied = JsonSerializer.Deserialize<CsvColumnMapping>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)); } catch (JsonException) { }
        var preset = supplied?.Preset?.Trim().ToLowerInvariant();
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (field, candidates) in Presets(preset))
        {
            var column = candidates.Select(candidate => columns.FirstOrDefault(c => c.Equals(candidate, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(c => c is not null);
            if (column is not null) fields[field] = column;
        }
        if (supplied?.Fields is not null) foreach (var (field, column) in supplied.Fields.Where(x => !string.IsNullOrWhiteSpace(x.Value))) fields[field] = column.Trim();
        var detected = preset ?? (columns.Any(c => c.Equals("Issue key", StringComparison.OrdinalIgnoreCase)) ? "jira" : columns.Any(c => c.Equals("Work Item Type", StringComparison.OrdinalIgnoreCase)) ? "azure-devops" : "aictiq");
        return new CsvColumnMapping(detected, fields);
    }

    internal static IEnumerable<(string Field, string[] Candidates)> Presets(string? preset) => preset switch
    {
        "jira" => [ ("externalRef", ["Issue key", "Issue id"]), ("parentExternalRef", ["Parent", "Parent key"]), ("type", ["Issue Type"]), ("title", ["Summary"]), ("description", ["Description"]), ("state", ["Status"]), ("assigneeEmail", ["Assignee", "Assignee email"]), ("labels", ["Labels"]), ("points", ["Story Points", "Story point estimate"]), ("estimateHours", ["Original Estimate", "Original estimate"]), ("createdAt", ["Created"]) ],
        "azure-devops" or "azure" => [ ("externalRef", ["ID"]), ("parentExternalRef", ["Parent"]), ("type", ["Work Item Type"]), ("title", ["Title"]), ("description", ["Description"]), ("state", ["State"]), ("assigneeEmail", ["Assigned To"]), ("labels", ["Tags"]), ("points", ["Story Points", "Effort"]), ("estimateHours", ["Original Estimate"]), ("createdAt", ["Created Date"]) ],
        _ => [ ("externalRef", ["externalRef", "external_ref"]), ("parentExternalRef", ["parentExternalRef", "parent_external_ref"]), ("type", ["type"]), ("title", ["title"]), ("description", ["description", "descriptionMarkdown"]), ("state", ["state"]), ("assigneeEmail", ["assigneeEmail"]), ("labels", ["labels"]), ("points", ["points"]), ("estimateHours", ["estimateHours"]), ("createdAt", ["createdAt"]) ]
    };

    internal static CsvImportJobView ToView(CsvImportJob job) => new(job.Id, job.Status, job.TotalRows, job.ProcessedRows, job.CreatedRows, job.SkippedRows, job.Errors, job.CreatedAt, job.CompletedAt);
}

internal sealed record CsvTable(IReadOnlyList<string> Columns, IReadOnlyList<Dictionary<string, string>> Rows)
{
    public static CsvTable Parse(string text)
    {
        var records = ReadRecords(text); if (records.Count == 0) return new([], []);
        var columns = records[0].Select(x => x.Trim()).ToList();
        var rows = records.Skip(1).Where(r => r.Any(v => !string.IsNullOrWhiteSpace(v))).Select(r => columns.Select((column, index) => new { column, value = index < r.Count ? r[index] : "" }).ToDictionary(x => x.column, x => x.value, StringComparer.OrdinalIgnoreCase)).ToList();
        return new(columns, rows);
    }
    private static List<List<string>> ReadRecords(string text)
    {
        var result = new List<List<string>>(); var row = new List<string>(); var value = new StringBuilder(); var quoted = false;
        for (var i = 0; i < text.Length; i++) { var c = text[i]; if (c == '"') { if (quoted && i + 1 < text.Length && text[i + 1] == '"') { value.Append(c); i++; } else quoted = !quoted; } else if (c == ',' && !quoted) { row.Add(value.ToString()); value.Clear(); } else if ((c == '\n' || c == '\r') && !quoted) { if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++; row.Add(value.ToString()); value.Clear(); result.Add(row); row = []; } else value.Append(c); }
        if (value.Length > 0 || row.Count > 0) { row.Add(value.ToString()); result.Add(row); } return result;
    }
    public static string Escape(string? value) => Aictiq.SharedKernel.Text.CsvCell.Escape(value);
}

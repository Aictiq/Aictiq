using System.Text;
using Aictiq.Modules.Tenancy.Domain;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Persistence;
using Aictiq.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Aictiq.Modules.Tenancy.Endpoints;

public sealed record AuditLogView(Guid Id, string EntityType, string EntityId, string Field, string? OldValue, string? NewValue, string? UserId, DateTimeOffset At);
public sealed record AuditPage(IReadOnlyList<AuditLogView> Items, int Page, bool HasMore);

public static class AuditLogEndpoints
{
    public static IEndpointRouteBuilder MapAuditLogEndpoints(this IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/orgs/{orgSlug}/audit").WithTags("Audit").RequireAuthorization();
        group.MapGet("", List).RequireOrgRole(OrgRole.Guest).RequireScope(Scopes.Read);
        group.MapGet("/export.csv", Export).RequireOrgRole(OrgRole.Guest).RequireScope(Scopes.Read);
        return api;
    }
    private static async Task<IResult> List(DateOnly? from, DateOnly? to, string? actor, string? action, int page, TenancyDbContext db, ICurrentTenant tenant, ICurrentUser user, CancellationToken ct)
    {
        if (!await Admin(db, tenant, user, ct)) return Results.NotFound();
        var query = Query(db, tenant.OrganizationId!.Value, from, to, actor, action); var size = 50; var rows = await query.Skip(Math.Max(0, page) * size).Take(size + 1).ToListAsync(ct);
        return Results.Ok(new AuditPage(rows.Take(size).Select(View).ToList(), Math.Max(0, page), rows.Count > size));
    }
    private static async Task<IResult> Export(DateOnly? from, DateOnly? to, string? actor, string? action, TenancyDbContext db, ICurrentTenant tenant, ICurrentUser user, CancellationToken ct)
    {
        if (!await Admin(db, tenant, user, ct)) return Results.NotFound();
        var rows = await Query(db, tenant.OrganizationId!.Value, from, to, actor, action).Take(10_000).ToListAsync(ct); var csv = new StringBuilder("At,Actor,Entity type,Entity id,Action,Old value,New value\n");
        foreach (var row in rows) csv.AppendJoin(',', [Escape(row.At.ToString("O")), Escape(row.UserId), Escape(row.EntityType), Escape(row.EntityId), Escape(row.Field), Escape(row.OldValue), Escape(row.NewValue)]).Append('\n');
        return Results.File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv", "aictiq-audit.csv");
    }
    private static IQueryable<AuditLogEntry> Query(TenancyDbContext db, Guid organizationId, DateOnly? from, DateOnly? to, string? actor, string? action) { var query = db.Set<AuditLogEntry>().AsNoTracking().Where(x => x.OrganizationId == organizationId); if (from is not null) query = query.Where(x => x.At >= from.Value.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)); if (to is not null) query = query.Where(x => x.At < to.Value.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)); if (!string.IsNullOrWhiteSpace(actor)) query = query.Where(x => x.UserId == actor); if (!string.IsNullOrWhiteSpace(action)) query = query.Where(x => x.Field == action); return query.OrderByDescending(x => x.At); }
    private static async Task<bool> Admin(TenancyDbContext db, ICurrentTenant tenant, ICurrentUser user, CancellationToken ct) => await db.Members.IgnoreQueryFilters().AnyAsync(x => x.OrganizationId == tenant.OrganizationId && x.UserId == user.UserId && (x.Role == OrgRole.Owner || x.Role == OrgRole.Admin), ct);
    private static AuditLogView View(AuditLogEntry row) => new(row.Id, row.EntityType, row.EntityId, row.Field, row.OldValue, row.NewValue, row.UserId, row.At);
    private static string Escape(string? value) => Aictiq.SharedKernel.Text.CsvCell.Escape(value);
}

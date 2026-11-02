using Aictiq.Modules.Automation.Domain;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Aictiq.Modules.Automation.Endpoints;

/// <param name="RunnerName">Null once the runner is deleted.</param>
/// <param name="RunnerUsable">The runner is neither deleted nor disabled, so the member's runs go to it.</param>
public sealed record MemberRunnerView(string UserId, Guid RunnerId, string? RunnerName, bool RunnerUsable);

/// <param name="RunnerId">The caller's assigned runner while it is usable; null when they have none.</param>
/// <param name="Locked">True when the caller is not a project Admin, so every run they start uses it.</param>
public sealed record MyRunnerView(Guid? RunnerId, string? RunnerName, bool Locked);

/// <param name="RunnerId">Null clears the member's runner.</param>
public sealed record SetMemberRunnerRequest(Guid? RunnerId);

/// <summary>The runner each project member's runs go to: <see cref="ProjectMemberRunner"/>.</summary>
public static class MemberRunnerEndpoints
{
    public static IEndpointRouteBuilder MapMemberRunnerEndpoints(this IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/orgs/{orgSlug}/projects/{projectKey}/member-runners")
            .WithTags("Factory settings").RequireAuthorization();
        group.MapGet("/", ListAsync).RequireProjectRole(ProjectRole.Admin).RequireScope(Scopes.Read);
        group.MapGet("/me", MineAsync).RequireProjectRole(ProjectRole.Guest).RequireScope(Scopes.Read);
        group.MapPut("/{userId}", SetAsync).RequireProjectRole(ProjectRole.Admin).RequireProjectWritable().RequireScope(Scopes.Write);
        return api;
    }

    /// <summary>
    /// The runner <paramref name="userId"/> was assigned in the project, while it is neither deleted
    /// nor disabled; null otherwise, and the person's runs go where they would without one.
    /// </summary>
    public static async Task<Runner?> UsableAsync(AutomationDbContext db, Guid projectId, string userId, CancellationToken ct) =>
        await db.MemberRunners.AsNoTracking()
            .Where(row => row.ProjectId == projectId && row.UserId == userId)
            .Join(db.Runners.Where(r => r.DeletedAt == null && r.DisabledAt == null),
                row => row.RunnerId, runner => runner.Id, (_, runner) => runner)
            .AsNoTracking()
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// The runner every run <paramref name="userId"/> starts must use: their usable assigned runner,
    /// unless they are a project Admin (an organization Owner is one implicitly), who may pick any.
    /// </summary>
    public static async Task<Runner?> LockedAsync(
        AutomationDbContext db, IProjectAccess access, Guid projectId, string userId, CancellationToken ct)
    {
        var runner = await UsableAsync(db, projectId, userId, ct);
        return runner is null || await access.GetProjectRoleAsync(userId, projectId, ct) == ProjectRole.Admin
            ? null
            : runner;
    }

    private static async Task<IResult> ListAsync(HttpContext http, AutomationDbContext db, CancellationToken ct)
    {
        var projectId = http.ResolvedProjectId()!.Value;
        var rows = await db.MemberRunners.AsNoTracking().Where(row => row.ProjectId == projectId)
            .Join(db.Runners, row => row.RunnerId, runner => runner.Id, (row, runner) => new MemberRunnerView(
                row.UserId, row.RunnerId, runner.DeletedAt == null ? runner.Name : null,
                runner.DeletedAt == null && runner.DisabledAt == null))
            .ToListAsync(ct);
        return Results.Ok(rows);
    }

    private static async Task<IResult> MineAsync(
        HttpContext http, AutomationDbContext db, IProjectAccess access, ICurrentUser user, CancellationToken ct)
    {
        var projectId = http.ResolvedProjectId()!.Value;
        var runner = await UsableAsync(db, projectId, user.UserId!, ct);
        if (runner is null)
        {
            return Results.Ok(new MyRunnerView(null, null, false));
        }
        var admin = await access.GetProjectRoleAsync(user.UserId!, projectId, ct) == ProjectRole.Admin;
        return Results.Ok(new MyRunnerView(runner.Id, runner.Name, !admin));
    }

    private static async Task<IResult> SetAsync(
        string userId, SetMemberRunnerRequest request, HttpContext http, AutomationDbContext db,
        ICurrentTenant tenant, ICurrentUser user, IProjectAccess access, TimeProvider clock, CancellationToken ct)
    {
        var projectId = http.ResolvedProjectId()!.Value;
        if (await access.GetProjectRoleAsync(userId, projectId, ct) is null)
        {
            return RunEndpoints.NotFound();
        }

        var row = await db.MemberRunners.SingleOrDefaultAsync(row => row.ProjectId == projectId && row.UserId == userId, ct);
        if (request.RunnerId is not { } runnerId)
        {
            if (row is not null)
            {
                db.MemberRunners.Remove(row);
                await db.SaveChangesAsync(ct);
            }
            return Results.NoContent();
        }

        // Another organization's runners are outside the query filter, so they read as missing.
        var runner = await db.Runners.AsNoTracking()
            .SingleOrDefaultAsync(r => r.Id == runnerId && r.DeletedAt == null && r.DisabledAt == null, ct);
        if (runner is null)
        {
            return Results.ValidationProblem(
                new Dictionary<string, string[]> { ["runnerId"] = ["Choose an enabled runner of this organization."] },
                type: ProblemTypes.Validation);
        }

        var now = clock.GetUtcNow();
        if (row is null)
        {
            row = new ProjectMemberRunner
            {
                OrganizationId = tenant.OrganizationId!.Value,
                ProjectId = projectId,
                UserId = userId,
                RunnerId = runnerId,
                AssignedBy = user.UserId!,
                UpdatedAt = now,
            };
            db.MemberRunners.Add(row);
        }
        else
        {
            row.RunnerId = runnerId;
            row.AssignedBy = user.UserId!;
            row.UpdatedAt = now;
        }
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Two admins assigned the same member at once; the other one's choice stands.
            return Results.Problem(title: "Someone else changed this member's runner. Reload and try again.",
                type: ProblemTypes.Conflict, statusCode: StatusCodes.Status409Conflict);
        }
        return Results.Ok(new MemberRunnerView(userId, runnerId, runner.Name, true));
    }
}

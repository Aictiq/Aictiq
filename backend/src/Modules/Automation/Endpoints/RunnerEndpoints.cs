using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aictiq.Modules.Automation.Auth;
using Aictiq.Modules.Automation.Domain;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Aictiq.Modules.Automation.Endpoints;

/// <param name="TokenDisplay"><c>jrn_a1b2c3d4…</c> - all that survives of the secret.</param>
/// <param name="IsOnline">Seen within two heartbeats. Decided here so every client agrees on what "online" means.</param>
public sealed record RunnerView(
    Guid Id, string Name, string TokenDisplay, string RegisteredBy, string? RegisteredByName,
    RunnerCapabilities? Capabilities, DateTimeOffset? LastSeenAt, bool IsOnline, bool IsDisabled,
    DateTimeOffset CreatedAt);

/// <param name="Secret">Returned exactly once. Aictiq keeps only its hash.</param>
public sealed record RunnerIssuedView(RunnerView Runner, string Secret);

/// <param name="SameMachineAs">
/// One of the caller's runners in another organization they administer (from
/// <c>GET runners/elsewhere</c>). The new runner is that machine's registration here: it
/// gets its own secret, and the machine adds this organization as a separate profile. The
/// name defaults to the other runner's.
/// </param>
public sealed record CreateRunnerRequest(string? Name, Guid? SameMachineAs = null);

/// <summary>
/// A machine the caller already runs for other organizations: their own live runners there,
/// grouped by the machine id the CLI reports, so a machine with three profiles is one entry.
/// </summary>
/// <param name="RunnerId">The most recently seen of its runners; what <c>sameMachineAs</c> names.</param>
/// <param name="IsConnectedHere">This organization already has a runner reporting the same machine.</param>
public sealed record RunnerMachineView(
    Guid RunnerId, string Name, RunnerCapabilities? Capabilities, DateTimeOffset? LastSeenAt, bool IsOnline,
    bool IsConnectedHere, IReadOnlyList<RunnerMachineOrganizationView> Organizations);

public sealed record RunnerMachineOrganizationView(string Slug, string Name, Guid RunnerId, string RunnerName);

/// <summary>
/// No <c>version</c>, deliberately - the documented exception to the xmin round-trip, like a
/// membership role. The runner writes its own row on every heartbeat, so a version token would
/// make every Admin edit race a machine and lose. Both fields are absolute assignments rather
/// than deltas; there is nothing a stale read could silently undo.
/// </summary>
/// <param name="Disabled">True disables the runner (its secret answers <c>token-revoked</c>); false enables it again.</param>
public sealed record UpdateRunnerRequest(string? Name, bool? Disabled);

public sealed record RunnerHelloRequest(RunnerCapabilities? Capabilities);

public sealed record RunnerHeartbeatRequest(RunnerCapabilities? Capabilities);

/// <summary>What a runner is told on arrival: who it is, where, and the limits it must keep.</summary>
public sealed record RunnerHelloView(
    Guid RunnerId, string Name, string OrganizationSlug,
    int HeartbeatIntervalSeconds, int PollTimeoutSeconds, int MaxLogBytes, int MaxLogBatchBytes, int MaxRunMinutes);

/// <summary>
/// Runners: the roster an organization Admin manages (<c>/orgs/{slug}/runners</c>) and the
/// protocol a runner speaks with its own secret (<c>/runner/*</c>).
///
/// The two halves never share a principal. The roster is a person's business and goes
/// through <c>RequireOrgRole</c>; the protocol accepts nothing but a runner principal
/// (<see cref="RunnerDefaults.Policy"/>), and a runner principal is accepted nowhere else.
/// </summary>
public static partial class RunnerEndpoints
{
    public static IEndpointRouteBuilder MapRunnerEndpoints(this IEndpointRouteBuilder api)
    {
        var roster = api.MapGroup("/orgs/{orgSlug}/runners")
            .WithTags("Runners")
            .RequireAuthorization();

        roster.MapGet("/", ListAsync).RequireOrgRole(OrgRole.Admin).RequireScope(Scopes.Read);
        roster.MapGet("/elsewhere", ElsewhereAsync).RequireOrgRole(OrgRole.Admin).RequireScope(Scopes.Read);
        // Registering or rotating mints a credential, so it takes the admin scope exactly as
        // creating a token does: a leaked read-write token must not be a way to a machine
        // that will be handed agent credentials.
        roster.MapPost("/", CreateAsync).RequireOrgRole(OrgRole.Admin).RequireScope(Scopes.Admin);
        roster.MapPatch("/{runnerId:guid}", UpdateAsync).RequireOrgRole(OrgRole.Admin).RequireScope(Scopes.Admin);
        roster.MapDelete("/{runnerId:guid}", DeleteAsync).RequireOrgRole(OrgRole.Admin).RequireScope(Scopes.Admin);
        roster.MapPost("/{runnerId:guid}/rotate", RotateAsync).RequireOrgRole(OrgRole.Admin).RequireScope(Scopes.Admin);

        var protocol = api.MapGroup("/runner")
            .WithTags("Runner protocol")
            .RequireAuthorization(RunnerDefaults.Policy);

        protocol.MapPost("/hello", HelloAsync);
        protocol.MapPost("/heartbeat", HeartbeatAsync);

        return api;
    }

    // ── roster ───────────────────────────────────────────────────────────────────────

    private static async Task<IResult> ListAsync(
        AutomationDbContext db, IUserDirectory directory, IOptions<AutomationOptions> options, TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var runners = await db.Runners.AsNoTracking()
            .Where(r => r.DeletedAt == null)
            .OrderBy(r => r.Name)
            .ToListAsync(cancellationToken);
        var people = await directory.GetAsync([.. runners.Select(r => r.RegisteredBy).Distinct()], cancellationToken);
        var now = clock.GetUtcNow();
        return Results.Ok(runners.Select(r => ToView(r, people, options.Value, now)).ToList());
    }

    /// <summary>
    /// The caller's own machines in their other organizations. Only organizations where they
    /// are an Admin now (the roster there is theirs to see anyway), only runners they
    /// registered themselves, and nothing at all for a token bound to one organization or an
    /// agent: that credential must not learn where else its owner works.
    /// </summary>
    private static async Task<IResult> ElsewhereAsync(
        AutomationDbContext db, AmbientCurrentTenant tenant, ICurrentUser user, IProjectAccess access,
        IOptions<AutomationOptions> options, TimeProvider clock, CancellationToken cancellationToken)
    {
        var hereMachines = (await db.Runners.AsNoTracking()
                .Where(r => r.DeletedAt == null)
                .Select(r => r.Capabilities)
                .ToListAsync(cancellationToken))
            .Select(c => c?.MachineId)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);

        var now = clock.GetUtcNow();
        var elsewhere = await OwnRunnersElsewhereAsync(db, tenant, user, access, cancellationToken);
        var machines = elsewhere
            .GroupBy(x => x.Runner.Capabilities?.MachineId ?? x.Runner.Id.ToString())
            .Select(group =>
            {
                var latest = group.OrderByDescending(x => x.Runner.LastSeenAt ?? x.Runner.CreatedAt).First().Runner;
                return new RunnerMachineView(
                    latest.Id, latest.Name, latest.Capabilities,
                    group.Max(x => x.Runner.LastSeenAt),
                    group.Any(x => IsOnline(x.Runner, options.Value, now)),
                    latest.Capabilities?.MachineId is { } machineId && hereMachines.Contains(machineId),
                    [.. group
                        .OrderBy(x => x.Organization.Name, StringComparer.OrdinalIgnoreCase)
                        .Select(x => new RunnerMachineOrganizationView(
                            x.Organization.Slug, x.Organization.Name, x.Runner.Id, x.Runner.Name))]);
            })
            .OrderBy(machine => machine.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return Results.Ok(machines);
    }

    /// <summary>
    /// The one cross-organization read of the roster. Each organization is read under its own
    /// tenant, so the query filter and RLS stay what they are everywhere else; the membership
    /// list only decides which organizations are asked.
    /// </summary>
    private static async Task<List<(OrganizationRef Organization, Runner Runner)>> OwnRunnersElsewhereAsync(
        AutomationDbContext db, AmbientCurrentTenant tenant, ICurrentUser user, IProjectAccess access,
        CancellationToken cancellationToken)
    {
        if (user.UserId is not { } userId || user.IsAgent || user.OrganizationId is not null)
        {
            return [];
        }

        var here = tenant.OrganizationId;
        var found = new List<(OrganizationRef, Runner)>();
        foreach (var membership in await access.ListOrganizationsAsync(userId, cancellationToken))
        {
            if (membership.Organization.Id == here || !membership.Role.Satisfies(OrgRole.Admin))
            {
                continue;
            }

            using (tenant.Use(membership.Organization.Id))
            {
                var runners = await db.Runners.AsNoTracking()
                    .Where(r => r.RegisteredBy == userId && r.DeletedAt == null && r.DisabledAt == null)
                    .ToListAsync(cancellationToken);
                found.AddRange(runners.Select(r => (membership.Organization, r)));
            }
        }

        return found;
    }

    private static async Task<IResult> CreateAsync(
        string orgSlug, CreateRunnerRequest request, AutomationDbContext db, AmbientCurrentTenant tenant,
        ICurrentUser user, IProjectAccess access, IUserDirectory directory, IOptions<AutomationOptions> options,
        TimeProvider clock, CancellationToken cancellationToken)
    {
        Runner? source = null;
        if (request.SameMachineAs is { } sourceId)
        {
            var elsewhere = await OwnRunnersElsewhereAsync(db, tenant, user, access, cancellationToken);
            source = elsewhere.Select(x => x.Runner).FirstOrDefault(r => r.Id == sourceId);
            if (source is null)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["sameMachineAs"] = ["Pick one of your own runners in another organization you administer."],
                }, type: ProblemTypes.Validation);
            }
        }

        var name = string.IsNullOrWhiteSpace(request.Name) ? source?.Name : request.Name;
        if (NameError(name) is { } error)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["name"] = [error] }, type: ProblemTypes.Validation);
        }

        var now = clock.GetUtcNow();
        var runner = Runner.Register(tenant.OrganizationId!.Value, name!.Trim(), user.UserId!, now, out var secret);
        // The machine's last report, so it groups with its other registrations at once. The
        // runner replaces it with its own on its first hello here.
        runner.Capabilities = source?.Capabilities;
        db.Runners.Add(runner);
        // A name another live runner already has is a unique violation → 409.
        await db.SaveChangesAsync(cancellationToken);

        var people = await directory.GetAsync([runner.RegisteredBy], cancellationToken);
        return Results.Created($"/api/v1/orgs/{orgSlug}/runners/{runner.Id}",
            new RunnerIssuedView(ToView(runner, people, options.Value, now), secret));
    }

    private static async Task<IResult> UpdateAsync(
        Guid runnerId, UpdateRunnerRequest request, AutomationDbContext db, IUserDirectory directory,
        IOptions<AutomationOptions> options, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (request.Name is not null && NameError(request.Name) is { } nameError)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["name"] = [nameError] }, type: ProblemTypes.Validation);
        }

        var runner = await db.Runners.FirstOrDefaultAsync(r => r.Id == runnerId && r.DeletedAt == null, cancellationToken);
        if (runner is null)
        {
            return NotFound();
        }

        var now = clock.GetUtcNow();
        if (request.Name is not null)
        {
            runner.Name = request.Name.Trim();
        }
        if (request.Disabled is { } disabled)
        {
            runner.DisabledAt = disabled ? runner.DisabledAt ?? now : null;
        }
        runner.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);

        var people = await directory.GetAsync([runner.RegisteredBy], cancellationToken);
        return Results.Ok(ToView(runner, people, options.Value, now));
    }

    private static async Task<IResult> DeleteAsync(
        Guid runnerId, AutomationDbContext db, TimeProvider clock, CancellationToken cancellationToken)
    {
        var runner = await db.Runners.FirstOrDefaultAsync(r => r.Id == runnerId && r.DeletedAt == null, cancellationToken);
        if (runner is null)
        {
            return NotFound();
        }

        // Soft, and final: the runner's id will be on every run it executed. Its secret stops
        // working in the same save, and its name is free for a new runner. Tracked rather than
        // a bulk update so the audit log records who retired it.
        var now = clock.GetUtcNow();
        runner.DeletedAt = now;
        runner.DisabledAt ??= now;
        runner.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> RotateAsync(
        Guid runnerId, AutomationDbContext db, IUserDirectory directory, IOptions<AutomationOptions> options,
        TimeProvider clock, CancellationToken cancellationToken)
    {
        var runner = await db.Runners.FirstOrDefaultAsync(r => r.Id == runnerId && r.DeletedAt == null, cancellationToken);
        if (runner is null)
        {
            return NotFound();
        }

        var now = clock.GetUtcNow();
        var secret = runner.Rotate(now);
        await db.SaveChangesAsync(cancellationToken);

        var people = await directory.GetAsync([runner.RegisteredBy], cancellationToken);
        return Results.Ok(new RunnerIssuedView(ToView(runner, people, options.Value, now), secret));
    }

    // ── protocol ─────────────────────────────────────────────────────────────────────

    private static async Task<IResult> HelloAsync(
        RunnerHelloRequest? request, HttpContext http, AutomationDbContext db, ICurrentTenant tenant,
        IOrganizationLookup organizations, IOptions<AutomationOptions> options, TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (CapabilitiesErrors(request?.Capabilities) is { Count: > 0 } errors)
        {
            return Results.ValidationProblem(errors, type: ProblemTypes.Validation);
        }

        var runnerId = RunnerId(http);
        var organization = await organizations.FindByIdAsync(tenant.OrganizationId!.Value, cancellationToken);
        if (organization is null)
        {
            return NotFound();
        }

        var now = clock.GetUtcNow();
        var capabilities = Serialize(request?.Capabilities);
        // Unthrottled: a hello is rare, and it is the moment the roster should turn green.
        var updated = await db.Database.ExecuteSqlAsync($"""
            UPDATE automation.runners
            SET last_seen_at = {now}, capabilities = COALESCE({capabilities}::jsonb, capabilities)
            WHERE id = {runnerId} AND disabled_at IS NULL AND deleted_at IS NULL
            """, cancellationToken);
        if (updated == 0)
        {
            return NotFound();
        }

        var name = http.User.FindFirstValue("name") ?? "";
        var o = options.Value;
        return Results.Ok(new RunnerHelloView(runnerId, name, organization.Slug,
            o.HeartbeatIntervalSeconds, o.PollTimeoutSeconds, o.MaxLogBytes, o.MaxLogBatchBytes, o.MaxRunMinutes));
    }

    private static async Task<IResult> HeartbeatAsync(
        RunnerHeartbeatRequest? request, HttpContext http, AutomationDbContext db, IOptions<AutomationOptions> options,
        TimeProvider clock, CancellationToken cancellationToken)
    {
        if (CapabilitiesErrors(request?.Capabilities) is { Count: > 0 } errors)
        {
            return Results.ValidationProblem(errors, type: ProblemTypes.Validation);
        }

        var runnerId = RunnerId(http);
        var now = clock.GetUtcNow();
        var cutoff = now - options.Value.LastSeenThrottle;
        var capabilities = Serialize(request?.Capabilities);
        // The throttle is in the WHERE clause, not an if: concurrent heartbeats from one runner
        // must not each decide to write. Changed capabilities always get through.
        await db.Database.ExecuteSqlAsync($"""
            UPDATE automation.runners
            SET last_seen_at = {now}, capabilities = COALESCE({capabilities}::jsonb, capabilities)
            WHERE id = {runnerId} AND disabled_at IS NULL AND deleted_at IS NULL
              AND (last_seen_at IS NULL OR last_seen_at < {cutoff}
                   OR ({capabilities}::jsonb IS NOT NULL AND capabilities IS DISTINCT FROM {capabilities}::jsonb))
            """, cancellationToken);
        return Results.NoContent();
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────

    internal static RunnerView ToView(
        Runner runner, IReadOnlyDictionary<string, UserSummary> people, AutomationOptions options, DateTimeOffset now) =>
        new(runner.Id, runner.Name, runner.Display, runner.RegisteredBy,
            people.TryGetValue(runner.RegisteredBy, out var person) ? person.DisplayName : null,
            runner.Capabilities, runner.LastSeenAt,
            IsOnline: IsOnline(runner, options, now),
            IsDisabled: runner.DisabledAt is not null,
            runner.CreatedAt);

    private static bool IsOnline(Runner runner, AutomationOptions options, DateTimeOffset now) =>
        runner.IsUsable && runner.LastSeenAt is { } seen && now - seen <= options.OnlineWindow;

    private static Guid RunnerId(HttpContext http) =>
        // The policy requires the claim, so a parse failure is a bug rather than a request to refuse.
        Guid.Parse(http.User.FindFirstValue(PrincipalClaims.Runner)!);

    private static string? NameError(string? name)
    {
        var trimmed = name?.Trim() ?? "";
        return trimmed.Length is 0 or > Runner.MaxNameLength
            ? "Name it after the machine - 'vps-1', 'ci-runner'. At most 100 characters."
            : null;
    }

    private static string? Serialize(RunnerCapabilities? capabilities) =>
        capabilities is null ? null : JsonSerializer.Serialize(capabilities, AutomationDbContext.CapabilitiesJson);

    private static Dictionary<string, string[]> CapabilitiesErrors(RunnerCapabilities? capabilities)
    {
        var errors = new Dictionary<string, string[]>();
        if (capabilities is null)
        {
            return errors;
        }

        if (capabilities.V != 1)
        {
            errors["capabilities.v"] = ["This server understands capabilities version 1."];
        }
        if (capabilities.Harnesses is null || capabilities.Harnesses.Count > 16
            || capabilities.Harnesses.Any(h => h is null || !HarnessName().IsMatch(h.Name ?? "") || h.Version is { Length: > 100 }))
        {
            errors["capabilities.harnesses"] = ["At most 16 harnesses, each named in lower case (claude, codex, opencode) with a version of at most 100 characters."];
        }
        if (capabilities.Os is { Length: > 64 } || capabilities.Arch is { Length: > 64 } || capabilities.CliVersion is { Length: > 64 })
        {
            errors["capabilities"] = ["os, arch and cliVersion are at most 64 characters."];
        }
        if (capabilities.MaxParallel is < 1 or > 64)
        {
            errors["capabilities.maxParallel"] = ["Between 1 and 64."];
        }
        if (capabilities.MachineId is { } machineId && !Guid.TryParseExact(machineId, "D", out _))
        {
            errors["capabilities.machineId"] = ["A UUID, the same for every organization this machine is registered with."];
        }

        return errors;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,31}$")]
    internal static partial Regex HarnessName();

    private static IResult NotFound() =>
        Results.Problem(
            title: "Not found.",
            detail: "The record does not exist, or you do not have access to it.",
            type: ProblemTypes.NotAMember,
            statusCode: StatusCodes.Status404NotFound);
}

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

/// <param name="TokenDisplay"><c>jrn_a1b2c3d4…</c> — all that survives of the secret.</param>
/// <param name="IsOnline">Seen within two heartbeats. Decided here so every client agrees on what "online" means.</param>
public sealed record RunnerView(
    Guid Id, string Name, string TokenDisplay, string RegisteredBy, string? RegisteredByName,
    RunnerCapabilities? Capabilities, DateTimeOffset? LastSeenAt, bool IsOnline, bool IsDisabled,
    DateTimeOffset CreatedAt);

/// <param name="Secret">Returned exactly once. Aictiq keeps only its hash.</param>
public sealed record RunnerIssuedView(RunnerView Runner, string Secret);

public sealed record CreateRunnerRequest(string? Name);

/// <summary>
/// No <c>version</c>, deliberately — the documented exception to the xmin round-trip, like a
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

    private static async Task<IResult> CreateAsync(
        string orgSlug, CreateRunnerRequest request, AutomationDbContext db, ICurrentTenant tenant, ICurrentUser user,
        IUserDirectory directory, IOptions<AutomationOptions> options, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (NameError(request.Name) is { } error)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["name"] = [error] }, type: ProblemTypes.Validation);
        }

        var now = clock.GetUtcNow();
        var runner = Runner.Register(tenant.OrganizationId!.Value, request.Name!.Trim(), user.UserId!, now, out var secret);
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
            IsOnline: runner.IsUsable && runner.LastSeenAt is { } seen && now - seen <= options.OnlineWindow,
            IsDisabled: runner.DisabledAt is not null,
            runner.CreatedAt);

    private static Guid RunnerId(HttpContext http) =>
        // The policy requires the claim, so a parse failure is a bug rather than a request to refuse.
        Guid.Parse(http.User.FindFirstValue(PrincipalClaims.Runner)!);

    private static string? NameError(string? name)
    {
        var trimmed = name?.Trim() ?? "";
        return trimmed.Length is 0 or > Runner.MaxNameLength
            ? "Name it after the machine — 'vps-1', 'ci-runner'. At most 100 characters."
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

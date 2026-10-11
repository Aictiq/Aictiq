using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Aictiq.Modules.Automation.Auth;
using Aictiq.Modules.Automation.Domain;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Http;
using Aictiq.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Aictiq.Modules.Automation.Endpoints;

/// <summary>What anyone who may start a run needs to know about fleet mode: whether to show tag fields.</summary>
/// <param name="Availability"><c>hidden</c> (self-hosted: not offered yet), <c>upgrade</c> (needs a paid hosted plan) or <c>available</c>.</param>
/// <param name="Active">Fleet rules are in force: enabled, and the plan allows it.</param>
/// <param name="Downgraded">Enabled, but the plan no longer allows it: runs dispatch as if it were off.</param>
/// <param name="KnownTags">Every tag a runner of the organization has ever offered.</param>
public sealed record FleetStatusView(string Availability, bool Enabled, bool Active, bool Downgraded, IReadOnlyList<string> KnownTags);

/// <summary>The Admin's view of fleet mode. Secrets are never returned here, only whether they exist.</summary>
public sealed record FleetSettingsView(
    string Availability, bool Enabled, bool Active, bool Downgraded,
    string? ScaleWebhookUrl, bool HasWebhookSecret, int ScaleAfterSeconds, int ScaleCooldownSeconds,
    DateTimeOffset? LastScaleCallAt, string? LastScaleCallError, string? EnrollTokenDisplay);

/// <param name="ScaleWebhookUrl">An HTTPS URL, or an empty string to turn scale-from-zero off; null leaves it.</param>
public sealed record UpdateFleetRequest(
    bool? Enabled, string? ScaleWebhookUrl = null, int? ScaleAfterSeconds = null, int? ScaleCooldownSeconds = null);

/// <param name="WebhookSecret">Set once, when this save minted the webhook's signing secret.</param>
public sealed record FleetSettingsSaved(FleetSettingsView Settings, string? WebhookSecret);

/// <summary>A secret shown exactly once.</summary>
public sealed record FleetSecretIssued(string Secret, string? Display);

/// <param name="Name">The runner's name; a random <c>ephemeral-…</c> one when omitted.</param>
public sealed record RunnerEnrollRequest(string? Name, IReadOnlyList<string>? Tags);

public sealed record RunnerEnrolledView(Guid RunnerId, string Name, string Secret);

/// <summary>
/// Fleet mode: the organization switch and its scale-from-zero settings (<c>/orgs/{slug}/fleet</c>),
/// and the two protocol routes of an ephemeral runner - enrolling with the fleet's token and
/// removing itself once idle.
/// </summary>
public static class FleetEndpoints
{
    public static IEndpointRouteBuilder MapFleetEndpoints(this IEndpointRouteBuilder api)
    {
        var fleet = api.MapGroup("/orgs/{orgSlug}/fleet").WithTags("Fleet").RequireAuthorization();
        fleet.MapGet("/status", StatusAsync).RequireOrgRole(OrgRole.Member).RequireScope(Scopes.Read);
        fleet.MapGet("/", GetAsync).RequireOrgRole(OrgRole.Admin).RequireScope(Scopes.Read);
        fleet.MapPut("/", UpdateAsync).RequireOrgRole(OrgRole.Admin).RequireScope(Scopes.Admin);
        fleet.MapPost("/webhook-secret", RotateWebhookSecretAsync).RequireOrgRole(OrgRole.Admin).RequireScope(Scopes.Admin);
        // An enrollment token mints runner credentials, so it is the admin scope's like registering a runner.
        fleet.MapPost("/enroll-token", RotateEnrollTokenAsync).RequireOrgRole(OrgRole.Admin).RequireScope(Scopes.Admin);
        fleet.MapDelete("/enroll-token", RevokeEnrollTokenAsync).RequireOrgRole(OrgRole.Admin).RequireScope(Scopes.Admin);

        // The token decides the tenant, like a runner secret does; the auth rate limit keeps it from being guessed at.
        api.MapPost("/runner/enroll", EnrollAsync).WithTags("Runner protocol").AllowAnonymous().RequireRateLimiting("auth");
        api.MapPost("/runner/deregister", DeregisterAsync).WithTags("Runner protocol")
            .RequireAuthorization(RunnerDefaults.Policy);
        return api;
    }

    private static async Task<IResult> StatusAsync(
        ICurrentTenant tenant, AutomationDbContext db, FleetMode fleetMode, IPlanLimits plans, CancellationToken ct)
    {
        var organizationId = tenant.OrganizationId!.Value;
        var availability = await plans.GetFleetModeAvailabilityAsync(organizationId, ct);
        var enabled = await db.FleetSettings.AsNoTracking().AnyAsync(row => row.Enabled, ct);
        var state = new FleetState(enabled, availability == FleetModeAvailability.Available);
        return Results.Ok(new FleetStatusView(
            AvailabilityName(availability), state.Enabled, state.Active, state.Downgraded,
            state.Active ? await fleetMode.KnownTagsAsync(ct) : []));
    }

    private static async Task<IResult> GetAsync(
        ICurrentTenant tenant, AutomationDbContext db, IPlanLimits plans, CancellationToken ct)
    {
        var availability = await plans.GetFleetModeAvailabilityAsync(tenant.OrganizationId!.Value, ct);
        var settings = await db.FleetSettings.AsNoTracking().SingleOrDefaultAsync(ct);
        return Results.Ok(ToView(settings, availability));
    }

    private static async Task<IResult> UpdateAsync(
        UpdateFleetRequest request, ICurrentTenant tenant, AutomationDbContext db, IPlanLimits plans,
        FleetSecrets secrets, IOptions<AutomationOptions> options, TimeProvider clock, CancellationToken ct)
    {
        var organizationId = tenant.OrganizationId!.Value;
        var availability = await plans.GetFleetModeAvailabilityAsync(organizationId, ct);
        if (availability == FleetModeAvailability.Hidden)
        {
            // Not offered on self-hosted installs yet; the route answers like one that does not exist.
            return NotFound();
        }

        if (request.Enabled == true && availability != FleetModeAvailability.Available)
        {
            return Results.Problem(
                title: "Plan limit reached.",
                detail: "Fleet mode is part of the paid Hosted plan. Upgrade this organization to turn it on.",
                type: ProblemTypes.PlanLimit,
                statusCode: StatusCodes.Status402PaymentRequired,
                extensions: new Dictionary<string, object?> { ["limit"] = "fleet_mode" });
        }

        var errors = new Dictionary<string, string[]>();
        if (request.ScaleAfterSeconds is < 30 or > 86_400)
        {
            errors["scaleAfterSeconds"] = ["Between 30 seconds and 24 hours."];
        }
        if (request.ScaleCooldownSeconds is < 60 or > 86_400)
        {
            errors["scaleCooldownSeconds"] = ["Between 1 minute and 24 hours."];
        }
        var url = request.ScaleWebhookUrl?.Trim();
        if (url is { Length: > 0 }
            && await WebhookUrlErrorAsync(url, options.Value.FleetWebhookAllowPrivateNetworks, ct) is { } urlError)
        {
            errors["scaleWebhookUrl"] = [urlError];
        }
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors, type: ProblemTypes.Validation);
        }

        var now = clock.GetUtcNow();
        var settings = await db.FleetSettings.SingleOrDefaultAsync(ct);
        if (settings is null)
        {
            settings = new FleetSettings { OrganizationId = organizationId };
            db.FleetSettings.Add(settings);
        }

        if (request.Enabled is { } enabled)
        {
            settings.Enabled = enabled;
        }
        settings.ScaleAfterSeconds = request.ScaleAfterSeconds ?? settings.ScaleAfterSeconds;
        settings.ScaleCooldownSeconds = request.ScaleCooldownSeconds ?? settings.ScaleCooldownSeconds;

        string? minted = null;
        if (url is not null)
        {
            settings.ScaleWebhookUrl = url.Length == 0 ? null : url;
            if (settings.ScaleWebhookUrl is not null && settings.ScaleWebhookSecretProtected is null)
            {
                minted = FleetSecrets.CreateSecret();
                settings.ScaleWebhookSecretProtected = secrets.Protect(minted);
            }
            settings.LastScaleCallError = null;
        }
        settings.UpdatedAt = now;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Two first saves raced; the other one created the row.
            return Results.Problem(title: "Conflict.", detail: "Fleet settings were saved by someone else. Reload and try again.",
                type: ProblemTypes.Conflict, statusCode: StatusCodes.Status409Conflict);
        }

        return Results.Ok(new FleetSettingsSaved(ToView(settings, availability), minted));
    }

    private static async Task<IResult> RotateWebhookSecretAsync(
        AutomationDbContext db, FleetSecrets secrets, TimeProvider clock, CancellationToken ct)
    {
        var settings = await db.FleetSettings.SingleOrDefaultAsync(ct);
        if (settings?.ScaleWebhookUrl is null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["scaleWebhookUrl"] = ["Set a webhook URL first."],
            }, type: ProblemTypes.Validation);
        }

        var secret = FleetSecrets.CreateSecret();
        settings.ScaleWebhookSecretProtected = secrets.Protect(secret);
        settings.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return Results.Ok(new FleetSecretIssued(secret, null));
    }

    private static async Task<IResult> RotateEnrollTokenAsync(
        ICurrentTenant tenant, AutomationDbContext db, FleetMode fleetMode, TimeProvider clock, CancellationToken ct)
    {
        var organizationId = tenant.OrganizationId!.Value;
        if (!(await fleetMode.GetAsync(organizationId, ct)).Active)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["enabled"] = ["Turn fleet mode on first."],
            }, type: ProblemTypes.Validation);
        }

        var settings = await db.FleetSettings.SingleAsync(ct);
        var token = FleetEnrollToken.Generate(organizationId);
        settings.EnrollTokenHash = FleetEnrollToken.Hash(token);
        settings.EnrollTokenPrefix = FleetEnrollToken.PrefixOf(token);
        settings.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return Results.Ok(new FleetSecretIssued(token, FleetEnrollToken.Display(settings.EnrollTokenPrefix)));
    }

    private static async Task<IResult> RevokeEnrollTokenAsync(AutomationDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var settings = await db.FleetSettings.SingleOrDefaultAsync(ct);
        if (settings is not null)
        {
            settings.EnrollTokenHash = null;
            settings.EnrollTokenPrefix = null;
            settings.UpdatedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
        }
        return Results.NoContent();
    }

    // ── protocol ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// An ephemeral runner registers itself with the fleet's enrollment token. Every refusal is
    /// the same 401, so a guesser learns nothing about which part was wrong.
    /// </summary>
    private static async Task<IResult> EnrollAsync(
        RunnerEnrollRequest? request, HttpContext http, AutomationDbContext db, AmbientCurrentTenant tenant,
        FleetMode fleetMode, TimeProvider clock, CancellationToken ct)
    {
        if (BearerCredentialRoute.ReadBearer(http.Request) is not { } bearer
            || !FleetEnrollToken.TryParse(bearer, out var organizationId))
        {
            return Unauthorized();
        }

        var tags = FleetTags.Normalize(request?.Tags);
        var name = string.IsNullOrWhiteSpace(request?.Name)
            ? $"ephemeral-{Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant()}"
            : request.Name.Trim();
        var errors = new Dictionary<string, string[]>();
        if (tags is null)
        {
            errors["tags"] = [FleetTags.Rule];
        }
        if (name.Length > Runner.MaxNameLength)
        {
            errors["name"] = ["At most 100 characters."];
        }

        using var scope = tenant.Use(organizationId);
        var settings = await db.FleetSettings.AsNoTracking().SingleOrDefaultAsync(ct);
        if (settings?.EnrollTokenHash is not { } expected
            || !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(FleetEnrollToken.Hash(bearer)))
            || !(await fleetMode.GetAsync(organizationId, ct)).Active)
        {
            return Unauthorized();
        }
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors, type: ProblemTypes.Validation);
        }

        var now = clock.GetUtcNow();
        var runner = Runner.Register(organizationId, name, $"fleet:{settings.EnrollTokenPrefix}", now, out var secret);
        runner.Ephemeral = true;
        runner.Tags = tags!;
        db.Runners.Add(runner);
        // A name another live runner already has is a unique violation → 409.
        await db.SaveChangesAsync(ct);
        return Results.Ok(new RunnerEnrolledView(runner.Id, runner.Name, secret));
    }

    /// <summary>An ephemeral runner that has been idle long enough removes itself, so the roster does not fill with dead machines.</summary>
    private static async Task<IResult> DeregisterAsync(
        HttpContext http, AutomationDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var runnerId = Guid.Parse(http.User.FindFirstValue(PrincipalClaims.Runner)!);
        var runner = await db.Runners.SingleOrDefaultAsync(r => r.Id == runnerId, ct);
        if (runner is null)
        {
            return NotFound();
        }
        if (!runner.Ephemeral)
        {
            return Results.Problem(title: "Conflict.", detail: "Only an ephemeral runner removes itself. An Admin removes this one on Factory → Runners.",
                type: ProblemTypes.Conflict, statusCode: StatusCodes.Status409Conflict);
        }

        // Its live runs are the sweeper's: a run it still held is failed as a lost runner's would be.
        var now = clock.GetUtcNow();
        runner.DeletedAt = now;
        runner.DisabledAt ??= now;
        runner.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────

    internal static string AvailabilityName(FleetModeAvailability availability) => availability switch
    {
        FleetModeAvailability.Available => "available",
        FleetModeAvailability.UpgradeRequired => "upgrade",
        _ => "hidden",
    };

    private static FleetSettingsView ToView(FleetSettings? settings, FleetModeAvailability availability)
    {
        var state = new FleetState(settings?.Enabled ?? false, availability == FleetModeAvailability.Available);
        return new FleetSettingsView(
            AvailabilityName(availability), state.Enabled, state.Active, state.Downgraded,
            settings?.ScaleWebhookUrl, settings?.ScaleWebhookSecretProtected is not null,
            settings?.ScaleAfterSeconds ?? 120, settings?.ScaleCooldownSeconds ?? 600,
            settings?.LastScaleCallAt, settings?.LastScaleCallError,
            settings?.EnrollTokenPrefix is { } prefix ? FleetEnrollToken.Display(prefix) : null);
    }

    private static async Task<string?> WebhookUrlErrorAsync(string url, bool allowPrivateNetworks, CancellationToken ct)
    {
        if (url.Length > FleetSettings.MaxWebhookUrlLength
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return "An absolute HTTPS URL of at most 2048 characters, without credentials in it.";
        }
        if (allowPrivateNetworks)
        {
            return null;
        }
        return (await PublicNetworkGuard.ResolvePublicAsync(uri.DnsSafeHost, ct)).Length == 0
            ? "The webhook URL must resolve to a public address."
            : null;
    }

    private static IResult Unauthorized() =>
        Results.Problem(title: "Unauthorized.", detail: "The enrollment token is not valid, or fleet mode is off.",
            statusCode: StatusCodes.Status401Unauthorized);

    private static IResult NotFound() =>
        Results.Problem(
            title: "Not found.",
            detail: "The record does not exist, or you do not have access to it.",
            type: ProblemTypes.NotAMember,
            statusCode: StatusCodes.Status404NotFound);
}

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Aictiq.Modules.Tenancy.Access;
using Aictiq.Modules.Tenancy.Domain;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Outbox;
using Aictiq.SharedKernel.Tenancy;

namespace Aictiq.Modules.Tenancy.Endpoints;

/// <summary>One row of the organization switcher.</summary>
/// <param name="CanOperateFactory">Whether the caller may start AI runs here; what the shell hides the Factory on.</param>
public sealed record OrganizationSummary(Guid Id, string Slug, string Name, OrgRole Role, bool CanOperateFactory);

/// <summary>Whether a person with no memberships may start a new organization on this host.</summary>
public sealed record OrganizationCreationPolicy(bool CanCreateOrganization);

/// <param name="CanOperateFactory">
/// Whether the caller may start and watch AI runs here. The SPA hides the factory
/// from a stakeholder on this; the API refuses them regardless.
/// </param>
public sealed record OrganizationView(
    Guid Id, string Slug, string Name, string Plan, string TimeZone, DayOfWeek WeekStart,
    bool MembersCanCreateProjects, OrgRole Role, DateTimeOffset CreatedAt, uint Version,
    bool CanOperateFactory);

/// <param name="Slug">Optional — derived from the name, uniquified, when omitted.</param>
public sealed record CreateOrganizationRequest(
    string? Name, string? Slug, string? TimeZone, DayOfWeek? WeekStart);

public sealed record UpdateOrganizationRequest(
    string? Name, string? TimeZone, DayOfWeek? WeekStart, bool? MembersCanCreateProjects, uint Version);

/// <param name="Name">
/// The organization's name, typed out. Deleting takes the whole team's work with it, so
/// it asks for something that cannot be produced by a mis-click.
/// </param>
public sealed record DeleteOrganizationRequest(string? Name);

public static class OrganizationEndpoints
{
    public static IEndpointRouteBuilder MapOrganizationEndpoints(this IEndpointRouteBuilder api)
    {
        MapMine(api);
        MapCreationPolicy(api);
        MapCreate(api);
        MapScoped(api);
        return api;
    }

    private static void MapCreationPolicy(IEndpointRouteBuilder api)
    {
        // The flag is deliberately the whole response: revealing the seeded organization's
        // name or address to a non-member would turn the welcome screen into enumeration.
        api.MapGet("/orgs/creation-policy", (IConfiguration configuration) =>
            Results.Ok(new OrganizationCreationPolicy(AllowsSelfServeCreation(configuration))))
            .WithTags("Organizations")
            .RequireAuthorization()
            .RequireScope(Scopes.Read);
    }

    private static void MapMine(IEndpointRouteBuilder api)
    {
        api.MapGet("/orgs", async (
            TenancyDbContext db,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            // Cross-tenant by definition — "which organizations am I in" has no single
            // organization to be scoped to, and it runs before one is chosen. One of the
            // handful of deliberate IgnoreQueryFilters calls in the codebase; the
            // user-id predicate is what keeps it safe.
            // A token bound to one organization answers for that one: listing the others
            // would tell whoever holds an agent's or CI's credential where else its owner works.
            var userId = user.UserId;
            var bound = user.OrganizationId;
            var organizations = await (
                from membership in db.Members.IgnoreQueryFilters().AsNoTracking()
                join organization in db.Organizations on membership.OrganizationId equals organization.Id
                where membership.UserId == userId && (bound == null || membership.OrganizationId == bound)
                orderby organization.Name
                select new OrganizationSummary(
                    organization.Id, organization.Slug, organization.Name, membership.Role,
                    MembershipRules.CanOperateFactory(membership.Role, membership.CanOperateFactory)))
                .ToListAsync(cancellationToken);

            return Results.Ok(organizations);
        })
        .WithTags("Organizations")
        .RequireAuthorization()
        .RequireScope(Scopes.Read);
    }

    private static void MapCreate(IEndpointRouteBuilder api)
    {
        api.MapPost("/orgs", async (
            CreateOrganizationRequest request,
            TenancyDbContext db,
            AmbientCurrentTenant tenant,
            ICurrentUser user,
            HybridCache cache,
            NpgsqlDataSource dataSource,
            IConfiguration configuration,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            if (!AllowsSelfServeCreation(configuration))
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status403Forbidden,
                    title: "Organization creation is disabled",
                    detail: "Ask an administrator for an invitation.",
                    type: ProblemTypes.OrganizationCreationDisabled);
            }

            var name = request.Name?.Trim() ?? "";
            var errors = new Dictionary<string, string[]>();

            if (name.Length == 0)
            {
                errors["name"] = ["Name is required."];
            }
            else if (name.Length > 200)
            {
                errors["name"] = ["Name must be 200 characters or fewer."];
            }

            var settings = ReadSettings(
                request.TimeZone, request.WeekStart, null, OrganizationSettings.Default, errors);

            string? slug = null;
            if (!string.IsNullOrWhiteSpace(request.Slug))
            {
                slug = request.Slug.Trim().ToLowerInvariant();
                if (!Slug.IsWellFormed(slug))
                {
                    errors["slug"] =
                    [
                        $"Use {Slug.MinLength}-{Slug.MaxLength} lowercase letters, digits and single hyphens."
                    ];
                }
                else if (Slug.IsReserved(slug))
                {
                    errors["slug"] = ["That address is reserved."];
                }
                else if (await db.Organizations.AnyAsync(o => o.Slug == slug, cancellationToken)
                    || await db.RetiredSlugs.AnyAsync(r => r.Slug == slug, cancellationToken))
                {
                    errors["slug"] = ["That address is already taken."];
                }
            }

            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors, type: ProblemTypes.Validation);
            }

            slug ??= await DeriveSlugAsync(db, name, cancellationToken);

            var now = timeProvider.GetUtcNow();
            var organization = Organization.Create(slug, name, user.UserId!, settings, now);
            var owner = OrganizationMember.Create(organization.Id, user.UserId!, OrgRole.Owner, now);

            // The organization, its first membership and the OrganizationCreated outbox
            // row commit together: an organization no one owns, or an event about one
            // that does not exist, are both states nothing downstream can handle.
            //
            // The tenant scope is what lets the membership be written at all — it is a
            // TenantEntity, and SaveChangesAsync refuses to stamp one without a tenant.
            // Establishing the tenant we are in the act of creating is the honest way to
            // say that, rather than exempting the table from the rule.
            using (tenant.Use(organization.Id))
            {
                db.Organizations.Add(organization);
                db.Members.Add(owner);
                await db.SaveChangesAsync(cancellationToken);
            }

            await TenancyCache.InvalidateAsync(cache, dataSource, organization.Id, slug, cancellationToken);

            return Results.Created($"/api/v1/orgs/{slug}", ToView(organization, OrgRole.Owner, true));
        })
        .WithTags("Organizations")
        .RequireAuthorization()
        .RequireScope(Scopes.Admin);
    }

    /// <summary>Defaults to open creation so existing self-hosts keep their current behavior.</summary>
    private static bool AllowsSelfServeCreation(IConfiguration configuration) =>
        configuration.GetValue<bool?>("Org:AllowSelfServeCreation") ?? true;

    private static void MapScoped(IEndpointRouteBuilder api)
    {
        // Every route here carries {orgSlug}, which is what TenantResolutionMiddleware
        // reads: by the time an endpoint runs the organization exists, the caller is a
        // member of it, and ICurrentTenant names it. A non-member never gets this far —
        // they get a 404 that says nothing about whether the slug is in use.
        var group = api.MapGroup("/orgs/{orgSlug}")
            .WithTags("Organizations")
            .RequireAuthorization();

        group.MapGet("/", async (
            TenancyDbContext db,
            ICurrentTenant tenant,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            var organization = await db.Organizations
                .AsNoTracking()
                .FirstOrDefaultAsync(o => o.Id == tenant.OrganizationId, cancellationToken);

            if (organization is null)
            {
                return TenancyResults.NotFound();
            }

            var membership = await db.Members.AsNoTracking()
                .Where(m => m.UserId == user.UserId)
                .Select(m => new { m.Role, m.CanOperateFactory })
                .FirstOrDefaultAsync(cancellationToken);

            return membership is null
                ? TenancyResults.NotFound()
                : Results.Ok(ToView(organization, membership.Role,
                    MembershipRules.CanOperateFactory(membership.Role, membership.CanOperateFactory)));
        })
        .RequireOrgRole(OrgRole.Guest)
        .RequireScope(Scopes.Read);

        group.MapPatch("/", async (
            UpdateOrganizationRequest request,
            TenancyDbContext db,
            ICurrentTenant tenant,
            ICurrentUser user,
            HybridCache cache,
            NpgsqlDataSource dataSource,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var organization = await db.Organizations
                .FirstOrDefaultAsync(o => o.Id == tenant.OrganizationId, cancellationToken);

            if (organization is null)
            {
                return TenancyResults.NotFound();
            }

            var errors = new Dictionary<string, string[]>();
            var name = request.Name?.Trim();
            if (name is not null)
            {
                if (name.Length == 0)
                {
                    errors["name"] = ["Name is required."];
                }
                else if (name.Length > 200)
                {
                    errors["name"] = ["Name must be 200 characters or fewer."];
                }
            }

            var settings = ReadSettings(
                request.TimeZone, request.WeekStart, request.MembersCanCreateProjects,
                organization.Settings, errors);

            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors, type: ProblemTypes.Validation);
            }

            // The client echoes the xmin it read; a stale one throws
            // DbUpdateConcurrencyException, which GlobalExceptionHandler turns into 409.
            db.Entry(organization).Property(o => o.Version).OriginalValue = request.Version;

            var now = timeProvider.GetUtcNow();
            if (name is not null)
            {
                organization.Rename(name, now);
            }
            organization.UpdateSettings(settings, now);

            await db.SaveChangesAsync(cancellationToken);
            await TenancyCache.InvalidateAsync(cache, dataSource, organization.Id, organization.Slug, cancellationToken);

            var role = await db.Members.AsNoTracking()
                .Where(m => m.UserId == user.UserId)
                .Select(m => (OrgRole?)m.Role)
                .FirstOrDefaultAsync(cancellationToken);

            // Only Admins and Owners reach this, and they always operate the factory.
            return Results.Ok(ToView(organization, role ?? OrgRole.Admin, true));
        })
        .RequireOrgRole(OrgRole.Admin)
        .RequireScope(Scopes.Write);

        group.MapDelete("/", async (
            // Explicit: minimal APIs will not infer a body on DELETE. The confirmation
            // has to travel somewhere, and a query string would put the organization's
            // name in every access log between here and the browser.
            [FromBody] DeleteOrganizationRequest request,
            TenancyDbContext db,
            ICurrentTenant tenant,
            ICurrentUser user,
            IUserDirectory directory,
            HybridCache cache,
            NpgsqlDataSource dataSource,
            CancellationToken cancellationToken) =>
        {
            var organization = await db.Organizations.AsNoTracking()
                .FirstOrDefaultAsync(o => o.Id == tenant.OrganizationId, cancellationToken);

            if (organization is null)
            {
                return TenancyResults.NotFound();
            }

            if (!string.Equals(request.Name?.Trim(), organization.Name, StringComparison.OrdinalIgnoreCase))
            {
                return Results.ValidationProblem(
                    new Dictionary<string, string[]>
                    {
                        ["name"] = ["Type the organization's name exactly to confirm deletion."]
                    },
                    type: ProblemTypes.Validation);
            }

            // Agents are created inside one organization and cannot join another, so an
            // agent on this roster is answerable to nobody once it is gone: its account goes
            // too. People keep theirs.
            var memberIds = await db.Members.AsNoTracking().Select(m => m.UserId).ToListAsync(cancellationToken);
            var agentIds = await directory.FilterAgentsAsync(memberIds, cancellationToken);

            // Tenancy's rows go now, in one statement: memberships, invitations, projects,
            // teams and their memberships all cascade from the organization row, and the
            // last-Owner trigger lets a cascade through. The slug is retired in the same
            // transaction, so there is no moment in which it is free. Every other module
            // finishes from the outbox row committed alongside.
            await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
            {
                db.RetiredSlugs.Add(new RetiredSlug { Slug = organization.Slug });
                db.Set<OutboxMessage>().Add(OutboxMessage.From(new OrganizationDeleted(
                    organization.Id, organization.Slug, [.. agentIds], user.UserId!)));
                await db.SaveChangesAsync(cancellationToken);
                await db.Database.ExecuteSqlAsync(
                    $"DELETE FROM tenancy.organizations WHERE id = {organization.Id}", cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }

            // Until this lands in every instance, a cached membership would keep the
            // organization reachable — so the eviction is not an optimisation here.
            await TenancyCache.InvalidateAsync(cache, dataSource, organization.Id, organization.Slug, cancellationToken);

            return Results.NoContent();
        })
        .RequireOrgRole(OrgRole.Owner)
        .RequireScope(Scopes.Admin);
    }

    /// <summary>
    /// Turns a display name into a free slug. The counter is bounded because a loop that
    /// asks the database forever is worse than a slug with a suffix on it.
    /// </summary>
    private static async Task<string> DeriveSlugAsync(
        TenancyDbContext db, string name, CancellationToken cancellationToken)
    {
        var stem = Slug.From(name);
        if (stem.Length < Slug.MinLength)
        {
            // Not "org": it is a reserved slug, so the loop below would immediately
            // reject it and every unsluggable name would come out as "org-2".
            stem = "workspace";
        }

        var candidate = stem;
        for (var suffix = 2; suffix <= 50; suffix++)
        {
            if (!Slug.IsReserved(candidate)
                && !await db.Organizations.AnyAsync(o => o.Slug == candidate, cancellationToken)
                && !await db.RetiredSlugs.AnyAsync(r => r.Slug == candidate, cancellationToken))
            {
                return candidate;
            }

            var room = Slug.MaxLength - (suffix.ToString().Length + 1);
            candidate = $"{stem[..Math.Min(stem.Length, room)].TrimEnd('-')}-{suffix}";
        }

        // Fifty collisions on one stem: stop guessing and take a name nobody will race.
        return $"{stem[..Math.Min(stem.Length, Slug.MaxLength - 7)].TrimEnd('-')}-{Random.Shared.Next(0x1000, 0xFFFF):x}";
    }

    private static OrganizationSettings ReadSettings(
        string? timeZone, DayOfWeek? weekStart, bool? membersCanCreateProjects,
        OrganizationSettings current, Dictionary<string, string[]> errors)
    {
        var resolved = current;

        if (membersCanCreateProjects is { } canCreate)
        {
            resolved = resolved with { MembersCanCreateProjects = canCreate };
        }

        if (timeZone is not null)
        {
            if (!OrganizationSettings.IsKnownTimeZone(timeZone))
            {
                errors["timeZone"] = ["Unknown time zone. Use an IANA identifier, e.g. Europe/Sarajevo."];
            }
            else
            {
                resolved = resolved with { TimeZone = timeZone };
            }
        }

        if (weekStart is { } day)
        {
            if (!Enum.IsDefined(day))
            {
                errors["weekStart"] = ["Not a day of the week."];
            }
            else
            {
                resolved = resolved with { WeekStart = day };
            }
        }

        return resolved;
    }

    private static OrganizationView ToView(Organization organization, OrgRole role, bool canOperateFactory) => new(
        organization.Id, organization.Slug, organization.Name, organization.Plan,
        organization.Settings.TimeZone, organization.Settings.WeekStart,
        organization.Settings.MembersCanCreateProjects, role,
        organization.CreatedAt, organization.Version, canOperateFactory);
}

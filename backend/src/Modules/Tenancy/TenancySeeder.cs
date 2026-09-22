using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Aictiq.Modules.Tenancy.Domain;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Tenancy;

namespace Aictiq.Modules.Tenancy;

/// <summary>
/// First-run seeding: gives a fresh installation the organization its seeded
/// administrator owns, so `docker compose up` lands on a usable app instead of an empty
/// state that cannot be left without creating one by hand.
///
/// <para>
/// First-run means exactly that: it does nothing once <b>any</b> organization exists.
/// Keying on "is this specific slug missing" would recreate a deliberately deleted
/// organization on the next restart.
/// </para>
///
/// <para>
/// The owner's user id is passed in rather than looked up. Tenancy has no business
/// knowing how Identity stores people - modules meet through contracts in SharedKernel,
/// and the composition root is the one place that already knows both.
/// </para>
/// </summary>
public static class TenancySeeder
{
    public static async Task SeedAsync(
        IServiceProvider services, string? ownerUserId, CancellationToken cancellationToken = default)
    {
        using var scope = services.CreateScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("TenancySeeder");
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();

        var name = configuration["Seed:OrganizationName"];
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        if (string.IsNullOrEmpty(ownerUserId))
        {
            logger.LogWarning(
                "Seed:OrganizationName is set but no seeded administrator exists - " +
                "set Seed:AdminEmail and Seed:AdminPassword too. Skipping organization seeding.");
            return;
        }

        var db = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        if (await db.Organizations.AnyAsync(cancellationToken))
        {
            return;
        }

        var slug = configuration["Seed:OrganizationSlug"]?.Trim().ToLowerInvariant() is { Length: > 0 } configured
            ? configured
            : Slug.From(name);

        if (!Slug.IsWellFormed(slug) || Slug.IsReserved(slug))
        {
            logger.LogError(
                "Seed:OrganizationSlug '{Slug}' is not a usable organization address; skipping seeding", slug);
            return;
        }

        var timeProvider = scope.ServiceProvider.GetRequiredService<TimeProvider>();
        var now = timeProvider.GetUtcNow();

        var settings = configuration["Seed:OrganizationTimeZone"] is { Length: > 0 } timeZone
            && OrganizationSettings.IsKnownTimeZone(timeZone)
                ? OrganizationSettings.Default with { TimeZone = timeZone }
                : OrganizationSettings.Default;

        var organization = Organization.Create(slug, name.Trim(), ownerUserId, settings, now);
        var owner = OrganizationMember.Create(organization.Id, ownerUserId, OrgRole.Owner, now);

        var tenant = scope.ServiceProvider.GetRequiredService<AmbientCurrentTenant>();
        using (tenant.Use(organization.Id))
        {
            db.Organizations.Add(organization);
            db.Members.Add(owner);
            await db.SaveChangesAsync(cancellationToken);
        }

        logger.LogInformation("Seeded organization {Name} ({Slug})", organization.Name, organization.Slug);
    }
}

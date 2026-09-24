using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Aictiq.Modules.Identity.Domain;
using Aictiq.SharedKernel;

namespace Aictiq.Modules.Identity;

public static class IdentitySeeder
{
    /// <summary>
    /// Idempotent: ensures roles exist and creates the initial admin from configuration.
    /// </summary>
    /// <returns>
    /// The seeded administrator's user id, or null when none is configured. Returned
    /// rather than looked up again by the caller: the first-run organization needs an
    /// owner, and Tenancy must not learn how Identity stores people to find one.
    /// </returns>
    public static async Task<string?> SeedAsync(
        IServiceProvider services, CancellationToken cancellationToken = default)
    {
        using var scope = services.CreateScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("IdentitySeeder");

        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        foreach (var role in Roles.All)
        {
            if (!await roleManager.RoleExistsAsync(role))
            {
                await roleManager.CreateAsync(new IdentityRole(role));
            }
        }

        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var email = configuration["Seed:AdminEmail"];
        var password = configuration["Seed:AdminPassword"];
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            logger.LogInformation("Seed:AdminEmail/AdminPassword not configured - skipping admin creation");
            return null;
        }

        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        if (await userManager.FindByEmailAsync(email) is { } existing)
        {
            return existing.Id;
        }

        var timeProvider = scope.ServiceProvider.GetRequiredService<TimeProvider>();
        var admin = new ApplicationUser
        {
            UserName = email,
            Email = email,
            FirstName = configuration["Seed:AdminFirstName"] ?? "Administrator",
            LastName = configuration["Seed:AdminLastName"] ?? "",
            EmailConfirmed = true,
            CreatedAt = timeProvider.GetUtcNow()
        };

        var result = await userManager.CreateAsync(admin, password);
        if (!result.Succeeded)
        {
            logger.LogError("Creating the administrator failed: {Errors}",
                string.Join("; ", result.Errors.Select(e => e.Description)));
            return null;
        }

        await userManager.AddToRoleAsync(admin, Roles.Admin);
        logger.LogInformation("Created initial administrator");
        return admin.Id;
    }
}

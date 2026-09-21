using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Aictiq.Modules.Identity.Access;
using Aictiq.Modules.Identity.Auth;
using Aictiq.Modules.Identity.Domain;
using Aictiq.Modules.Identity.Endpoints;
using Aictiq.Modules.Identity.Workers;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Persistence;
using Aictiq.SharedKernel.Events;

namespace Aictiq.Modules.Identity;

public static class IdentityModule
{
    public static IServiceCollection AddIdentityModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<IdentityDbContext>("identity");

        services.AddIdentityCore<ApplicationUser>(options =>
        {
            options.User.RequireUniqueEmail = true;

            // NIST 800-63B: length over composition rules.
            options.Password.RequiredLength = 12;
            options.Password.RequireDigit = false;
            options.Password.RequireLowercase = false;
            options.Password.RequireUppercase = false;
            options.Password.RequireNonAlphanumeric = false;

            // Lockout is enforced because login uses CheckPasswordSignInAsync.
            options.Lockout.AllowedForNewUsers = true;
            options.Lockout.MaxFailedAccessAttempts = 5;
            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        })
        .AddRoles<IdentityRole>()
        .AddEntityFrameworkStores<IdentityDbContext>()
        .AddSignInManager()
        .AddDefaultTokenProviders();

        // SignInManager needs the authentication core (IAuthenticationSchemeProvider). The
        // API configures schemes on top; Workers load this module without any, and would
        // otherwise fail DI validation at start-up. TryAdd-based, so the API is unaffected.
        services.AddAuthentication();

        services.AddScoped<ITokenService, TokenService>();

        // Identity owns the people, so it answers "who is this id" for every other
        // module. Replaces SharedKernel's empty stub rather than stacking on top of it.
        services.RemoveAll<IUserDirectory>();
        services.AddScoped<IUserDirectory, IdentityUserDirectory>();
        services.RemoveAll<IExternalLoginLookup>();
        services.AddScoped<IExternalLoginLookup, IdentityExternalLoginLookup>();

        // Agents are accounts, so what one *is* belongs here; where it belongs is
        // Tenancy's. Replaces SharedKernel's fail-closed stub.
        services.RemoveAll<IAgentIdentities>();
        services.AddScoped<IAgentIdentities, IdentityAgentIdentities>();

        return services;
    }

    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapAuthEndpoints();
        api.MapExternalAuthEndpoints();
        api.MapCredentialEndpoints();
        api.MapProfileEndpoints();
        api.MapOnboardingEndpoints();
        api.MapSessionEndpoints();
        api.MapTokenEndpoints();
        api.MapUsersEndpoints();
        return api;
    }

    /// <summary>
    /// Workers-only registrations. Retention pruning lives here because Identity owns the
    /// tables that grow without bound (refresh tokens, the shared audit log and outbox).
    /// It needs no DbContext — raw SQL across three schemas — so nothing else is wired up.
    /// </summary>
    public static IServiceCollection AddIdentityWorkers(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<RetentionOptions>(configuration.GetSection(RetentionOptions.SectionName));
        services.AddHostedService<RetentionCleanupService>();
        services.AddScoped<IDomainEventHandler<OrganizationDeleted>, Events.OrganizationDeletedHandler>();
        services.AddScoped<IDomainEventHandler<ProjectDeleted>, Events.ProjectOutboxPurgeHandler>();
        services.AddScoped<IDomainEventHandler<WorkItemsDeleted>, Events.WorkItemsOutboxPurgeHandler>();
        return services;
    }
}

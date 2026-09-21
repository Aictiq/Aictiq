using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Aictiq.Modules.Tenancy.Access;
using Aictiq.Modules.Tenancy.Endpoints;
using Aictiq.Modules.Tenancy.Events;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Events;
using Aictiq.SharedKernel.Persistence;
using Aictiq.SharedKernel.Mcp;

namespace Aictiq.Modules.Tenancy;

public static class TenancyModule
{
    public static IServiceCollection AddTenancyModule(this IServiceCollection services)
    {
        services.AddModuleDbContext<TenancyDbContext>("tenancy");
        services.AddSingleton<IMcpToolProvider>(new McpToolProvider(typeof(TenancyModule).Assembly));

        // SharedKernel registers fail-closed stubs so the wiring is exercised before this
        // module exists. Remove them rather than layering on top: two registrations for
        // one interface is a coin toss for anything that resolves them as a collection.
        services.RemoveAll<IOrganizationLookup>();
        services.AddScoped<IOrganizationLookup, OrganizationLookup>();

        services.RemoveAll<IProjectAccess>();
        services.AddScoped<IProjectAccess, TenancyProjectAccess>();
        services.AddScoped<IOrganizationTimeZoneSource, OrganizationTimeZoneSource>();
        services.AddScoped<IOrganizationPlanUsageSource, TenancyPlanUsageSource>();

        // Outbox-only (integration events are never dispatched in-process), so registering
        // it here costs the API nothing and gives Workers the handler.
        services.AddScoped<IDomainEventHandler<OrganizationBillingChanged>, OrganizationPlanHandler>();

        return services;
    }

    public static IEndpointRouteBuilder MapTenancyEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapOrganizationEndpoints();
        api.MapMemberEndpoints();
        api.MapInvitationEndpoints();
        api.MapProjectEndpoints();
        api.MapProjectMemberEndpoints();
        api.MapTeamEndpoints();
        api.MapAgentEndpoints();
        api.MapAuditLogEndpoints();
        return api;
    }
}

using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Aictiq.Modules.Tenancy.Contracts;
using Aictiq.Modules.Wiki.Endpoints;
using Aictiq.Modules.Wiki.Events;
using Aictiq.SharedKernel.Events;
using Aictiq.SharedKernel.Persistence;
using Aictiq.SharedKernel.Mcp;
using Aictiq.SharedKernel.Contracts;
using Aictiq.Modules.Wiki.Search;

namespace Aictiq.Modules.Wiki;

public static class WikiModule
{
    public static IServiceCollection AddWikiModule(this IServiceCollection services)
    {
        services.AddModuleDbContext<WikiDbContext>("wiki");
        services.AddSingleton<IMcpToolProvider>(new McpToolProvider(typeof(WikiModule).Assembly));
        services.AddScoped<IWikiSearch, WikiSearchService>();
        services.AddScoped<WikiPageAccess>();
        services.AddScoped<IWikiPageAccess>(provider => provider.GetRequiredService<WikiPageAccess>());
        services.AddScoped<WikiPageContentService>();
        services.AddScoped<IWikiPageContent>(provider => provider.GetRequiredService<WikiPageContentService>());
        services.AddScoped<IWikiPageCreator>(provider => provider.GetRequiredService<WikiPageContentService>());
        return services;
    }

    public static IServiceCollection AddWikiWorkers(this IServiceCollection services)
    {
        services.AddScoped<IDomainEventHandler<ProjectCreated>, WikiProjectCreatedHandler>();
        services.AddScoped<IDomainEventHandler<WorkItemsDeleted>, WikiWorkItemsDeletedHandler>();
        services.AddScoped<IDomainEventHandler<ProjectDeleted>, WikiProjectDeletedHandler>();
        services.AddScoped<IDomainEventHandler<OrganizationDeleted>, WikiOrganizationDeletedHandler>();
        return services;
    }

    public static IEndpointRouteBuilder MapWikiEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapWikiPageEndpoints();
        return api;
    }
}

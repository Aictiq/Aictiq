using Aictiq.Modules.Automation.Endpoints;
using Aictiq.Modules.Automation.Events;
using Aictiq.Modules.Automation.Mcp;
using Aictiq.Modules.Automation.Workers;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Events;
using Aictiq.SharedKernel.Mcp;
using Aictiq.SharedKernel.Persistence;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aictiq.Modules.Automation;

/// <summary>
/// The AI software factory (phase 10). The runner credential is registered separately, on
/// the host's authentication builder (<c>AddRunnerCredentials</c>), because only the API
/// authenticates anything.
/// </summary>
public static class AutomationModule
{
    public static IServiceCollection AddAutomationModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<AutomationDbContext>(AutomationDbContext.SchemaName);
        services.AddOptions<AutomationOptions>()
            .Bind(configuration.GetSection(AutomationOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<AutomationRetentionOptions>()
            .Bind(configuration.GetSection(AutomationRetentionOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddScoped<RunDispatcher>();
        // The run tools, the aictiq://run/{id} resource: the API host discovers them from
        // this assembly exactly as it discovers WorkItems'.
        services.AddSingleton<IMcpToolProvider>(new McpToolProvider(typeof(RunMcpTools).Assembly));
        return services;
    }

    /// <summary>Workers only: the handlers that react to other modules' integration events.</summary>
    public static IServiceCollection AddAutomationWorkers(this IServiceCollection services)
    {
        services.AddScoped<IDomainEventHandler<OrganizationDeleted>, AutomationOrganizationDeletedHandler>();
        services.AddScoped<IDomainEventHandler<ProjectDeleted>, AutomationProjectDeletedHandler>();
        services.AddScoped<IDomainEventHandler<WikiPagesDeleted>, AutomationWikiPagesDeletedHandler>();
        services.AddScoped<IDomainEventHandler<WorkItemsDeleted>, AutomationWorkItemsDeletedHandler>();
        // The rule conveyor: fires on WorkItems' own transition event.
        services.AddScoped<IDomainEventHandler<Aictiq.Modules.WorkItems.Contracts.WorkItemTransitioned>, RuleFiringHandler>();
        services.AddHostedService<RunSweeper>();
        services.AddHostedService<RunLogRetentionService>();
        return services;
    }

    public static IEndpointRouteBuilder MapAutomationEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapRunnerEndpoints();
        api.MapPlaybookEndpoints();
        api.MapRunEndpoints();
        api.MapRunProtocolEndpoints();
        api.MapRuleEndpoints();
        return api;
    }
}

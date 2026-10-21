using Aictiq.Modules.Automation.Access;
using Aictiq.Modules.Automation.Endpoints;
using Aictiq.Modules.Automation.Events;
using Aictiq.Modules.Automation.Mcp;
using Aictiq.Modules.Automation.Workers;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Events;
using Aictiq.SharedKernel.Http;
using Aictiq.SharedKernel.Mcp;
using Aictiq.SharedKernel.Persistence;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

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
        services.AddScoped<FleetMode>();
        services.AddSingleton<FleetSecrets>();
        // Registered with the module rather than the workers so the sweeper a test drives in the
        // API host finds it. The address is re-checked when the socket opens, as outbound
        // webhooks' is: a receiver whose DNS flips to a private address after the save still fails.
        // No retry handler: one attempt per cooldown is the contract, and a retried 5xx would
        // start a second machine for the same queue.
        services.AddScoped<FleetScaler>();
        // EXTEXP0001: RemoveAllResilienceHandlers is still marked experimental; it is the
        // supported way to opt one client out of ConfigureHttpClientDefaults.
#pragma warning disable EXTEXP0001
        services.AddHttpClient(FleetScaler.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(10))
            .ConfigurePrimaryHttpMessageHandler(sp => PublicNetworkGuard.CreateHandler(
                sp.GetRequiredService<IOptions<AutomationOptions>>().Value.FleetWebhookAllowPrivateNetworks))
            .RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001
        services.AddScoped<RunMentionQueue>();
        services.AddScoped<ChainQueue>();
        services.AddScoped<IFactoryPages, AutomationFactoryPages>();
        services.AddScoped<IProjectChatGrants, AutomationProjectChatGrants>();
        services.AddScoped<IScheduledRuns, AutomationScheduledRuns>();
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
        // The comment door: a comment that mentions an agent asks it to work on the item.
        services.AddScoped<IDomainEventHandler<Aictiq.Modules.WorkItems.Contracts.CommentAdded>, AgentMentionHandler>();
        services.AddScoped<IDomainEventHandler<Aictiq.Modules.WorkItems.Contracts.CommentMentionsAdded>, AgentMentionEditHandler>();
        services.AddHostedService<RunSweeper>();
        services.AddHostedService<RunLogRetentionService>();
        return services;
    }

    public static IEndpointRouteBuilder MapAutomationEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapRunnerEndpoints();
        api.MapFleetEndpoints();
        api.MapPlaybookEndpoints();
        api.MapRunEndpoints();
        api.MapRunProtocolEndpoints();
        api.MapRuleEndpoints();
        api.MapRefinementEndpoints();
        api.MapProjectChatEndpoints();
        return api;
    }
}

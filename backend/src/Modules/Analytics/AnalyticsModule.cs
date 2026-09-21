using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Aictiq.Modules.Analytics.Events;
using Aictiq.Modules.Analytics.Endpoints;
using Aictiq.Modules.Analytics.Workers;
using Aictiq.Modules.WorkItems.Contracts;
using Aictiq.SharedKernel.Events;
using Aictiq.SharedKernel.Persistence;
using Aictiq.SharedKernel.Contracts;

namespace Aictiq.Modules.Analytics;

public static class AnalyticsModule
{
    public static IServiceCollection AddAnalyticsModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<AnalyticsDbContext>("analytics");
        // Bound here rather than in AddAnalyticsWorkers: the report window is read by the
        // API's endpoints, and the operator's retention is one of the two bounds it takes
        // the narrower of.
        services.Configure<AnalyticsOptions>(configuration.GetSection(AnalyticsOptions.SectionName));
        services.AddScoped<AnalyticsHistoryWindow>();
        return services;
    }

    public static IServiceCollection AddAnalyticsWorkers(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IDomainEventHandler<WorkItemTransitioned>, TransitionAnalyticsEventHandler>();
        services.AddScoped<IDomainEventHandler<SprintScopeChanged>, ScopeAnalyticsEventHandler>();
        services.AddScoped<IDomainEventHandler<WorkItemsDeleted>, AnalyticsWorkItemsDeletedHandler>();
        services.AddScoped<IDomainEventHandler<ProjectDeleted>, AnalyticsProjectDeletedHandler>();
        services.AddScoped<IDomainEventHandler<SprintsDeleted>, AnalyticsSprintsDeletedHandler>();
        services.AddScoped<IDomainEventHandler<OrganizationDeleted>, AnalyticsOrganizationDeletedHandler>();
        services.AddHostedService<DailySnapshotService>();
        services.AddHostedService<RetentionCleanupService>();
        return services;
    }

    public static IEndpointRouteBuilder MapAnalyticsEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapSprintMetricsEndpoints();
        api.MapFlowMetricsEndpoints();
        api.MapDashboardEndpoints();
        api.MapOrganizationOverviewEndpoints();
        return api;
    }
}

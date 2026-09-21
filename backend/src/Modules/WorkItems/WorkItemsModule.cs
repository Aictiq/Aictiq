using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.EntityFrameworkCore;
using Aictiq.Modules.WorkItems.Endpoints;
using Aictiq.Modules.WorkItems.Events;
using Aictiq.Modules.WorkItems.Contracts;
using Aictiq.Modules.WorkItems.Workers;
using Aictiq.Modules.Integrations.Contracts;
using Aictiq.Modules.Tenancy.Contracts;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Events;
using Aictiq.SharedKernel.Persistence;
using Aictiq.SharedKernel.Mcp;
using Aictiq.SharedKernel.Http;
using Aictiq.Modules.WorkItems.Access;
using Aictiq.Modules.WorkItems.Mcp;

namespace Aictiq.Modules.WorkItems;

public static class WorkItemsModule
{
    public static IServiceCollection AddWorkItemsModule(this IServiceCollection services)
    {
        services.AddModuleDbContext<WorkItemsDbContext>("work");
        services.AddScoped<IStorageUsageSource, WorkItemsStorageUsageSource>();
        services.AddScoped<IWorkItemLookup, WorkItemLookup>();
        services.AddScoped<IProjectWorkflowAccess, ProjectWorkflowAccess>();
        services.AddScoped<IWorkItemLabels, WorkItemLabelsAccess>();
        services.AddScoped<IProjectMcpDetails, WorkItemsProjectMcpDetails>();
        services.AddSingleton<IMcpToolProvider>(new McpToolProvider(typeof(WorkItemsModule).Assembly));
        services.AddOptions<AttachmentsOptions>().BindConfiguration(AttachmentsOptions.SectionName);
        services.AddOptions<ClaimsOptions>().BindConfiguration(ClaimsOptions.SectionName);
        // Public addresses only, checked at connect time so a rebinding name cannot turn a
        // link preview into a request to the metadata service or a neighbour.
        services.AddHttpClient("aictiq-link-preview", client => client.Timeout = TimeSpan.FromSeconds(3))
            .ConfigurePrimaryHttpMessageHandler(() => PublicNetworkGuard.CreateHandler());
        services.AddScoped<LinkPreviewFetcher>();
        services.RemoveAll<ITeamUsage>();
        services.AddScoped<ITeamUsage, WorkItemsTeamUsage>();
        // The same replace-the-null precedent: SharedKernel's NullWorkItemClaims answers
        // NotFound so a missing WorkItems module fails closed; this module owns the real CAS.
        services.RemoveAll<IWorkItemClaims>();
        services.AddScoped<IWorkItemClaims, WorkItemClaims>();
        services.AddScoped<IItemWatchers, WorkItemWatchers>();
        services.AddScoped<IWorkItemSnapshotSource, WorkItemSnapshotSource>();
        return services;
    }
    public static IServiceCollection AddWorkItemsWorkers(this IServiceCollection services)
    {
        services.AddScoped<IDomainEventHandler<ProjectCreated>, ProjectCreatedHandler>();
        services.AddScoped<IDomainEventHandler<AttachmentDeleted>, AttachmentDeletedHandler>();
        services.AddScoped<IDomainEventHandler<WikiPagesDeleted>, WikiPagesDeletedHandler>();
        services.AddScoped<IDomainEventHandler<WorkItemsDeleted>, WorkItemsDeletedHandler>();
        services.AddScoped<IDomainEventHandler<ProjectDeleted>, ProjectDeletedHandler>();
        services.AddScoped<IDomainEventHandler<OrganizationDeleted>, OrganizationDeletedHandler>();
        services.AddScoped<IDomainEventHandler<CommitReferencedItem>, CommitReferencedItemHandler>();
        services.AddScoped<IDomainEventHandler<PullRequestReferencedItem>, PullRequestReferencedItemHandler>();
        services.AddScoped<IDomainEventHandler<RunFinished>, RunFinishedHandler>();
        services.AddScoped<IDomainEventHandler<ItemChanged>, ItemChangedRealtimeHandler>();
        services.AddHostedService<AttachmentCleanupService>();
        services.AddHostedService<StaleClaimReleaseService>();
        services.AddHostedService<CsvImportWorker>();
        return services;
    }
    public static IEndpointRouteBuilder MapWorkItemsEndpoints(this IEndpointRouteBuilder api)
    { api.MapWorkflowEndpoints(); api.MapItemWatchEndpoints(); api.MapSprintEndpoints(); api.MapBoardEndpoints(); api.MapWorkItemEndpoints(); api.MapCsvImportExportEndpoints(); api.MapPortfolioEndpoints(); api.MapLabelEndpoints(); api.MapItemTemplateEndpoints(); api.MapSavedViewEndpoints(); api.MapCommentEndpoints(); api.MapAttachmentEndpoints(); api.MapItemRelationsEndpoints(); api.MapGitHubBranchEndpoints(); api.MapSearchEndpoints(); api.MapAgentActivityEndpoints(); return api; }
}

internal sealed class WorkItemsTeamUsage(WorkItemsDbContext db) : ITeamUsage
{
    public Task<bool> HasItemsAsync(Guid teamId, CancellationToken cancellationToken = default) =>
        db.Items.AnyAsync(x => x.TeamId == teamId, cancellationToken);
}

using Aictiq.Modules.Billing.Endpoints;
using Aictiq.Modules.Billing.Events;
using Aictiq.Modules.Billing.Payments;
using Aictiq.Modules.Billing.Workers;
using Aictiq.Modules.Tenancy.Contracts;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Events;
using Aictiq.SharedKernel.Persistence;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aictiq.Modules.Billing;

public static class BillingModule
{
    public static IServiceCollection AddBillingModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<BillingDbContext>("billing");
        services.AddOptions<BillingOptions>().Bind(configuration.GetSection(BillingOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        // Bound, never validated: no Stripe keys is how every self-hosted instance runs.
        services.AddOptions<StripeOptions>().Bind(configuration.GetSection(StripeOptions.SectionName));

        services.RemoveAll<IPlanLimits>(); services.AddScoped<IPlanLimits, BillingPlanLimits>();
        services.RemoveAll<IOrganizationBillingState>(); services.AddScoped<IOrganizationBillingState, BillingOrganizationState>();
        services.RemoveAll<IPlanAllowances>(); services.AddScoped<IPlanAllowances, BillingAllowances>();

        services.AddSingleton<BillingAvailability>();
        services.AddScoped<BillingUsage>();
        services.AddScoped<SeatSynchronizer>();
        services.AddScoped<StripeWebhookProcessor>();
        // The SDK client is built lazily inside, so registering it costs nothing without keys.
        services.AddSingleton<IStripeGateway, StripeGateway>();

        // Outbox-only; registered with the module so Workers (and tests that drain the
        // outbox through the API's container) have them.
        services.AddScoped<SeatSyncHandler>();
        services.AddScoped<IDomainEventHandler<OrganizationMemberAdded>>(sp => sp.GetRequiredService<SeatSyncHandler>());
        services.AddScoped<IDomainEventHandler<OrganizationMembershipChanged>>(sp => sp.GetRequiredService<SeatSyncHandler>());
        services.AddScoped<IDomainEventHandler<OrganizationBillingChanged>>(sp => sp.GetRequiredService<SeatSyncHandler>());
        services.AddScoped<IDomainEventHandler<OrganizationDeleted>, BillingOrganizationDeletedHandler>();
        services.AddScoped<EvaluationStartHandler>();
        services.AddScoped<IDomainEventHandler<OrganizationCreated>>(sp => sp.GetRequiredService<EvaluationStartHandler>());
        return services;
    }

    /// <summary>Workers only: the nightly seat sync and the Stripe event-ledger sweep.</summary>
    public static IServiceCollection AddBillingWorkers(this IServiceCollection services)
    {
        services.AddSingleton<BillingNightlyService>();
        services.AddHostedService(sp => sp.GetRequiredService<BillingNightlyService>());
        return services;
    }

    /// <param name="root">The unversioned root, for Stripe's webhook URL.</param>
    public static IEndpointRouteBuilder MapBillingEndpoints(this IEndpointRouteBuilder api, IEndpointRouteBuilder root)
    {
        api.MapBillingApiEndpoints();
        root.MapStripeWebhookEndpoint();
        return api;
    }
}

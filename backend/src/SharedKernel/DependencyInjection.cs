using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Aictiq.SharedKernel.Email;
using Aictiq.SharedKernel.Events;
using Aictiq.SharedKernel.Outbox;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Persistence;
using Aictiq.SharedKernel.Storage;
using Aictiq.SharedKernel.Tenancy;
using Aictiq.SharedKernel.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Aictiq.SharedKernel;

public static class DependencyInjection
{
    public static IServiceCollection AddSharedKernel(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<OperationRateLimiter>();
        services.AddScoped<IDomainEventDispatcher, DomainEventDispatcher>();
        services.AddScoped<AuditingInterceptor>();
        services.AddScoped<TenantSessionInterceptor>();

        // Scoped: one tenant per request (or per unit of background work). Registered as
        // both the concrete type and the interface so the middleware can set it while
        // everything else can only read it.
        services.AddScoped<AmbientCurrentTenant>();
        services.AddScoped<ICurrentTenant>(sp => sp.GetRequiredService<AmbientCurrentTenant>());

        // The modules that own these replace them with TryAdd-free registrations when they
        // are added. Until then the fail-closed stubs keep the wiring honest.
        services.TryAddScoped<IOrganizationLookup, NullOrganizationLookup>();
        services.TryAddScoped<IProjectAccess, NullProjectAccess>();
        services.TryAddScoped<IUserDirectory, NullUserDirectory>();
        services.TryAddScoped<IExternalLoginLookup, NullExternalLoginLookup>();
        services.TryAddScoped<IAgentIdentities, NullAgentIdentities>();
        services.TryAddSingleton<IRealtimePublisher, NullRealtimePublisher>();
        services.TryAddSingleton<IUserRealtimePublisher, NullRealtimePublisher>();
        services.TryAddSingleton<IRunRealtimePublisher, NullRealtimePublisher>();
        services.TryAddScoped<IUnreadNotificationCounter, NullUnreadNotificationCounter>();
        services.TryAddScoped<IWikiPageContent, NullWikiPageContent>();
        services.TryAddScoped<IWikiPageCreator, NullWikiPageCreator>();
        services.TryAddScoped<IProjectWorkflowAccess, NullProjectWorkflowAccess>();

        // Deliberately permissive, unlike the stubs above: "no billing module" means the
        // instance is not metered, not that nobody may join it. The Billing module replaces it.
        services.TryAddSingleton<IPlanLimits, UnlimitedPlanLimits>();
        // Same reasoning: no billing module means nothing is ever read-only for want of a
        // payment. The Billing module replaces it.
        services.TryAddSingleton<IOrganizationBillingState, UnmeteredBillingState>();
        // And no billing module means no allowance ever narrows a retention default.
        // The Billing module replaces it.
        services.TryAddSingleton<IPlanAllowances, UnmeteredPlanAllowances>();

        // Same reasoning: no WorkItems module means nothing references a team, so teams
        // are deletable rather than permanently undeletable. The WorkItems module replaces it.
        services.TryAddScoped<ITeamUsage, NoTeamUsage>();
        // No WorkItems module also means there is no item to claim: the factory's
        // dispatcher gets "not found" rather than a pretend success. The WorkItems
        // module replaces this with the real compare-and-swap.
        services.TryAddScoped<IWorkItemClaims, NullWorkItemClaims>();
        // No Integrations module means no bindings and no clone tokens.
        services.TryAddScoped<IRepositoryCredentials, NullRepositoryCredentials>();

        return services;
    }

    /// <summary>
    /// Object storage over the S3 API, plus its readiness check. Registered by both the
    /// API (presigning, deletes) and Workers (blob garbage collection).
    ///
    /// The options are validated on start rather than on first use: a typo in an endpoint
    /// or a missing key should stop a deployment, not surface as a failed upload later.
    /// </summary>
    public static IServiceCollection AddBlobStorage(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<S3StorageOptions>()
            .Bind(configuration.GetSection(S3StorageOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<IBlobStorage, S3BlobStorage>();
        services.AddHealthChecks()
            .AddCheck<StorageHealthCheck>(StorageHealthCheck.Name, tags: ["ready", "storage"]);

        return services;
    }

    /// <summary>
    /// Outbound email, plus the capability report on /health/ready.
    ///
    /// Optional by design: with no <c>Email:Smtp:Host</c> the service still starts, the
    /// sender is <see cref="NullEmailSender"/>, and <see cref="IEmailCapabilities"/> says
    /// so, which is what lets the invitation screens offer a copyable link instead of
    /// promising a message. That is why these options are not validated on start the way
    /// storage's are — "absent" is a valid configuration here and an invalid one there.
    /// </summary>
    public static IServiceCollection AddEmail(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<EmailOptions>()
            .Bind(configuration.GetSection(EmailOptions.SectionName));
        services.AddSingleton<IValidateOptions<EmailOptions>, EmailOptionsValidator>();

        services.AddSingleton<IEmailCapabilities, EmailCapabilities>();
        services.AddHostedService<EmailConfigurationWarning>();

        // Chosen once, at composition, from the configuration the process started with.
        // A relay cannot be configured into existence at runtime, and picking per-send
        // would hide "there is no relay" behind a successful-looking code path.
        var options = configuration.GetSection(EmailOptions.SectionName).Get<EmailOptions>();
        if (options?.IsConfigured == true)
        {
            services.AddSingleton<IEmailSender, SmtpEmailSender>();
        }
        else
        {
            services.AddSingleton<IEmailSender, NullEmailSender>();
        }

        services.AddHealthChecks().AddCheck<EmailHealthCheck>(
            EmailHealthCheck.Name,
            // "public" is ServiceDefaults' opt-in tag (Extensions.PublicHealthTag): this
            // check's description is a fact about the deployment, safe to publish in the
            // readiness body so an operator can see why no invitations arrive. Spelled
            // out because SharedKernel does not reference the hosting project.
            tags: ["ready", "email", "public"]);

        return services;
    }

    /// <summary>Workers-only: the single consumer of shared.outbox_messages.</summary>
    public static IServiceCollection AddOutboxProcessor(this IServiceCollection services, params Assembly[] eventAssemblies)
    {
        services.AddSingleton(new IntegrationEventTypeRegistry(eventAssemblies));
        services.AddSingleton<OutboxProcessor>();
        services.AddHostedService(sp => sp.GetRequiredService<OutboxProcessor>());
        services.AddOutboxHealthCheck();
        return services;
    }

    /// <summary>
    /// Surfaces dead-lettered outbox messages on /health/ready. Registered automatically
    /// with the processor; call it in the API too if you want the alert on both services.
    /// </summary>
    public static IServiceCollection AddOutboxHealthCheck(this IServiceCollection services)
    {
        services.AddHealthChecks()
            .AddCheck<OutboxHealthCheck>(OutboxHealthCheck.Name, tags: ["ready", "outbox"]);
        return services;
    }
}

using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Aictiq.SharedKernel.Http;
using Aictiq.Modules.Integrations.Workers;
using Aictiq.Modules.Integrations.Endpoints;
using Aictiq.SharedKernel.Events;
using Aictiq.SharedKernel.Persistence;
using Aictiq.SharedKernel.Contracts;

namespace Aictiq.Modules.Integrations;

public static class IntegrationsModule
{
    public const string GitHubHttpClientName = "aictiq-github";
    public const string WebhookHttpClientName = "aictiq-webhook";

    public static IServiceCollection AddIntegrationsModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<IntegrationsDbContext>("integrations");
        services.AddOptions<GitHubOptions>().Bind(configuration.GetSection(GitHubOptions.SectionName));
        services.AddOptions<WebhooksOptions>().Bind(configuration.GetSection(WebhooksOptions.SectionName));
        services.AddHttpClient(GitHubHttpClientName, client =>
        {
            client.BaseAddress = new Uri("https://api.github.com/");
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Aictiq-GitHub-App/1.0");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        });
        // The address is re-checked when the socket opens, not only when the URL was saved:
        // a receiver whose DNS flips to a private address after validation must still fail.
        services.AddHttpClient(WebhookHttpClientName, client => client.Timeout = TimeSpan.FromSeconds(10))
            .ConfigurePrimaryHttpMessageHandler(sp => PublicNetworkGuard.CreateHandler(
                sp.GetRequiredService<IOptions<WebhooksOptions>>().Value.AllowPrivateNetworks));
        services.AddSingleton<WebhookSecretProtector>();
        services.AddScoped<GitHubAppClient>();
        services.RemoveAll<IRepositoryCredentials>();
        services.AddScoped<IRepositoryCredentials, Contracts.IntegrationsRepositoryCredentials>();
        return services;
    }

    public static IServiceCollection AddIntegrationsWorkers(this IServiceCollection services)
    {
        services.AddScoped<IDomainEventHandler<Contracts.GitHubDeliveryReceived>, GitHubDeliveryReceivedHandler>();
        services.AddScoped<IDomainEventHandler<Aictiq.SharedKernel.Domain.IIntegrationEvent>, WebhookFanoutHandler>();
        services.AddScoped<IDomainEventHandler<ProjectDeleted>, Events.IntegrationsProjectDeletedHandler>();
        services.AddScoped<IDomainEventHandler<OrganizationDeleted>, Events.IntegrationsOrganizationDeletedHandler>();
        services.AddSingleton<WebhookDeliveryService>();
        services.AddHostedService(sp => sp.GetRequiredService<WebhookDeliveryService>());
        return services;
    }

    /// <summary>
    /// The host calls these only when the App credentials are present. Conditional route
    /// registration is what makes a non-configured GitHub feature answer 404, including
    /// the otherwise-public webhook URL.
    /// </summary>
    public static void MapIntegrationsEndpoints(this IEndpointRouteBuilder api, IEndpointRouteBuilder root, IConfiguration configuration)
    {
        api.MapWebhookEndpoints();
        if (configuration.GetSection(GitHubOptions.SectionName).Get<GitHubOptions>()?.IsConfigured ?? false)
        {
            api.MapGitHubApiEndpoints();
            root.MapGitHubSetupEndpoint();
            root.MapGitHubWebhookEndpoint();
        }
    }
}

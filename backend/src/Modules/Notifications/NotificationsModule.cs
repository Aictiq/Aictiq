using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Routing;
using Aictiq.Modules.Notifications.Delivery;
using Aictiq.Modules.Notifications.Events;
using Aictiq.Modules.Notifications.Templates;
using Aictiq.SharedKernel.Email;
using Aictiq.SharedKernel.Events;
using Aictiq.SharedKernel.Persistence;
using Aictiq.Modules.WorkItems.Contracts;
using Aictiq.Modules.Notifications.Endpoints;
using Aictiq.SharedKernel.Contracts;

namespace Aictiq.Modules.Notifications;

public static class NotificationsModule
{
    public static IServiceCollection AddNotificationsModule(this IServiceCollection services)
    {
        services.AddModuleDbContext<NotificationsDbContext>("notify");
        services.AddScoped<IUnreadNotificationCounter, UnreadNotificationCounter>();
        services.AddScoped<INotificationPresence, NotificationPresenceService>();
        services.AddDataProtection();
        services.AddSingleton<NotificationUnsubscribeTokens>();
        return services;
    }

    public static IEndpointRouteBuilder MapNotificationsEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapNotificationEndpoints();
        return api;
    }

    /// <summary>
    /// Workers-only: the handler that turns a <see cref="SendEmailRequested"/> event into a
    /// queued message, and the sweep that hands the queue to a relay. Neither belongs in
    /// the API - sending is not something an HTTP request should wait on, and a second
    /// instance sweeping the same queue would double every message.
    /// </summary>
    public static IServiceCollection AddNotificationsWorkers(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<EmailDeliveryOptions>(
            configuration.GetSection(EmailDeliveryOptions.SectionName));
        services.AddOptions<NotificationEmailOptions>()
            .Bind(configuration.GetSection(NotificationEmailOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Templates are parsed once and cached; the renderer is stateless beyond that.
        services.AddSingleton<EmailTemplateRenderer>();
        services.AddScoped<NotificationEmailService>();
        services.AddScoped<IDomainEventHandler<SendEmailRequested>, SendEmailRequestedHandler>();
        services.AddScoped<IDomainEventHandler<CommentAdded>, CommentNotificationHandler>();
        services.AddScoped<IDomainEventHandler<WorkItemTransitioned>, TransitionNotificationHandler>();
        services.AddScoped<IDomainEventHandler<WorkItemsDeleted>, NotificationsWorkItemsDeletedHandler>();
        services.AddScoped<IDomainEventHandler<ProjectDeleted>, NotificationsProjectDeletedHandler>();
        services.AddScoped<IDomainEventHandler<OrganizationDeleted>, NotificationsOrganizationDeletedHandler>();
        services.AddSingleton<EmailDeliveryService>();
        services.AddHostedService(sp => sp.GetRequiredService<EmailDeliveryService>());
        services.AddHostedService<DailyNotificationDigestService>();

        return services;
    }
}

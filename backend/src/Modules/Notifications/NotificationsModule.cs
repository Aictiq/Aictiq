using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.Configuration;
using Npgsql;
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
        services.AddScoped<NotificationVisibility>();
        services.AddScoped<IUnreadNotificationCounter, UnreadNotificationCounter>();
        services.AddScoped<INotificationPresence, NotificationPresenceService>();
        // One key ring for the API and Workers, in the database: see PostgresDataProtectionKeys.
        services.AddDataProtection().SetApplicationName("Aictiq");
        services.AddOptions<KeyManagementOptions>().Configure<IServiceProvider>((options, provider) =>
            options.XmlRepository = new PostgresDataProtectionKeys(provider.GetRequiredService<NpgsqlDataSource>()));
        services.AddSingleton<NotificationUnsubscribeTokens>();

        // Chat channels: the API connects and tests them, Workers deliver to them. The client
        // logs nothing - a webhook URL or Telegram API path is itself the credential. It also
        // drops the default retry handler: chat_outbox already retries with backoff and counts
        // failures toward "broken", and a post retried after a timeout or a 5xx the platform
        // had already accepted shows up twice in the chat.
        services.AddOptions<TelegramOptions>().BindConfiguration(TelegramOptions.SectionName);
        // EXTEXP0001: RemoveAllResilienceHandlers is still marked experimental; it is the
        // supported way to opt one client out of ConfigureHttpClientDefaults.
#pragma warning disable EXTEXP0001
        services.AddHttpClient(ChatSender.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(10))
            .RemoveAllLoggers()
            .RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001
        services.AddSingleton<ChatSecrets>();
        services.AddScoped<IChatSender, TelegramSender>();
        services.AddScoped<IChatSender, SlackSender>();
        services.AddScoped<IChatSender, DiscordSender>();
        services.AddScoped<ChatNotificationService>();
        return services;
    }

    public static IEndpointRouteBuilder MapNotificationsEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapNotificationEndpoints();
        return api;
    }

    /// <summary>
    /// Workers-only: the handler that turns a <see cref="SendEmailRequested"/> event into a
    /// queued message, the notification handlers, and the sweeps that hand the email and
    /// chat queues to a relay or platform. Neither belongs in
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
        services.AddScoped<IDomainEventHandler<CommentReactionAdded>, CommentReactionNotificationHandler>();
        services.AddScoped<IDomainEventHandler<CommentMentionsAdded>, CommentMentionsNotificationHandler>();
        services.AddScoped<IDomainEventHandler<WorkItemTransitioned>, TransitionNotificationHandler>();
        services.AddScoped<IDomainEventHandler<RunFinished>, RunNotificationHandler>();
        services.AddScoped<IDomainEventHandler<SprintStarted>, SprintStartedChannelHandler>();
        services.AddScoped<IDomainEventHandler<SprintCompleted>, SprintCompletedChannelHandler>();
        services.AddScoped<IDomainEventHandler<WorkItemsDeleted>, NotificationsWorkItemsDeletedHandler>();
        services.AddScoped<IDomainEventHandler<ProjectDeleted>, NotificationsProjectDeletedHandler>();
        services.AddScoped<IDomainEventHandler<OrganizationDeleted>, NotificationsOrganizationDeletedHandler>();
        services.AddSingleton<EmailDeliveryService>();
        services.AddHostedService(sp => sp.GetRequiredService<EmailDeliveryService>());
        services.AddHostedService<DailyNotificationDigestService>();
        services.Configure<ChatDeliveryOptions>(configuration.GetSection(ChatDeliveryOptions.SectionName));
        services.AddSingleton<ChatDeliveryService>();
        services.AddHostedService(sp => sp.GetRequiredService<ChatDeliveryService>());

        return services;
    }
}

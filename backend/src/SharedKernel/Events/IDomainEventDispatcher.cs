using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Aictiq.SharedKernel.Domain;

namespace Aictiq.SharedKernel.Events;

public interface IDomainEventHandler
{
    Task HandleAsync(IDomainEvent domainEvent, CancellationToken cancellationToken);
}

public interface IDomainEventHandler<in TEvent> : IDomainEventHandler where TEvent : IDomainEvent
{
    Task HandleAsync(TEvent domainEvent, CancellationToken cancellationToken);

    Task IDomainEventHandler.HandleAsync(IDomainEvent domainEvent, CancellationToken cancellationToken) =>
        HandleAsync((TEvent)domainEvent, cancellationToken);
}

public interface IDomainEventDispatcher
{
    /// <summary>Post-commit in-process dispatch: handler failures are logged, never propagated.</summary>
    Task DispatchAsync(IDomainEvent domainEvent, CancellationToken cancellationToken);

    /// <summary>Outbox dispatch: failures propagate so the message is retried (attempts++).</summary>
    Task DispatchStrictAsync(IDomainEvent domainEvent, CancellationToken cancellationToken);
}

/// <summary>
/// Resolves all IDomainEventHandler&lt;TEvent&gt; registrations for the event's runtime
/// type and invokes them.
/// </summary>
public sealed class DomainEventDispatcher(IServiceProvider serviceProvider, ILogger<DomainEventDispatcher> logger)
    : IDomainEventDispatcher
{
    public async Task DispatchAsync(IDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        foreach (var handler in ResolveHandlers(domainEvent))
        {
            try
            {
                await handler.HandleAsync(domainEvent, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Handler {Handler} failed to process event {Event}",
                    handler.GetType().Name, domainEvent.GetType().Name);
            }
        }
    }

    public async Task DispatchStrictAsync(IDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        foreach (var handler in ResolveHandlers(domainEvent))
        {
            await handler.HandleAsync(domainEvent, cancellationToken);
        }
    }

    private IEnumerable<IDomainEventHandler> ResolveHandlers(IDomainEvent domainEvent)
    {
        var handlerType = typeof(IDomainEventHandler<>).MakeGenericType(domainEvent.GetType());
        var handlers = serviceProvider.GetServices(handlerType).Cast<IDomainEventHandler>();
        // A module that observes every durable event (outgoing webhooks) should not need
        // a reference to every producing module. Exact handlers still run as before.
        return domainEvent is IIntegrationEvent
            ? handlers.Concat(serviceProvider.GetServices<IDomainEventHandler<IIntegrationEvent>>())
            : handlers;
    }
}

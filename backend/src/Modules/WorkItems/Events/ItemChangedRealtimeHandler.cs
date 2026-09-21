using Aictiq.Modules.WorkItems.Contracts;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Events;

namespace Aictiq.Modules.WorkItems.Events;

/// <summary>
/// Tells the people looking at a project that an item changed. <see cref="ItemChanged"/> is
/// an integration event, so this runs in Workers from the outbox — registered with the
/// API's in-process handlers it never ran at all, and no item edit reached a browser.
///
/// At-least-once delivery needs no database guard here: the message is an instruction to
/// refetch, and a replay only refetches again.
/// </summary>
public sealed class ItemChangedRealtimeHandler(IRealtimePublisher publisher) : IDomainEventHandler<ItemChanged>
{
    public Task HandleAsync(ItemChanged e, CancellationToken ct) => publisher.PublishAsync(e.ProjectId, "item.changed",
        new { id = e.ItemId, key = e.Key, actorId = e.ActorId, changedFields = e.ChangedFields }, ct);
}

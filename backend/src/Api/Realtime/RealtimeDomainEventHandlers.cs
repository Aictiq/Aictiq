using Aictiq.Modules.WorkItems.Contracts;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Events;

namespace Aictiq.Api.Realtime;

/// <summary>
/// Maps post-commit WorkItems domain events to intentionally small client notifications.
/// Only plain domain events belong here — they are dispatched in this process. An
/// integration event (<c>ItemChanged</c>) is handled in Workers, so its realtime handler
/// lives with the module (<c>WorkItems.Events.ItemChangedRealtimeHandler</c>).
/// </summary>
public sealed class CommentAddedRealtimeHandler(IRealtimePublisher publisher) : IDomainEventHandler<RealtimeCommentAdded>
{
    public Task HandleAsync(RealtimeCommentAdded e, CancellationToken ct) => publisher.PublishAsync(e.ProjectId, "comment.added",
        new { itemId = e.ItemId, commentId = e.CommentId, actorId = e.AuthorId }, ct);
}

public sealed class BoardMovedRealtimeHandler(IRealtimePublisher publisher) : IDomainEventHandler<BoardMoved>
{
    public Task HandleAsync(BoardMoved e, CancellationToken ct) => publisher.PublishAsync(e.ProjectId, "board.moved",
        new { id = e.ItemId, key = e.Key, actorId = e.ActorId }, ct);
}

public sealed class SprintChangedRealtimeHandler(IRealtimePublisher publisher) : IDomainEventHandler<SprintChanged>
{
    public Task HandleAsync(SprintChanged e, CancellationToken ct) => publisher.PublishAsync(e.ProjectId, "sprint.changed",
        new { id = e.SprintId, teamId = e.TeamId, actorId = e.ActorId }, ct);
}

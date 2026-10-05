using Aictiq.Modules.Notifications.Delivery;
using Aictiq.Modules.Notifications.Domain;
using Aictiq.Modules.WorkItems.Contracts;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Events;
using Aictiq.SharedKernel.Tenancy;

namespace Aictiq.Modules.Notifications.Events;

/// <summary>Sprints starting and ending are news for the whole team: shared channels only.</summary>
public sealed class SprintStartedChannelHandler(ChatNotificationService chat, IProjectAccess access, AmbientCurrentTenant tenant)
    : SprintChannelPublisher(chat, access, tenant), IDomainEventHandler<SprintStarted>
{
    public Task HandleAsync(SprintStarted e, CancellationToken cancellationToken) =>
        PublishAsync(e.OrganizationId, e.EventId, NotificationKind.SprintStarted, "started", e.Name, e.ProjectId, e.TeamId, e.SprintId, cancellationToken);
}

/// <inheritdoc cref="SprintStartedChannelHandler"/>
public sealed class SprintCompletedChannelHandler(ChatNotificationService chat, IProjectAccess access, AmbientCurrentTenant tenant)
    : SprintChannelPublisher(chat, access, tenant), IDomainEventHandler<SprintCompleted>
{
    public Task HandleAsync(SprintCompleted e, CancellationToken cancellationToken) =>
        PublishAsync(e.OrganizationId, e.EventId, NotificationKind.SprintCompleted, "completed", e.Name, e.ProjectId, e.TeamId, e.SprintId, cancellationToken);
}

public abstract class SprintChannelPublisher(ChatNotificationService chat, IProjectAccess access, AmbientCurrentTenant tenant)
{
    protected async Task PublishAsync(Guid organizationId, Guid eventId, NotificationKind kind, string verb, string? name,
        Guid? projectId, Guid teamId, Guid sprintId, CancellationToken cancellationToken)
    {
        using var scope = tenant.Use(organizationId);
        var project = projectId is { } id ? await access.GetProjectAsync(id, cancellationToken) : null;
        var url = project is null ? null : await chat.OrganizationUrlAsync(organizationId,
            $"p/{Uri.EscapeDataString(project.Key)}/teams/{teamId}/sprints/{sprintId}", cancellationToken);
        await chat.PublishToOrganizationAsync(organizationId, eventId, kind,
            new ChatMessage(name is null ? $"A sprint {verb}." : $"Sprint {name} {verb}.", project?.Name, url, "Open the sprint"), cancellationToken);
    }
}

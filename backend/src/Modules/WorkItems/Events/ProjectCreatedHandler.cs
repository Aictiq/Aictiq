using Aictiq.Modules.Tenancy.Contracts;
using Aictiq.Modules.WorkItems.Endpoints;
using Aictiq.SharedKernel.Events;
using Aictiq.SharedKernel.Tenancy;

namespace Aictiq.Modules.WorkItems.Events;

/// <summary>Creates the default workflow once. The filtered unique index is the replay guard.</summary>
public sealed class ProjectCreatedHandler(WorkItemsDbContext db, ICurrentTenant currentTenant) : IDomainEventHandler<ProjectCreated>
{
    public async Task HandleAsync(ProjectCreated @event, CancellationToken cancellationToken)
    {
        using var tenant = currentTenant is AmbientCurrentTenant ambient ? ambient.Use(@event.OrganizationId) : null;
        await WorkflowEndpoints.EnsureDefaultAsync(db, @event.OrganizationId, @event.ProjectId, cancellationToken);
        await ItemTemplateEndpoints.EnsureBuiltInsAsync(db, @event.OrganizationId, @event.ProjectId, cancellationToken);
    }
}

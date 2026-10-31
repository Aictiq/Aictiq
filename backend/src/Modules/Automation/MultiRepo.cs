using Aictiq.Modules.Automation.Domain;
using Aictiq.SharedKernel.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Aictiq.Modules.Automation;

/// <summary>
/// Whether multiple repositories are in force for a project: its switch, and the plan's say, like
/// <see cref="FleetMode"/>. A project that never turned it on - or whose organization downgraded -
/// takes exactly the single-repository path, and keeps its configuration for when it is back.
/// </summary>
public sealed class MultiRepo(AutomationDbContext db, IPlanLimits plans)
{
    public async Task<bool> IsActiveAsync(Guid organizationId, ProjectFactorySettings? settings, CancellationToken ct) =>
        // No plan check for the common case: a project that never opted in.
        settings is { MultiRepoEnabled: true }
        && await plans.GetMultiRepoAvailabilityAsync(organizationId, ct) == MultiRepoAvailability.Available;

    public async Task<IReadOnlyList<ProjectRepository>> AdditionalAsync(Guid projectId, CancellationToken ct) =>
        await db.ProjectRepositories.AsNoTracking()
            .Where(row => row.ProjectId == projectId)
            .OrderBy(row => row.Position)
            .ToListAsync(ct);
}

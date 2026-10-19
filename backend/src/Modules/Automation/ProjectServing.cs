using Aictiq.Modules.Automation.Domain;
using Aictiq.SharedKernel.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Aictiq.Modules.Automation;

/// <summary>
/// What a runner needs to have for one project's runs: nothing when each run clones it from
/// GitHub, otherwise a mapping for <see cref="Key"/> or a root above <see cref="PathHint"/>.
/// </summary>
public sealed record ProjectServing(string Key, string? PathHint, bool NeedsLocalRepository)
{
    public bool Serves(RunnerCapabilities? capabilities) =>
        !NeedsLocalRepository || RunnerProjects.CanServe(capabilities, Key, PathHint);

    public static async Task<Dictionary<Guid, ProjectServing>> LoadAsync(
        AutomationDbContext db, IProjectAccess access, IReadOnlyCollection<Guid> projectIds, CancellationToken ct)
    {
        var result = new Dictionary<Guid, ProjectServing>();
        if (projectIds.Count == 0)
        {
            return result;
        }

        var settings = await db.ProjectSettings.AsNoTracking()
            .Where(s => projectIds.Contains(s.ProjectId))
            .ToDictionaryAsync(s => s.ProjectId, ct);
        foreach (var projectId in projectIds)
        {
            var row = settings.GetValueOrDefault(projectId);
            if (!RunnerProjects.NeedsLocalRepository(row))
            {
                result[projectId] = new ProjectServing("", null, false);
                continue;
            }
            // A project that is gone leaves nothing to match, so no runner is kept from its runs.
            result[projectId] = (await access.GetProjectAsync(projectId, ct))?.Key is { } key
                ? new ProjectServing(key, row?.LocalPathHint, true)
                : new ProjectServing("", null, false);
        }
        return result;
    }
}

using Aictiq.Modules.Automation.Domain;
using Aictiq.SharedKernel.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Aictiq.Modules.Automation.Access;

/// <summary>A project-chat token's grant is its live run: the run's project, and the person who asked.</summary>
public sealed class AutomationProjectChatGrants(AutomationDbContext db) : IProjectChatGrants
{
    public async Task<ProjectChatGrant?> FindAsync(Guid tokenId, CancellationToken cancellationToken = default)
    {
        var run = await db.Runs.AsNoTracking()
            .Where(run => run.AgentTokenId == tokenId && run.Kind == RunKind.ProjectChat
                && (run.Status == RunStatus.Assigned || run.Status == RunStatus.Running))
            .Select(run => new { run.ProjectId, run.RequestedBy })
            .SingleOrDefaultAsync(cancellationToken);
        return run?.RequestedBy is { } requestedBy ? new ProjectChatGrant(run.ProjectId, requestedBy) : null;
    }
}

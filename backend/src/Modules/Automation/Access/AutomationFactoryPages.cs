using Aictiq.SharedKernel.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Aictiq.Modules.Automation.Access;

internal sealed class AutomationFactoryPages(AutomationDbContext db) : IFactoryPages
{
    public async Task<IReadOnlyList<Guid>> ListPlaybookPageIdsAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        await db.Playbooks.AsNoTracking()
            .Where(x => x.ProjectId == projectId && x.WikiPageId != null)
            .Select(x => x.WikiPageId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);
}

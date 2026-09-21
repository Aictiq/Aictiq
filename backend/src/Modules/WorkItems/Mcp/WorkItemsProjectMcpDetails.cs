using Aictiq.SharedKernel.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Aictiq.Modules.WorkItems.Mcp;

internal sealed class WorkItemsProjectMcpDetails(WorkItemsDbContext db) : IProjectMcpDetails
{
    public async Task<McpProjectPlanningDetails> GetAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var labels = await db.Labels.AsNoTracking().Where(x => x.ProjectId == projectId).OrderBy(x => x.Name)
            .Select(x => new McpProjectLabel(x.Id, x.Name, x.Color, x.Group)).ToListAsync(cancellationToken);
        var templates = (await db.ItemTemplates.AsNoTracking().Where(x => x.ProjectId == projectId).OrderBy(x => x.Name)
            .ToListAsync(cancellationToken)).Select(x => new McpProjectTemplate(x.Id, x.Name, x.Type.ToString(), x.DescriptionMarkdown,
                x.DefaultLabelIds, x.DefaultPriority?.ToString(), x.IsDefault, x.Version)).ToList();
        return new McpProjectPlanningDetails(labels, templates);
    }
}

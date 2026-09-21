namespace Aictiq.SharedKernel.Contracts;

/// <summary>Planning data attached to an MCP project response.  It is a narrow read
/// contract so Tenancy's public project tool does not acquire a reference cycle to WorkItems.</summary>
public sealed record McpProjectLabel(Guid Id, string Name, string? Color, string? Group);
public sealed record McpProjectTemplate(Guid Id, string Name, string Type, string DescriptionMarkdown,
    IReadOnlyList<Guid> DefaultLabelIds, string? DefaultPriority, bool IsDefault, uint Version);
public sealed record McpProjectPlanningDetails(IReadOnlyList<McpProjectLabel> Labels, IReadOnlyList<McpProjectTemplate> Templates);

public interface IProjectMcpDetails
{
    Task<McpProjectPlanningDetails> GetAsync(Guid projectId, CancellationToken cancellationToken = default);
}

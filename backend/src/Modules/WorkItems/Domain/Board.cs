using Aictiq.SharedKernel.Domain;

namespace Aictiq.Modules.WorkItems.Domain;

public enum BoardKind : short { Kanban, Taskboard }
public enum BoardGeneralState : short { New, Doing, Done }

/// <summary>A team-owned presentation of work. The JSON config is deliberately versioned with the board row.</summary>
public sealed class Board : TenantEntity
{
    public Guid TeamId { get; init; }
    public BoardKind Kind { get; init; }
    public required BoardConfig Config { get; set; }
    public uint Version { get; private set; }
}

public sealed record BoardConfig(IReadOnlyList<BoardColumnConfig> Columns, string Swimlane = "none",
    IReadOnlyList<string>? CardFields = null, IReadOnlyList<WorkItemType>? Types = null);
/// <summary>
/// Columns are board positions, not workflow states. Several columns may deliberately
/// use the same detailed workflow state while retaining distinct card placement.
/// </summary>
// Id has no default: OpenAPI's schema exporter reads a `Guid Id = default` parameter as a
// null default and fails converting it, which took down the whole /openapi document.
public sealed record BoardColumnConfig(string Name, IReadOnlyList<Guid> StateIds, int? WipLimit,
    Guid Id, BoardGeneralState? GeneralState = null);

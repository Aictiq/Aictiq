using Aictiq.SharedKernel.Domain;

namespace Aictiq.Modules.WorkItems.Domain;

public enum WorkflowStateCategory : short { Proposed, Active, Resolved, Completed, Removed }
public enum WorkItemType : short { Epic, Feature, Story, Task, Bug }
public enum WorkItemPriority : short { None, Low, Medium, High, Urgent }

public sealed class Workflow : TenantEntity
{
    public Guid ProjectId { get; init; }
    public required string Name { get; set; }
    public bool IsDefault { get; init; }
    public uint Version { get; private set; }
    public ICollection<WorkflowState> States { get; } = new List<WorkflowState>();
    public ICollection<WorkflowTransition> Transitions { get; } = new List<WorkflowTransition>();
}

public sealed class WorkflowState : EntityBase
{
    public Guid WorkflowId { get; init; }
    public required string Name { get; set; }
    public WorkflowStateCategory Category { get; set; }
    public int Position { get; set; }
    public string? Color { get; set; }
    public bool IsInitial { get; set; }
}

public sealed class WorkflowTransition : EntityBase
{
    public Guid WorkflowId { get; init; }
    public Guid? FromStateId { get; init; }
    public Guid ToStateId { get; init; }
}

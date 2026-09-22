using Aictiq.SharedKernel.Domain;
using Aictiq.Modules.WorkItems.Contracts;

namespace Aictiq.Modules.WorkItems.Domain;

public enum SprintState : short { Planned, Active, Completed }
public enum SprintScopeChange : short { Added, Removed }

public sealed class Sprint : TenantEntity
{
    public Guid TeamId { get; init; }
    public required string Name { get; set; }
    public string Goal { get; set; } = "";
    public DateOnly StartsOn { get; set; }
    public DateOnly EndsOn { get; set; }
    public SprintState State { get; set; }
    public bool AutoCreateNext { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public uint Version { get; private set; }

    public void Complete() => Raise(new SprintCompleted(OrganizationId, Id, TeamId));
    public void Started() => Raise(new SprintStarted(OrganizationId, Id, TeamId));
    public void Changed(Guid projectId, string actorId) => Raise(new SprintChanged(projectId, Id, TeamId, actorId));
}

/// <summary>Append-only sprint membership history with the scope metrics at that instant.</summary>
public sealed class SprintScopeLog : TenantEntity
{
    public Guid SprintId { get; init; }
    public Guid ItemId { get; init; }
    public SprintScopeChange Change { get; init; }
    public decimal? Points { get; init; }
    public decimal? RemainingHours { get; init; }
    public DateTimeOffset At { get; init; }
}

/// <summary>
/// The availability a particular person committed to for one sprint. This deliberately
/// lives beside the sprint rather than on the team: holidays and part-time weeks are a
/// property of a timebox, while a team member's normal daily capacity remains the useful
/// default for future sprints.
/// </summary>
public sealed class SprintCapacity : TenantEntity
{
    public Guid SprintId { get; init; }
    public required string UserId { get; init; }
    public decimal HoursPerDay { get; set; }
    public int DaysOff { get; set; }
}

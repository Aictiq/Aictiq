using Aictiq.SharedKernel.Domain;

namespace Aictiq.Modules.Analytics.Domain;

/// <summary>Immutable mirror of a durable workflow transition.  The integration event id
/// is the key, making an at-least-once outbox delivery harmless.</summary>
public sealed class ItemTransition : TenantEntity
{
    public Guid EventId { get; init; }
    public Guid ProjectId { get; init; }
    public Guid ItemId { get; init; }
    public Guid FromStateId { get; init; }
    public Guid ToStateId { get; init; }
    public Guid? SprintId { get; init; }
    public required string ActorId { get; init; }
    public DateTimeOffset At { get; init; }
}

/// <summary>One state sample per item per organization-local calendar day.</summary>
public sealed class ItemStateDaily : TenantEntity
{
    public Guid ItemId { get; init; }
    public DateOnly Day { get; init; }
    public Guid ProjectId { get; set; }
    public Guid StateId { get; set; }
    public Guid? SprintId { get; set; }
    public decimal? Points { get; set; }
    public decimal? EstimateHours { get; set; }
    public decimal? RemainingHours { get; set; }
    public decimal? CompletedHours { get; set; }
    public DateTimeOffset CapturedAt { get; set; }
}

/// <summary>Analytics-owned, event-idempotent scope-change stream for future metrics.</summary>
public sealed class SprintScopeLog : TenantEntity
{
    public Guid EventId { get; init; }
    public Guid SprintId { get; init; }
    public Guid ItemId { get; init; }
    public bool Added { get; init; }
    public decimal? Points { get; init; }
    public decimal? RemainingHours { get; init; }
    public DateTimeOffset At { get; init; }
}

/// <summary>A shared project dashboard or a dashboard owned by one user. Layout is JSONB
/// because widget configuration evolves independently from the dashboard container.</summary>
public sealed class Dashboard : TenantEntity
{
    public Guid ProjectId { get; init; }
    public string? OwnerUserId { get; init; }
    public required string Name { get; set; }
    public required string LayoutJson { get; set; }
    public bool IsDefault { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }
    public uint Version { get; private set; }
}

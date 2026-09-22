namespace Aictiq.SharedKernel.Persistence;

/// <summary>
/// One changed field of one entity mutation. Append-only - a DB trigger rejects
/// UPDATE/DELETE. Owned by the Identity module's migrations; mapped read-only into
/// every module context so the interceptor can write rows transactionally.
/// </summary>
public sealed class AuditLogEntry
{
    public const string CreatedMarker = "(created)";
    public const string DeletedMarker = "(deleted)";

    public Guid Id { get; init; } = Guid.CreateVersion7();
    public required string EntityType { get; init; }
    public required string EntityId { get; init; }
    public required string Field { get; init; }
    public string? OldValue { get; init; }
    public string? NewValue { get; init; }
    public string? UserId { get; init; }
    /// <summary>Nullable for historic/global security events. New tenant mutations stamp this
    /// value so organization administrators can query without inferring ownership.</summary>
    public Guid? OrganizationId { get; init; }
    public DateTimeOffset At { get; init; }
}

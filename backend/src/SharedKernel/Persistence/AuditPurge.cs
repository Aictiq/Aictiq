using Microsoft.EntityFrameworkCore;

namespace Aictiq.SharedKernel.Persistence;

/// <summary>
/// The only ways past the audit log's append-only trigger besides retention, for records that
/// were deleted for good: an audit row carries the old and new value of every field, so a
/// deleted item's title and description would otherwise outlive the item in the log.
///
/// Each calls a function that holds the transaction-local flag for exactly its own DELETE
/// (<c>shared.purge_audit_entities</c>, <c>shared.purge_audit_organization</c>, owned by
/// Identity's migrations). Both are idempotent: purging twice deletes nothing the second time.
/// </summary>
public static class AuditPurge
{
    /// <summary>
    /// Deletes the audit trail of the named entities. An id also matches a composite key it
    /// leads (<c>{teamId}/{userId}</c> for a team member), so passing a team's id removes its
    /// memberships' rows as well.
    /// </summary>
    public static async Task EntitiesAsync(DbContext db, Guid organizationId, string entityType,
        IEnumerable<Guid> entityIds, CancellationToken cancellationToken)
    {
        var ids = entityIds.Select(id => id.ToString()).Distinct().ToArray();
        if (ids.Length == 0) return;
        foreach (var chunk in ids.Chunk(5_000))
            await db.Database.ExecuteSqlAsync(
                $"SELECT shared.purge_audit_entities({organizationId}, {entityType}, {chunk})", cancellationToken);
    }

    /// <summary>Deletes every audit row an organization has.</summary>
    public static Task OrganizationAsync(DbContext db, Guid organizationId, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlAsync($"SELECT shared.purge_audit_organization({organizationId})", cancellationToken);

    /// <summary>
    /// The one row a deleted record leaves in its organization's audit log: what it was, who
    /// deleted it and when — never its content. An organization that still exists must be able
    /// to answer "where did project WEB go"; everything the record said is gone with it.
    /// </summary>
    public static AuditLogEntry Tombstone(Guid organizationId, string entityType, Guid entityId, string label,
        string? userId, DateTimeOffset at) => new()
    {
        OrganizationId = organizationId,
        EntityType = entityType,
        EntityId = entityId.ToString(),
        Field = AuditLogEntry.DeletedMarker,
        NewValue = label,
        UserId = userId,
        At = at,
    };
}

using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Aictiq.SharedKernel.Domain;
using Aictiq.SharedKernel.Tenancy;

namespace Aictiq.SharedKernel.Persistence;

/// <summary>
/// Field-level audit of every IAudited entity mutation, written to audit.audit_log in
/// the same transaction as the mutation itself.
/// </summary>
public sealed class AuditingInterceptor(ICurrentUser currentUser, ICurrentTenant tenant, TimeProvider timeProvider) : SaveChangesInterceptor
{
    // Secrets that must never land in the audit log, plus per-request security counters
    // (AccessFailedCount/LockoutEnd change on every failed login) that would drown the
    // audit trail in noise.
    private static readonly HashSet<string> SensitiveProperties =
        [
            "PasswordHash", "SecurityStamp", "ConcurrencyStamp", "TokenHash", "AccessFailedCount", "LockoutEnd",
            // Webhook signing secrets: the hash identifies one, the protected form is the
            // credential itself. Neither belongs in a log an organization admin can export.
            "SecretHash", "SecretProtected",
        ];

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        CollectAuditEntries(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        CollectAuditEntries(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void CollectAuditEntries(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        var rows = new List<AuditLogEntry>();

        foreach (var entry in context.ChangeTracker.Entries().ToList())
        {
            if (entry.Entity is not IAudited ||
                entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
            {
                continue;
            }

            var entityType = entry.Metadata.ClrType.Name;
            var entityId = GetEntityId(entry);

            switch (entry.State)
            {
                case EntityState.Added:
                    rows.Add(NewRow(entityType, entityId, AuditLogEntry.CreatedMarker, null, null, now));
                    foreach (var property in entry.Properties)
                    {
                        if (property.CurrentValue is not null && !property.Metadata.IsPrimaryKey() &&
                            !SensitiveProperties.Contains(property.Metadata.Name))
                        {
                            rows.Add(NewRow(entityType, entityId, property.Metadata.Name,
                                null, Stringify(property.CurrentValue), now));
                        }
                    }
                    break;

                case EntityState.Modified:
                    foreach (var property in entry.Properties)
                    {
                        if (property.IsModified && !Equals(property.OriginalValue, property.CurrentValue) &&
                            !SensitiveProperties.Contains(property.Metadata.Name))
                        {
                            rows.Add(NewRow(entityType, entityId, property.Metadata.Name,
                                Stringify(property.OriginalValue), Stringify(property.CurrentValue), now));
                        }
                    }
                    break;

                case EntityState.Deleted:
                    rows.Add(NewRow(entityType, entityId, AuditLogEntry.DeletedMarker, null, null, now));
                    break;
            }
        }

        if (rows.Count > 0)
        {
            context.Set<AuditLogEntry>().AddRange(rows);
        }
    }

    private AuditLogEntry NewRow(
        string entityType, string entityId, string field,
        string? oldValue, string? newValue, DateTimeOffset at) => new()
    {
        EntityType = entityType,
        EntityId = entityId,
        Field = field,
        OldValue = oldValue,
        NewValue = newValue,
        UserId = currentUser.UserId,
        OrganizationId = tenant.OrganizationId,
        At = at
    };

    private static string GetEntityId(EntityEntry entry)
    {
        var key = entry.Metadata.FindPrimaryKey();
        if (key is null)
        {
            return "?";
        }

        return string.Join("/", key.Properties.Select(p => Stringify(entry.Property(p.Name).CurrentValue) ?? "?"));
    }

    private static string? Stringify(object? value) => value switch
    {
        null => null,
        DateTimeOffset dto => dto.ToString("O"),
        DateTime dt => dt.ToString("O"),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString()
    };
}

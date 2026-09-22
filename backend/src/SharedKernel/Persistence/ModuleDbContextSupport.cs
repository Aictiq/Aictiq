using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Aictiq.SharedKernel.Domain;
using Aictiq.SharedKernel.Events;
using Aictiq.SharedKernel.Outbox;
using Aictiq.SharedKernel.Tenancy;

namespace Aictiq.SharedKernel.Persistence;

/// <summary>
/// Shared plumbing for module contexts. Lives outside ModuleDbContext because the
/// Identity context must inherit IdentityDbContext and can't share the base class.
/// </summary>
public static class ModuleDbContextSupport
{
    public static void ConfigureSharedInfrastructure(ModelBuilder modelBuilder, bool owns)
    {
        modelBuilder.Entity<OutboxMessage>(b =>
        {
            b.ToTable("outbox_messages", "shared", t =>
            {
                if (!owns) t.ExcludeFromMigrations();
            });
            b.HasKey(x => x.Id);
            b.Property(x => x.Type).HasMaxLength(512);
            b.Property(x => x.Payload).HasColumnType("jsonb");
            // Covers exactly the processor's claim query: unprocessed and not dead-lettered.
            b.HasIndex(x => x.OccurredAt)
                .HasFilter("processed_at IS NULL AND dead_lettered_at IS NULL")
                .HasDatabaseName("ix_outbox_messages_pending");
            b.HasIndex(x => x.DeadLetteredAt)
                .HasFilter("dead_lettered_at IS NOT NULL")
                .HasDatabaseName("ix_outbox_messages_dead_lettered");
        });

        modelBuilder.Entity<AuditLogEntry>(b =>
        {
            b.ToTable("audit_log", "audit", t =>
            {
                if (!owns) t.ExcludeFromMigrations();
            });
            b.HasKey(x => x.Id);
            b.Property(x => x.EntityType).HasMaxLength(128);
            // 128, not 64: a composite key is stringified as "part/part", and two uuids
            // (tenancy.organization_members) already need 73.
            b.Property(x => x.EntityId).HasMaxLength(128);
            b.Property(x => x.Field).HasMaxLength(128);
            b.Property(x => x.UserId).HasMaxLength(64);
            b.HasIndex(x => new { x.OrganizationId, x.At });
            b.HasIndex(x => new { x.EntityType, x.EntityId, x.At });
            b.HasIndex(x => new { x.UserId, x.At });
        });
    }

    /// <summary>
    /// Applies the organization filter to every <see cref="TenantEntity"/> in the model.
    ///
    /// Discovering them by base type rather than listing them is the point: a new tenant
    /// table gets isolation by deriving from <c>TenantEntity</c>, and there is no
    /// per-entity call for someone to forget. The filter closes over a property of the
    /// context, so EF re-reads the tenant on every query rather than baking in whatever
    /// it was when the model was built.
    ///
    /// When no tenant is in scope the comparison is against null, which matches no row -
    /// so a code path that forgets to establish a tenant sees nothing rather than
    /// everything.
    /// </summary>
    public static void ApplyTenantFilters(
        ModelBuilder modelBuilder, Expression<Func<Guid?>> currentOrganizationId)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (!typeof(TenantEntity).IsAssignableFrom(entityType.ClrType))
            {
                continue;
            }

            // e => (Guid?)e.OrganizationId == currentOrganizationId
            var entity = Expression.Parameter(entityType.ClrType, "e");
            var organizationId = Expression.Property(entity, nameof(TenantEntity.OrganizationId));
            var filter = Expression.Lambda(
                Expression.Equal(
                    Expression.Convert(organizationId, typeof(Guid?)),
                    currentOrganizationId.Body),
                entity);

            modelBuilder.Entity(entityType.ClrType).HasQueryFilter(filter);
        }
    }

    /// <summary>
    /// Stamps the tenant on new rows and refuses any write that would cross a tenant
    /// boundary. Call before base.SaveChangesAsync.
    ///
    /// Modified rows need no check: the query filter means the caller could only have
    /// loaded rows from their own tenant in the first place. New rows are the gap - a
    /// caller can construct one with any <c>OrganizationId</c> it likes - so an explicit
    /// value that disagrees with the scope is rejected rather than trusted.
    /// </summary>
    public static void StampTenant(DbContext context, Guid? currentOrganizationId)
    {
        foreach (var entry in context.ChangeTracker.Entries<TenantEntity>())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified))
            {
                continue;
            }

            if (currentOrganizationId is not { } tenant)
            {
                throw TenantMissingException.NoTenant(entry.Entity.GetType().Name);
            }

            if (entry.Entity.OrganizationId == Guid.Empty)
            {
                entry.Entity.OrganizationId = tenant;
            }
            else if (entry.Entity.OrganizationId != tenant)
            {
                throw TenantMissingException.WrongTenant(
                    entry.Entity.GetType().Name, entry.Entity.OrganizationId, tenant);
            }
        }
    }

    /// <summary>
    /// Drains domain events from tracked entities and stages integration events as
    /// outbox rows in the pending transaction. Call before base.SaveChangesAsync.
    /// </summary>
    public static List<IDomainEvent> StageDomainEvents(DbContext context)
    {
        var domainEvents = new List<IDomainEvent>();
        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.Entity is IHasDomainEvents { DomainEvents.Count: > 0 } entity)
            {
                domainEvents.AddRange(entity.DomainEvents);
                entity.ClearDomainEvents();
            }
        }

        foreach (var integrationEvent in domainEvents.OfType<IIntegrationEvent>())
        {
            context.Set<OutboxMessage>().Add(OutboxMessage.From(integrationEvent));
        }

        return domainEvents;
    }

    /// <summary>Dispatches non-integration events in-process. Call after a successful save.</summary>
    public static async Task DispatchAsync(
        IDomainEventDispatcher? dispatcher, IReadOnlyList<IDomainEvent> domainEvents, CancellationToken cancellationToken)
    {
        if (dispatcher is null)
        {
            return;
        }

        foreach (var domainEvent in domainEvents)
        {
            if (domainEvent is not IIntegrationEvent)
            {
                await dispatcher.DispatchAsync(domainEvent, cancellationToken);
            }
        }
    }
}

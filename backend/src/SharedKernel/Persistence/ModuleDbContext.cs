using Microsoft.EntityFrameworkCore;
using Aictiq.SharedKernel.Events;
using Aictiq.SharedKernel.Tenancy;

namespace Aictiq.SharedKernel.Persistence;

/// <summary>
/// Base for every module's DbContext: pins the module schema, maps the shared
/// infrastructure tables (outbox, audit log), turns integration events into outbox rows
/// inside the same transaction, and dispatches plain domain events post-commit.
/// </summary>
public abstract class ModuleDbContext : DbContext
{
    private readonly IDomainEventDispatcher? _dispatcher;
    private readonly ICurrentTenant? _currentTenant;

    protected ModuleDbContext(
        DbContextOptions options,
        IDomainEventDispatcher? dispatcher = null,
        ICurrentTenant? currentTenant = null)
        : base(options)
    {
        _dispatcher = dispatcher;
        _currentTenant = currentTenant;
    }

    /// <summary>
    /// Read by the tenant query filter on every query. It is a property, not a captured
    /// value, precisely so a filter built once at model time still reflects the tenant of
    /// the request being served.
    /// </summary>
    protected Guid? CurrentOrganizationId => _currentTenant?.OrganizationId;

    protected abstract string Schema { get; }

    /// <summary>Only one module context migrates the shared/audit tables (here: Identity).</summary>
    protected virtual bool OwnsSharedInfrastructure => false;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        ModuleDbContextSupport.ConfigureSharedInfrastructure(modelBuilder, OwnsSharedInfrastructure);
        ModuleDbContextSupport.ApplyTenantFilters(modelBuilder, () => CurrentOrganizationId);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess) =>
        throw new NotSupportedException("Use SaveChangesAsync - synchronous SaveChanges skips domain event dispatch.");

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        ModuleDbContextSupport.StampTenant(this, CurrentOrganizationId);
        var domainEvents = ModuleDbContextSupport.StageDomainEvents(this);
        var result = await base.SaveChangesAsync(cancellationToken);
        await ModuleDbContextSupport.DispatchAsync(_dispatcher, domainEvents, cancellationToken);
        return result;
    }
}

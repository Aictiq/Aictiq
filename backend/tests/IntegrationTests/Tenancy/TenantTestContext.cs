using Microsoft.EntityFrameworkCore;
using Aictiq.SharedKernel.Domain;
using Aictiq.SharedKernel.Persistence;
using Aictiq.SharedKernel.Tenancy;

namespace Aictiq.IntegrationTests.Tenancy;

/// <summary>
/// A throwaway tenant table. Testing isolation against a real module's entity would tie
/// these tests to that module's rules; what is under test is the SharedKernel machinery,
/// so it gets the smallest possible thing that derives from <see cref="TenantEntity"/>.
/// </summary>
public sealed class TenantThing : TenantEntity
{
    public required string Name { get; set; }
}

/// <summary>A row that is deliberately *not* tenant-scoped, to prove the filter is selective.</summary>
public sealed class GlobalThing : EntityBase
{
    public required string Name { get; set; }
}

public sealed class TenantTestDbContext(
    DbContextOptions<TenantTestDbContext> options,
    ICurrentTenant? currentTenant = null)
    : ModuleDbContext(options, dispatcher: null, currentTenant)
{
    protected override string Schema => "tenant_test";

    public DbSet<TenantThing> Things => Set<TenantThing>();
    public DbSet<GlobalThing> GlobalThings => Set<GlobalThing>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<TenantThing>(b =>
        {
            b.ToTable("things");
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).HasMaxLength(200);
            // Tenant-scoped uniqueness always includes the organization, or one tenant
            // could deny another the use of a name.
            b.HasIndex(x => new { x.OrganizationId, x.Name }).IsUnique();
        });

        modelBuilder.Entity<GlobalThing>(b =>
        {
            b.ToTable("global_things");
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).HasMaxLength(200);
        });
    }
}

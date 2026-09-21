using Microsoft.EntityFrameworkCore;
using Aictiq.Modules.Analytics.Domain;
using Aictiq.SharedKernel.Events;
using Aictiq.SharedKernel.Persistence;
using Aictiq.SharedKernel.Tenancy;

namespace Aictiq.Modules.Analytics;

public sealed class AnalyticsDbContext(DbContextOptions<AnalyticsDbContext> options,
    IDomainEventDispatcher? dispatcher = null, ICurrentTenant? currentTenant = null)
    : ModuleDbContext(options, dispatcher, currentTenant)
{
    protected override string Schema => "analytics";
    public DbSet<ItemTransition> ItemTransitions => Set<ItemTransition>();
    public DbSet<ItemStateDaily> ItemStateDaily => Set<ItemStateDaily>();
    public DbSet<SprintScopeLog> SprintScopeLog => Set<SprintScopeLog>();
    public DbSet<Dashboard> Dashboards => Set<Dashboard>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<ItemTransition>(builder =>
        {
            builder.ToTable("item_transitions"); builder.Ignore(row => row.Id); builder.HasKey(row => row.EventId);
            builder.Property(row => row.ActorId).HasMaxLength(64);
            builder.HasIndex(row => new { row.OrganizationId, row.ItemId, row.At });
            builder.HasIndex(row => new { row.OrganizationId, row.ProjectId, row.At });
        });
        modelBuilder.Entity<ItemStateDaily>(builder =>
        {
            builder.ToTable("item_state_daily"); builder.Ignore(row => row.Id);
            builder.HasKey(row => new { row.OrganizationId, row.ItemId, row.Day });
            builder.Property(row => row.Points).HasPrecision(10, 2);
            builder.Property(row => row.EstimateHours).HasPrecision(10, 2);
            builder.Property(row => row.RemainingHours).HasPrecision(10, 2);
            builder.Property(row => row.CompletedHours).HasPrecision(10, 2);
            builder.HasIndex(row => new { row.OrganizationId, row.Day, row.ProjectId });
        });
        modelBuilder.Entity<SprintScopeLog>(builder =>
        {
            builder.ToTable("sprint_scope_log"); builder.Ignore(row => row.Id); builder.HasKey(row => row.EventId);
            builder.Property(row => row.Points).HasPrecision(10, 2);
            builder.Property(row => row.RemainingHours).HasPrecision(10, 2);
            builder.HasIndex(row => new { row.OrganizationId, row.SprintId, row.At });
        });
        modelBuilder.Entity<Dashboard>(builder =>
        {
            builder.ToTable("dashboards");
            builder.Property(row => row.OwnerUserId).HasMaxLength(64);
            builder.Property(row => row.Name).HasMaxLength(100);
            builder.Property(row => row.LayoutJson).HasColumnType("jsonb");
            builder.Property(row => row.Version).IsRowVersion();
            builder.HasIndex(row => new { row.ProjectId, row.OwnerUserId, row.Name }).IsUnique();
            builder.HasIndex(row => row.ProjectId).HasFilter("is_default").IsUnique();
            builder.ToTable(table => table.HasCheckConstraint("ck_dashboards_name", "length(btrim(name)) > 0"));
        });
    }
}

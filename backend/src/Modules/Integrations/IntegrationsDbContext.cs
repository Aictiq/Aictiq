using Microsoft.EntityFrameworkCore;
using Aictiq.Modules.Integrations.Domain;
using Aictiq.SharedKernel.Events;
using Aictiq.SharedKernel.Persistence;
using Aictiq.SharedKernel.Tenancy;

namespace Aictiq.Modules.Integrations;

/// <summary>Schema <c>integrations</c>: GitHub App installations, bindings and inbox.</summary>
public sealed class IntegrationsDbContext(
    DbContextOptions<IntegrationsDbContext> options,
    IDomainEventDispatcher? dispatcher = null,
    ICurrentTenant? currentTenant = null)
    : ModuleDbContext(options, dispatcher, currentTenant)
{
    protected override string Schema => "integrations";

    public DbSet<GitHubInstallation> GitHubInstallations => Set<GitHubInstallation>();
    public DbSet<RepoBinding> RepoBindings => Set<RepoBinding>();
    public DbSet<GitHubDelivery> GitHubDeliveries => Set<GitHubDelivery>();
    public DbSet<WebhookSubscription> WebhookSubscriptions => Set<WebhookSubscription>();
    public DbSet<WebhookDelivery> WebhookDeliveries => Set<WebhookDelivery>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<GitHubInstallation>(b =>
        {
            b.ToTable("github_installations");
            b.Property(x => x.AccountLogin).HasMaxLength(256);
            b.Property(x => x.AccountType).HasMaxLength(32);
            b.Property(x => x.Status).HasConversion<short>();
            b.HasIndex(x => x.InstallationId).IsUnique();
            b.HasIndex(x => new { x.OrganizationId, x.Status });
        });

        modelBuilder.Entity<RepoBinding>(b =>
        {
            b.ToTable("repo_bindings");
            b.Property(x => x.FullName).HasMaxLength(512);
            // A repository is bindable to different projects, but only once to each.
            b.HasIndex(x => new { x.RepoId, x.ProjectId }).IsUnique()
                .HasDatabaseName("ux_repo_bindings_repo_project");
            b.HasIndex(x => new { x.OrganizationId, x.ProjectId });
            b.HasIndex(x => x.InstallationId);
        });

        modelBuilder.Entity<GitHubDelivery>(b =>
        {
            b.ToTable("github_deliveries");
            b.HasKey(x => x.DeliveryId);
            b.Property(x => x.DeliveryId).HasMaxLength(128);
            b.Property(x => x.EventType).HasMaxLength(128);
            b.Property(x => x.Payload).HasColumnType("jsonb");
            b.HasIndex(x => x.ReceivedAt);
            b.HasIndex(x => x.InstallationId);
            b.HasIndex(x => x.OrganizationId);
        });

        modelBuilder.Entity<WebhookSubscription>(b =>
        {
            b.ToTable("webhook_subscriptions");
            b.Property(x => x.Url).HasMaxLength(2048);
            b.Property(x => x.SecretHash).HasMaxLength(64);
            b.Property(x => x.SecretProtected).HasMaxLength(4096);
            b.Property(x => x.Events).HasColumnType("text[]");
            b.HasIndex(x => new { x.OrganizationId, x.ProjectId, x.Active });
        });

        modelBuilder.Entity<WebhookDelivery>(b =>
        {
            b.ToTable("webhook_deliveries");
            b.Property(x => x.EventName).HasMaxLength(128);
            b.Property(x => x.Payload).HasColumnType("jsonb");
            b.Property(x => x.Status).HasMaxLength(16);
            b.Property(x => x.ResponseExcerpt).HasMaxLength(2000);
            b.Property(x => x.LastError).HasMaxLength(1000);
            b.HasIndex(x => new { x.SubscriptionId, x.EventId }).IsUnique();
            b.HasIndex(x => new { x.Status, x.NextAttemptAt });
        });
    }
}

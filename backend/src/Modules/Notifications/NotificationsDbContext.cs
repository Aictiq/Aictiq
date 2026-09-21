using Microsoft.EntityFrameworkCore;
using Aictiq.Modules.Notifications.Domain;
using Aictiq.SharedKernel.Events;
using Aictiq.SharedKernel.Persistence;
using Aictiq.SharedKernel.Tenancy;

namespace Aictiq.Modules.Notifications;

/// <summary>
/// Schema <c>notify</c>. Owns the email queue, in-app notifications and per-user
/// delivery preferences.
/// </summary>
public sealed class NotificationsDbContext(
    DbContextOptions<NotificationsDbContext> options, IDomainEventDispatcher? dispatcher = null,
    ICurrentTenant? currentTenant = null)
    : ModuleDbContext(options, dispatcher, currentTenant)
{
    protected override string Schema => "notify";

    public DbSet<EmailOutboxMessage> EmailOutbox => Set<EmailOutboxMessage>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<NotificationPreference> Preferences => Set<NotificationPreference>();
    public DbSet<NotificationDigest> Digests => Set<NotificationDigest>();
    public DbSet<NotificationPresence> Presence => Set<NotificationPresence>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<EmailOutboxMessage>(b =>
        {
            b.ToTable("email_outbox");

            // "to" is a reserved word in Postgres; the column carries the qualifier the
            // data model's shorthand leaves out.
            b.Property(m => m.ToAddress).HasMaxLength(320);
            b.Property(m => m.Subject).HasMaxLength(500);
            b.Property(m => m.Template).HasMaxLength(64);
            b.Property(m => m.Status).HasMaxLength(16);
            b.Property(m => m.LastError).HasMaxLength(1000);

            // Exactly the delivery sweep's claim query, and only the rows it can claim.
            b.HasIndex(m => m.SendAfter)
                .HasFilter("status = 'pending'")
                .HasDatabaseName("ix_email_outbox_pending");

            b.ToTable(t => t.HasCheckConstraint("ck_email_outbox_status",
                "status IN ('pending','sent','failed','skipped')"));
            // A sent row without a timestamp (or the reverse) would make "when did this
            // go out" unanswerable, so the two can never disagree.
            b.ToTable(t => t.HasCheckConstraint("ck_email_outbox_sent_consistency",
                "(status = 'sent') = (sent_at IS NOT NULL)"));
            b.ToTable(t => t.HasCheckConstraint("ck_email_outbox_recipient_not_blank",
                "length(btrim(to_address)) > 0"));
        });
        modelBuilder.Entity<Notification>(b =>
        {
            b.ToTable("notifications"); b.Property(x => x.UserId).HasMaxLength(64); b.Property(x => x.ItemKey).HasMaxLength(32); b.Property(x => x.Message).HasMaxLength(500); b.Property(x => x.Kind).HasConversion<short>();
            b.HasIndex(x => new { x.UserId, x.EventId }).IsUnique(); b.HasIndex(x => new { x.UserId, x.ReadAt, x.CreatedAt });
        });
        modelBuilder.Entity<NotificationPreference>(b =>
        {
            b.ToTable("preferences"); b.Property(x => x.UserId).HasMaxLength(64); b.Property(x => x.Kind).HasConversion<short>(); b.Property(x => x.EmailMode).HasConversion<short>(); b.HasIndex(x => new { x.UserId, x.Kind }).IsUnique();
        });
        modelBuilder.Entity<NotificationDigest>(b =>
        {
            b.ToTable("digests"); b.Property(x => x.UserId).HasMaxLength(64); b.HasIndex(x => x.UserId).IsUnique();
        });
        modelBuilder.Entity<NotificationPresence>(b =>
        {
            b.ToTable("presence"); b.Property(x => x.UserId).HasMaxLength(64); b.HasIndex(x => x.UserId).IsUnique();
        });
    }
}

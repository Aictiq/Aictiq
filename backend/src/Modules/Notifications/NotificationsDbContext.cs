using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Aictiq.Modules.Notifications.Delivery;
using Aictiq.Modules.Notifications.Domain;
using Aictiq.SharedKernel.Events;
using Aictiq.SharedKernel.Persistence;
using Aictiq.SharedKernel.Tenancy;

namespace Aictiq.Modules.Notifications;

/// <summary>
/// Schema <c>notify</c>. Owns the email and chat queues, in-app notifications, chat
/// channels and per-user and per-organization delivery preferences.
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
    public DbSet<UserChatChannel> UserChannels => Set<UserChatChannel>();
    public DbSet<OrgChatChannel> OrgChannels => Set<OrgChatChannel>();
    public DbSet<ChatConnectCode> ConnectCodes => Set<ChatConnectCode>();
    public DbSet<OrgNotificationDefault> OrgDefaults => Set<OrgNotificationDefault>();
    public DbSet<ChatOutboxMessage> ChatOutbox => Set<ChatOutboxMessage>();
    public DbSet<ChatDigestEntry> ChatDigestEntries => Set<ChatDigestEntry>();
    public DbSet<DataProtectionKeyRow> DataProtectionKeys => Set<DataProtectionKeyRow>();

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
            b.Property(x => x.TelegramMode).HasConversion<short?>(); b.Property(x => x.SlackMode).HasConversion<short?>(); b.Property(x => x.DiscordMode).HasConversion<short?>();
        });
        modelBuilder.Entity<UserChatChannel>(b =>
        {
            b.ToTable("user_channels"); b.Property(x => x.UserId).HasMaxLength(64); b.Property(x => x.Type).HasConversion<short>(); b.Property(x => x.Status).HasConversion<short>();
            b.Property(x => x.TargetHint).HasMaxLength(200); b.Property(x => x.LastError).HasMaxLength(500);
            b.HasIndex(x => new { x.UserId, x.Type }).IsUnique();
        });
        modelBuilder.Entity<OrgChatChannel>(b =>
        {
            b.ToTable("org_channels"); b.Property(x => x.Name).HasMaxLength(100); b.Property(x => x.Type).HasConversion<short>(); b.Property(x => x.Status).HasConversion<short>();
            b.Property(x => x.TargetHint).HasMaxLength(200); b.Property(x => x.LastError).HasMaxLength(500);
            b.Property(x => x.Modes).HasColumnType("jsonb").HasConversion(
                modes => JsonSerializer.Serialize(modes.ToDictionary(m => (short)m.Key, m => (short)m.Value), (JsonSerializerOptions?)null),
                json => JsonSerializer.Deserialize<Dictionary<short, short>>(json, (JsonSerializerOptions?)null)!
                    .ToDictionary(m => (NotificationKind)m.Key, m => (EmailNotificationMode)m.Value),
                new ValueComparer<Dictionary<NotificationKind, EmailNotificationMode>>(
                    (a, b) => a!.Count == b!.Count && !a.Except(b).Any(),
                    modes => modes.Aggregate(0, (hash, m) => HashCode.Combine(hash, m.Key, m.Value)),
                    modes => new Dictionary<NotificationKind, EmailNotificationMode>(modes)));
            b.HasIndex(x => x.OrganizationId);
        });
        modelBuilder.Entity<ChatConnectCode>(b =>
        {
            b.ToTable("chat_connect_codes"); b.Property(x => x.CodeHash).HasMaxLength(64); b.HasIndex(x => x.CodeHash).IsUnique(); b.HasIndex(x => x.ChannelId);
        });
        modelBuilder.Entity<OrgNotificationDefault>(b =>
        {
            b.ToTable("org_defaults"); b.Property(x => x.Kind).HasConversion<short>();
            b.Property(x => x.EmailMode).HasConversion<short?>(); b.Property(x => x.TelegramMode).HasConversion<short?>();
            b.Property(x => x.SlackMode).HasConversion<short?>(); b.Property(x => x.DiscordMode).HasConversion<short?>();
            b.HasIndex(x => new { x.OrganizationId, x.Kind }).IsUnique();
        });
        modelBuilder.Entity<ChatOutboxMessage>(b =>
        {
            b.ToTable("chat_outbox"); b.Property(x => x.Status).HasMaxLength(16); b.Property(x => x.LastError).HasMaxLength(500);
            b.Property(x => x.Kind).HasConversion<short?>();
            b.HasIndex(x => x.SendAfter).HasFilter("status = 'pending'").HasDatabaseName("ix_chat_outbox_pending");
            b.HasIndex(x => x.ChannelId);
            b.ToTable(t => t.HasCheckConstraint("ck_chat_outbox_status", "status IN ('pending','sent','failed','skipped')"));
        });
        modelBuilder.Entity<DataProtectionKeyRow>(b =>
        {
            b.ToTable("data_protection_keys"); b.Property(x => x.Id).UseIdentityAlwaysColumn(); b.Property(x => x.FriendlyName).HasMaxLength(200);
        });
        modelBuilder.Entity<ChatDigestEntry>(b =>
        {
            b.ToTable("chat_digest_entries"); b.Property(x => x.Line).HasMaxLength(1000); b.HasIndex(x => new { x.ChannelId, x.CreatedAt });
            b.Property(x => x.Kind).HasConversion<short?>();
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

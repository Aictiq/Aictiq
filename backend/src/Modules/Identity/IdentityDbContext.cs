using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Aictiq.Modules.Identity.Domain;
using Aictiq.SharedKernel.Events;
using Aictiq.SharedKernel.Persistence;

namespace Aictiq.Modules.Identity;

/// <summary>
/// Inherits IdentityDbContext (not ModuleDbContext) for the Identity table mappings;
/// the shared module plumbing comes from ModuleDbContextSupport. This context OWNS the
/// shared infrastructure tables (audit.audit_log, shared.outbox_messages) because
/// Identity is the one module every derived project keeps.
/// </summary>
public sealed class IdentityDbContext(
    DbContextOptions<IdentityDbContext> options, IDomainEventDispatcher? dispatcher = null)
    : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<PersonalAccessToken> PersonalAccessTokens => Set<PersonalAccessToken>();
    public DbSet<UserSecurityToken> UserSecurityTokens => Set<UserSecurityToken>();
    public DbSet<UserOnboarding> UserOnboarding => Set<UserOnboarding>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.HasDefaultSchema("identity");
        ModuleDbContextSupport.ConfigureSharedInfrastructure(builder, owns: true);

        builder.Entity<ApplicationUser>(b =>
        {
            b.Property(u => u.FirstName).HasMaxLength(100);
            b.Property(u => u.LastName).HasMaxLength(100);
            b.Property(u => u.AgentOwnerUserId).HasMaxLength(64);
            b.Property(u => u.AvatarKey).HasMaxLength(512);
            b.Property(u => u.TimeZone).HasMaxLength(64);

            // "One account per address" was until now a UserManager validator - an
            // application check, and therefore a race: two registrations (or an email
            // change and a registration) that interleave both pass it. The index is the
            // guarantee; the validator stays for the error message.
            b.HasIndex(u => u.NormalizedEmail).IsUnique().HasDatabaseName("ux_users_normalized_email");

            // An agent always has an owner and a person never does. The pair is what makes
            // "who let this thing into the repository" answerable, so it is a constraint
            // rather than a convention.
            b.ToTable(t => t.HasCheckConstraint("ck_users_agent_has_owner",
                "is_agent = (agent_owner_user_id IS NOT NULL)"));

            // "Which agents does this person own" - asked when they leave.
            b.HasIndex(u => u.AgentOwnerUserId).HasFilter("agent_owner_user_id IS NOT NULL");

            b.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(u => u.AgentOwnerUserId)
                // Restrict, not cascade: deleting a person must not silently delete the
                // agents they were answerable for. Ownership is reassigned instead.
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<PersonalAccessToken>(b =>
        {
            b.ToTable("personal_access_tokens");
            b.HasKey(t => t.Id);

            b.Property(t => t.UserId).HasMaxLength(64);
            b.Property(t => t.Name).HasMaxLength(PersonalAccessToken.MaxNameLength);
            b.Property(t => t.TokenHash).HasMaxLength(64);
            b.Property(t => t.Prefix).HasMaxLength(PersonalAccessToken.PrefixLength);
            b.Property(t => t.Scopes).HasColumnType("text[]");

            // The lookup every authenticated CLI, MCP and agent request makes.
            b.HasIndex(t => t.TokenHash).IsUnique();
            b.HasIndex(t => t.UserId);

            b.ToTable(t => t.HasCheckConstraint("ck_personal_access_tokens_name_not_blank",
                "length(btrim(name)) > 0"));
            // The scope set is a narrowing, so an unknown scope is not a smaller
            // permission - it is a typo that would silently widen nothing and confuse
            // everyone reading the row.
            b.ToTable(t => t.HasCheckConstraint("ck_personal_access_tokens_scopes",
                "scopes <@ ARRAY['read','write','admin','mcp']::text[]"));

            b.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(t => t.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<RefreshToken>(b =>
        {
            b.Property(t => t.UserId).HasMaxLength(64);
            b.Property(t => t.TokenHash).HasMaxLength(64);
            b.Property(t => t.UserAgent).HasMaxLength(RefreshToken.MaxUserAgentLength);
            b.HasIndex(t => t.TokenHash).IsUnique();
            b.HasIndex(t => t.UserId);
            b.HasIndex(t => t.FamilyId);
        });

        builder.Entity<UserSecurityToken>(b =>
        {
            b.ToTable("user_security_tokens");
            b.HasKey(t => t.Id);

            b.Property(t => t.UserId).HasMaxLength(64);
            b.Property(t => t.TokenHash).HasMaxLength(64);
            b.Property(t => t.NewEmail).HasMaxLength(256);

            b.HasIndex(t => t.TokenHash).IsUnique();

            // One live link per person per purpose. Asking for a new one therefore has to
            // retire the old one, which is the behaviour we want: only the newest link
            // works, and a link that went astray stops working when a new one is minted.
            b.HasIndex(t => new { t.UserId, t.Purpose })
                .IsUnique()
                .HasFilter("used_at IS NULL")
                .HasDatabaseName("ux_user_security_tokens_live");

            // An email-change token carries the address it is confirming and nothing else
            // does - a reset token with an address in it would be a different feature that
            // nobody wrote.
            b.ToTable(t => t.HasCheckConstraint("ck_user_security_tokens_new_email",
                "(purpose = 1) = (new_email IS NOT NULL)"));
            b.ToTable(t => t.HasCheckConstraint("ck_user_security_tokens_new_email_lower",
                "new_email IS NULL OR new_email = lower(new_email)"));

            b.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(t => t.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<UserOnboarding>(b =>
        {
            b.ToTable("user_onboarding");
            b.HasKey(t => t.UserId);

            b.Property(t => t.UserId).HasMaxLength(64);
            b.Property(t => t.Status).HasMaxLength(20);
            b.Property(t => t.LastStepId).HasMaxLength(40);
            b.Property(t => t.Version).IsRowVersion();

            // The status set and the pairing of "completed" with a completion timestamp are
            // the database's to keep honest - the endpoint validates for a good message,
            // these are the guarantee. Step ids are validated at the API boundary only:
            // they belong to the tour's content, which the frontend owns.
            b.ToTable(t => t.HasCheckConstraint("ck_user_onboarding_status",
                "status IN ('not_started', 'in_progress', 'deferred', 'dismissed', 'completed')"));
            b.ToTable(t => t.HasCheckConstraint("ck_user_onboarding_tour_version", "tour_version >= 1"));
            b.ToTable(t => t.HasCheckConstraint("ck_user_onboarding_completed_at",
                "(status = 'completed') = (completed_at IS NOT NULL)"));

            b.HasOne<ApplicationUser>()
                .WithOne()
                .HasForeignKey<UserOnboarding>(t => t.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess) =>
        throw new NotSupportedException("Use SaveChangesAsync - synchronous SaveChanges skips domain event dispatch.");

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var domainEvents = ModuleDbContextSupport.StageDomainEvents(this);
        var result = await base.SaveChangesAsync(cancellationToken);
        await ModuleDbContextSupport.DispatchAsync(dispatcher, domainEvents, cancellationToken);
        return result;
    }
}

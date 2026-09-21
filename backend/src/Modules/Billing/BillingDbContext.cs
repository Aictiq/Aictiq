using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Aictiq.Modules.Billing.Domain;
using Aictiq.SharedKernel.Events;
using Aictiq.SharedKernel.Persistence;
using Aictiq.SharedKernel.Tenancy;

namespace Aictiq.Modules.Billing;

/// <summary>
/// A <see cref="ModuleDbContext"/>: subscriptions are tenant rows and must get
/// the organization filter by type like every other, and a webhook's effect has to reach the
/// outbox (<c>OrganizationBillingChanged</c>) in the same transaction as the row it changed.
/// </summary>
public sealed class BillingDbContext(DbContextOptions<BillingDbContext> options,
    IDomainEventDispatcher? dispatcher = null, ICurrentTenant? currentTenant = null)
    : ModuleDbContext(options, dispatcher, currentTenant)
{
    /// <summary>Stripe statuses that mean "Stripe is charging this subscription" (see <see cref="SubscriptionStatuses.IsBilling"/>).</summary>
    private const string BillingStatuses = "3, 4, 5, 7";

    protected override string Schema => "billing";

    public DbSet<BillingPlan> Plans => Set<BillingPlan>();
    public DbSet<UsageSnapshot> UsageSnapshots => Set<UsageSnapshot>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<StripeEvent> StripeEvents => Set<StripeEvent>();
    public DbSet<Evaluation> Evaluations => Set<Evaluation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<BillingPlan>(b =>
        {
            b.ToTable("plans"); b.HasKey(x => x.Code); b.Property(x => x.Code).HasMaxLength(50);
            b.Property(x => x.HumanSeatPrice).HasPrecision(10, 2);
            b.Property(x => x.OrganizationPrice).HasPrecision(10, 2);
            b.Property(x => x.Limits).HasColumnType("jsonb").HasConversion(
                value => JsonSerializer.Serialize(value, (JsonSerializerOptions?)null),
                value => JsonSerializer.Deserialize<PlanLimits>(value, (JsonSerializerOptions?)null)!);
            b.ToTable(t => t.HasCheckConstraint("ck_plans_included_agents", "included_agents_per_human IS NULL OR included_agents_per_human >= 0"));
            // The current offer, seeded beside the legacy rows rather than over them: an
            // existing subscription must keep resolving the plan it was sold, and hosted
            // is the only code checkout sells. Hosted carries the shared allowances —
            // 10 GiB of committed attachments, 90 days of finished-run raw logs, a
            // 365-day analytics window — and no seat, project or item cap at all.
            b.HasData(
                new BillingPlan { Code = "self_hosted", HumanSeatPrice = 0, OrganizationPrice = 0, Limits = new PlanLimits(null, null, null, null, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "webhooks", "page_permissions", "audit_export" }) },
                new BillingPlan { Code = "free", HumanSeatPrice = 0, OrganizationPrice = 0, Limits = new PlanLimits(5, 1, 3, 1_073_741_824) },
                new BillingPlan { Code = "starter", HumanSeatPrice = 9, OrganizationPrice = 0, IncludedAgentsPerHuman = 3, Limits = new PlanLimits(20, 5, 20, 10_737_418_240) },
                new BillingPlan { Code = "team", HumanSeatPrice = 15, OrganizationPrice = 0, Limits = new PlanLimits(null, 25, null, 107_374_182_400, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "webhooks", "page_permissions", "audit_export" }) },
                new BillingPlan { Code = "enterprise", HumanSeatPrice = 19, OrganizationPrice = 0, Limits = new PlanLimits(null, null, null, null, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "webhooks", "page_permissions", "audit_export", "sso", "scim" }) },
                new BillingPlan { Code = "hosted", HumanSeatPrice = 0, OrganizationPrice = 49m, Limits = new PlanLimits(null, null, null, 10_737_418_240, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "webhooks", "page_permissions", "audit_export" }, RunLogDays: 90, AnalyticsDays: 365) });
        });
        modelBuilder.Entity<UsageSnapshot>(b =>
        {
            b.ToTable("usage_snapshots"); b.HasKey(x => new { x.OrganizationId, x.Day });
            b.HasIndex(x => new { x.OrganizationId, x.Day });
        });
        modelBuilder.Entity<Subscription>(b =>
        {
            b.ToTable("subscriptions");
            b.Property(x => x.Version).IsRowVersion();
            b.Property(x => x.Plan).HasMaxLength(50);
            b.Property(x => x.StripeCustomerId).HasMaxLength(255);
            b.Property(x => x.StripeSubscriptionId).HasMaxLength(255);
            b.Property(x => x.Status).HasConversion<short>();
            b.Property(x => x.FoundingPrice).HasPrecision(10, 2);

            // A real foreign key: plans live in the same schema, so the database can refuse a
            // subscription to a plan that does not exist.
            b.HasOne<BillingPlan>().WithMany().HasForeignKey(x => x.Plan).OnDelete(DeleteBehavior.Restrict);

            // One account per organization. The Stripe ids are unique across the whole
            // table rather than per organization, unlike every other tenant uniqueness:
            // they are Stripe's global identifiers and they are how an anonymous webhook
            // finds its organization — two rows claiming one would make that a guess.
            b.HasIndex(x => x.OrganizationId).IsUnique().HasDatabaseName("ux_subscriptions_organization_id");
            b.HasIndex(x => x.StripeCustomerId).IsUnique().HasFilter("stripe_customer_id IS NOT NULL")
                .HasDatabaseName("ux_subscriptions_stripe_customer_id");
            b.HasIndex(x => x.StripeSubscriptionId).IsUnique().HasFilter("stripe_subscription_id IS NOT NULL")
                .HasDatabaseName("ux_subscriptions_stripe_subscription_id");
            b.HasIndex(x => x.GraceEndsAt).HasFilter("grace_ends_at IS NOT NULL")
                .HasDatabaseName("ix_subscriptions_grace_ends_at");

            b.ToTable(t =>
            {
                t.HasCheckConstraint("ck_subscriptions_status", "status BETWEEN 0 AND 8");
                t.HasCheckConstraint("ck_subscriptions_seats", "seats_human >= 0 AND seats_agent >= 0");
                // A subscription belongs to a customer, and a status Stripe is charging for
                // needs a subscription to be charging.
                t.HasCheckConstraint("ck_subscriptions_subscription_has_customer",
                    "stripe_subscription_id IS NULL OR stripe_customer_id IS NOT NULL");
                t.HasCheckConstraint("ck_subscriptions_billing_has_subscription",
                    $"status NOT IN ({BillingStatuses}) OR stripe_subscription_id IS NOT NULL");
                // The grace period is one fact stored as two columns: both or neither, and it
                // ends after it starts.
                t.HasCheckConstraint("ck_subscriptions_grace",
                    "(payment_failed_at IS NULL) = (grace_ends_at IS NULL) AND (grace_ends_at IS NULL OR grace_ends_at > payment_failed_at)");
                // The founding offer is one fact stored as four: the price and its period
                // count exist together or not at all, the counted periods are never
                // negative, and a conversion time is only meaningful once an offer existed.
                t.HasCheckConstraint("ck_subscriptions_founding",
                    "(founding_price IS NULL) = (founding_periods IS NULL) AND founding_periods_billed >= 0 AND (founding_converted_at IS NULL OR founding_price IS NOT NULL)");
            });
        });
        modelBuilder.Entity<StripeEvent>(b =>
        {
            b.ToTable("stripe_events");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasMaxLength(255);
            b.Property(x => x.Type).HasMaxLength(100);
            b.HasIndex(x => x.ReceivedAt);
        });
        modelBuilder.Entity<Evaluation>(b =>
        {
            b.ToTable("evaluations");
            b.Property(x => x.Version).IsRowVersion();
            b.HasIndex(x => x.OrganizationId).IsUnique().HasDatabaseName("ux_evaluations_organization_id");
            // A window that ends before it starts is corrupt data, not a short trial.
            b.ToTable(t => t.HasCheckConstraint("ck_evaluations_window", "ends_at > started_at"));
        });
    }
}

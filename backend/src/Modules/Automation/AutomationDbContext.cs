using System.Text.Json;
using Aictiq.Modules.Automation.Domain;
using Aictiq.SharedKernel.Events;
using Aictiq.SharedKernel.Persistence;
using Aictiq.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Aictiq.Modules.Automation;

/// <summary>
/// The AI software factory (phase 10): runners now; playbooks, runs and rules as their
/// tickets land.
/// </summary>
public sealed class AutomationDbContext(DbContextOptions<AutomationDbContext> options,
    IDomainEventDispatcher? dispatcher = null, ICurrentTenant? currentTenant = null)
    : ModuleDbContext(options, dispatcher, currentTenant)
{
    public const string SchemaName = "automation";

    /// <summary>The capabilities document round-trips in the wire's own casing, so a raw read of the column matches the API.</summary>
    internal static readonly JsonSerializerOptions CapabilitiesJson = new(JsonSerializerDefaults.Web);

    protected override string Schema => SchemaName;

    public DbSet<Runner> Runners => Set<Runner>();
    public DbSet<Playbook> Playbooks => Set<Playbook>();
    public DbSet<ProjectFactorySettings> ProjectSettings => Set<ProjectFactorySettings>();
    public DbSet<Run> Runs => Set<Run>();
    public DbSet<RunLogChunk> RunLogChunks => Set<RunLogChunk>();
    public DbSet<Rule> Rules => Set<Rule>();
    public DbSet<RuleFiring> RuleFirings => Set<RuleFiring>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Runner>(b =>
        {
            b.ToTable("runners");
            b.Property(x => x.Version).IsRowVersion();
            b.Property(x => x.Name).HasMaxLength(Runner.MaxNameLength);
            b.Property(x => x.TokenHash).HasMaxLength(64);
            b.Property(x => x.TokenPrefix).HasMaxLength(RunnerCredential.PrefixLength);
            b.Property(x => x.RegisteredBy).HasMaxLength(450);
            b.Property(x => x.Capabilities)
                .HasColumnType("jsonb")
                .HasConversion(
                    value => value == null ? null : JsonSerializer.Serialize(value, CapabilitiesJson),
                    value => value == null ? null : JsonSerializer.Deserialize<RunnerCapabilities>(value, CapabilitiesJson),
                    new ValueComparer<RunnerCapabilities?>(
                        (left, right) => JsonSerializer.Serialize(left, CapabilitiesJson) == JsonSerializer.Serialize(right, CapabilitiesJson),
                        value => JsonSerializer.Serialize(value, CapabilitiesJson).GetHashCode(StringComparison.Ordinal),
                        value => value));

            // The secret is how an anonymous request finds its runner, so its hash is unique
            // across every organization — two rows claiming one would make that a guess.
            b.HasIndex(x => x.TokenHash).IsUnique().HasDatabaseName("ux_runners_token_hash");
            b.HasIndex(x => x.OrganizationId).HasDatabaseName("ix_runners_organization_id");
            // Unique (organization_id, lower(name)) among runners that still exist is raw SQL
            // in the migration: EF cannot index an expression.

            b.ToTable(t =>
            {
                t.HasCheckConstraint("ck_runners_name", "length(btrim(name)) BETWEEN 1 AND 100");
                t.HasCheckConstraint("ck_runners_token_prefix", "length(token_prefix) = 8");
                t.HasCheckConstraint("ck_runners_token_hash", "token_hash ~ '^[0-9A-F]{64}$'");
                // Deleting a runner is disabling it for good: a deleted runner that could
                // still authenticate would be the one thing the roster no longer shows.
                t.HasCheckConstraint("ck_runners_deleted_is_disabled", "deleted_at IS NULL OR disabled_at IS NOT NULL");
            });
        });

        modelBuilder.Entity<Playbook>(b =>
        {
            b.ToTable("playbooks");
            b.Property(x => x.Version).IsRowVersion();
            b.Property(x => x.Name).HasMaxLength(Playbook.MaxNameLength);
            b.Property(x => x.Harness).HasMaxLength(16);
            b.Property(x => x.CreatedBy).HasMaxLength(450);
            b.HasIndex(x => x.ProjectId).HasDatabaseName("ix_playbooks_project_id");
            b.HasIndex(x => new { x.ProjectId, x.IsDefault }).IsUnique().HasFilter("is_default")
                .HasDatabaseName("ux_playbooks_default_project");
            b.ToTable(table =>
            {
                table.HasCheckConstraint("ck_playbooks_name", "length(btrim(name)) BETWEEN 1 AND 100");
                table.HasCheckConstraint("ck_playbooks_harness", "harness IN ('claude', 'codex', 'opencode')");
                table.HasCheckConstraint("ck_playbooks_max_minutes", "max_minutes BETWEEN 5 AND 720");
            });
        });

        modelBuilder.Entity<ProjectFactorySettings>(b =>
        {
            b.ToTable("project_settings");
            b.Ignore(x => x.Id);
            b.HasKey(x => x.ProjectId);
            b.Property(x => x.Version).IsRowVersion();
            b.Property(x => x.RepoSource).HasConversion<short>();
            b.Property(x => x.RepoFullName).HasMaxLength(500);
            b.Property(x => x.DefaultBranch).HasMaxLength(255).HasDefaultValue("main");
            b.Property(x => x.LocalPathHint).HasMaxLength(2000);
            b.Property(x => x.DefaultAgentId).HasMaxLength(450);
            b.ToTable(table =>
            {
                table.HasCheckConstraint("ck_project_settings_repo_source", "repo_source IN (0, 1)");
                table.HasCheckConstraint("ck_project_settings_github_repo", "repo_source <> 0 OR repo_full_name IS NOT NULL");
                table.HasCheckConstraint("ck_project_settings_default_branch", "length(btrim(default_branch)) BETWEEN 1 AND 255");
            });
        });

        modelBuilder.Entity<Run>(b =>
        {
            b.ToTable("runs");
            b.Property(x => x.Version).IsRowVersion();
            b.Property(x => x.ItemKey).HasMaxLength(Run.MaxItemKeyLength);
            b.Property(x => x.AgentUserId).HasMaxLength(Run.MaxActorLength);
            b.Property(x => x.RequestedBy).HasMaxLength(Run.MaxActorLength);
            b.HasIndex(x => x.RuleId).HasDatabaseName("ix_runs_rule_id");
            b.Property(x => x.Harness).HasMaxLength(16);
            b.Property(x => x.BranchName).HasMaxLength(Run.MaxBranchNameLength);
            b.Property(x => x.OutcomeSummary).HasMaxLength(Run.MaxOutcomeSummaryLength);
            b.Property(x => x.PullRequestUrl).HasMaxLength(Run.MaxPullRequestUrlLength);
            b.Property(x => x.FailureReason).HasMaxLength(Run.MaxFailureReasonLength);
            b.Property(x => x.CostUsd).HasPrecision(12, 2);
            b.Property(x => x.Status).HasConversion<short>();

            b.HasIndex(x => new { x.ProjectId, x.QueuedAt }).HasDatabaseName("ix_runs_project_queued");
            b.HasIndex(x => new { x.OrganizationId, x.Status, x.QueuedAt }).HasDatabaseName("ix_runs_organization_status_queued");
            // Two distinct indexes on ItemId need distinct model names: a second HasIndex
            // keyed on properties alone would return the first index object and merge them.
            b.HasIndex(x => x.ItemId, "ix_runs_item").HasDatabaseName("ix_runs_item");
            // A work item carries at most one live run. The filter is what turns "no two
            // runs ever" into "no two live runs": a finished run frees the item for the next.
            b.HasIndex(x => x.ItemId, "ux_runs_item_live").IsUnique().HasFilter("status < 3")
                .HasDatabaseName("ux_runs_item_live");

            // finished_at is set exactly when the status says the run is over — the
            // sweeper's claim statement and the endpoints cannot disagree.
            b.ToTable(t =>
            {
                t.HasCheckConstraint("ck_runs_status", "status BETWEEN 0 AND 6");
                t.HasCheckConstraint("ck_runs_finished_status", "(finished_at IS NOT NULL) = (status >= 3)");
                t.HasCheckConstraint("ck_runs_max_minutes", "max_minutes BETWEEN 5 AND 720");
                // A run is dispatched by exactly one kind of actor. No FK from rule_id to
                // rules: a deleted rule must not rewrite or block the history of what it ran.
                t.HasCheckConstraint("ck_runs_requested_by_xor_rule",
                    "(requested_by IS NOT NULL) <> (rule_id IS NOT NULL)");
            });
        });

        modelBuilder.Entity<RunLogChunk>(b =>
        {
            b.ToTable("run_log_chunks");
            b.Ignore(x => x.Id);
            b.HasKey(x => new { x.RunId, x.Seq });
            b.Property(x => x.Stream).HasConversion<short>();
            b.Property(x => x.Text).HasMaxLength(RunLogChunk.MaxTextLength);
            b.HasOne<Run>().WithMany().HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Cascade);
            b.ToTable(t =>
            {
                t.HasCheckConstraint("ck_run_log_chunks_stream", "stream BETWEEN 0 AND 2");
                t.HasCheckConstraint("ck_run_log_chunks_seq", "seq >= 0");
            });
        });

        modelBuilder.Entity<Rule>(b =>
        {
            b.ToTable("rules");
            b.Property(x => x.Version).IsRowVersion();
            b.Property(x => x.Name).HasMaxLength(Rule.MaxNameLength);
            b.Property(x => x.AgentUserId).HasMaxLength(Run.MaxActorLength);
            b.Property(x => x.CreatedBy).HasMaxLength(450);
            b.HasIndex(x => x.ProjectId).HasDatabaseName("ix_rules_project_id");
            b.HasIndex(x => x.PlaybookId).HasDatabaseName("ix_rules_playbook_id");
            // RESTRICT: a playbook a rule uses cannot be deleted out from under it. The
            // endpoint turns the resulting FK violation into a friendly 409 up front, but
            // the constraint — not the check — is the guarantee (a race still 409s cleanly
            // through GlobalExceptionHandler).
            b.HasOne<Playbook>().WithMany().HasForeignKey(x => x.PlaybookId).OnDelete(DeleteBehavior.Restrict);
            b.ToTable(t =>
            {
                t.HasCheckConstraint("ck_rules_name", "length(btrim(name)) BETWEEN 1 AND 100");
                t.HasCheckConstraint("ck_rules_agent_user_id", "length(btrim(agent_user_id)) BETWEEN 1 AND 64");
            });
            // Unique (project_id, lower(name)) is raw SQL in the migration: EF cannot index lower().
        });

        modelBuilder.Entity<RuleFiring>(b =>
        {
            b.ToTable("rule_firings");
            b.Ignore(x => x.Id);
            b.HasKey(x => new { x.RuleId, x.ItemId, x.EventId });
            b.Property(x => x.ItemKey).HasMaxLength(Run.MaxItemKeyLength);
            b.Property(x => x.SkipReason).HasMaxLength(RuleSkipReasons.MaxLength);
            b.HasIndex(x => new { x.RuleId, x.At }).HasDatabaseName("ix_rule_firings_rule_at");
            b.HasOne<Rule>().WithMany().HasForeignKey(x => x.RuleId).OnDelete(DeleteBehavior.Cascade);
            b.ToTable(t =>
            {
                // A firing started a run or says why it did not — never both. Neither is
                // the attempt in flight: RuleFiringHandler claims the row first and fills
                // one in before its transaction commits.
                t.HasCheckConstraint("ck_rule_firings_run_or_skip", "run_id IS NULL OR skip_reason IS NULL");
            });
        });
    }
}

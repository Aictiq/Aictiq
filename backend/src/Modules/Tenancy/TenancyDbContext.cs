using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Aictiq.Modules.Tenancy.Domain;
using Aictiq.SharedKernel.Events;
using Aictiq.SharedKernel.Persistence;
using Aictiq.SharedKernel.Tenancy;

namespace Aictiq.Modules.Tenancy;

/// <summary>
/// Schema <c>tenancy</c>: organizations, who belongs to them, who has been asked to, the
/// projects inside them and the teams inside those.
/// </summary>
public sealed class TenancyDbContext(
    DbContextOptions<TenancyDbContext> options,
    IDomainEventDispatcher? dispatcher = null,
    ICurrentTenant? currentTenant = null)
    : ModuleDbContext(options, dispatcher, currentTenant)
{
    private static readonly JsonSerializerOptions SettingsJson = new(JsonSerializerDefaults.Web);

    protected override string Schema => "tenancy";

    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<RetiredSlug> RetiredSlugs => Set<RetiredSlug>();
    public DbSet<OrganizationMember> Members => Set<OrganizationMember>();
    public DbSet<Invitation> Invitations => Set<Invitation>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<ProjectMember> ProjectMembers => Set<ProjectMember>();
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<TeamMember> TeamMembers => Set<TeamMember>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Organization>(b =>
        {
            b.ToTable("organizations");
            b.Property(o => o.Version).IsRowVersion();

            b.Property(o => o.Slug).HasMaxLength(Slug.MaxLength);
            b.Property(o => o.Name).HasMaxLength(200);
            b.Property(o => o.Plan).HasMaxLength(50).HasDefaultValue(OrganizationPlans.SelfHosted);
            b.Property(o => o.CreatedBy).HasMaxLength(64);

            // One jsonb column rather than a scalar per preference: read as a unit, never
            // queried across, and a new preference is then a code change, not a migration.
            b.Property(o => o.Settings)
                .HasColumnType("jsonb")
                .HasConversion(
                    settings => JsonSerializer.Serialize(settings, SettingsJson),
                    json => JsonSerializer.Deserialize<OrganizationSettings>(json, SettingsJson)!,
                    // Records have value equality, but EF needs to be told: without a
                    // comparer it falls back to reference equality and reports every
                    // load-then-save as a settings change in the audit log.
                    new ValueComparer<OrganizationSettings>(
                        (left, right) => left == right,
                        settings => settings.GetHashCode(),
                        settings => settings));

            // A deleted organization's slug lives on in tenancy.retired_slugs, and a
            // trigger keeps new organizations off it: see RetiredSlug.
            b.HasIndex(o => o.Slug).IsUnique();

            b.ToTable(t => t.HasCheckConstraint("ck_organizations_slug_format",
                $"slug ~ '^[a-z0-9](?:[a-z0-9-]*[a-z0-9])?$' AND slug !~ '--' " +
                $"AND length(slug) BETWEEN {Slug.MinLength} AND {Slug.MaxLength}"));
            b.ToTable(t => t.HasCheckConstraint("ck_organizations_name_not_blank",
                "length(btrim(name)) > 0"));
        });

        modelBuilder.Entity<RetiredSlug>(b =>
        {
            b.ToTable("retired_slugs");
            b.HasKey(x => x.Slug);
            b.Property(x => x.Slug).HasMaxLength(Slug.MaxLength);
        });

        modelBuilder.Entity<OrganizationMember>(b =>
        {
            b.ToTable("organization_members");

            // The pair *is* the identity - a person is a member of an organization once
            // or not at all - so the surrogate key EntityBase supplies is dropped rather
            // than carried alongside a unique index that says the same thing.
            b.Ignore(m => m.Id);
            b.HasKey(m => new { m.OrganizationId, m.UserId });

            b.Property(m => m.UserId).HasMaxLength(64);
            b.Property(m => m.Role).HasConversion<short>();
            b.Property(m => m.CanOperateFactory).HasDefaultValue(true);
            b.Ignore(m => m.OperatesFactory);

            // A Guest never operates the factory. For Owners and Admins the rule is
            // MembershipRules' (they operate whatever the column says), so only this half is
            // the database's to guarantee. 3 is OrgRole.Guest as stored.
            b.ToTable(t => t.HasCheckConstraint("ck_organization_members_guest_not_operator",
                "role <> 3 OR NOT can_operate_factory"));

            // "Which organizations am I in" - the query behind every sign-in and the org
            // switcher - leads with the user, not the organization.
            b.HasIndex(m => m.UserId);

            b.HasOne<Organization>()
                .WithMany()
                .HasForeignKey(m => m.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Invitation>(b =>
        {
            b.ToTable("invitations");

            b.Property(i => i.Email).HasMaxLength(320);
            b.Property(i => i.Role).HasConversion<short>();
            b.Property(i => i.ProjectRole).HasConversion<short>();
            b.Property(i => i.InvitedBy).HasMaxLength(64);
            b.Property(i => i.AcceptedBy).HasMaxLength(64);

            // The token is the credential, so the hash is the lookup key and it is
            // globally unique: a presented token names its organization rather than
            // being searched for inside one, which is what lets the accept endpoint work
            // before any tenant exists.
            b.HasIndex(i => i.TokenHash).IsUnique();

            // One open invitation per address per organization. Partial, because the
            // closed ones are history: re-inviting someone who declined last quarter must
            // not be blocked by the row that records it.
            b.HasIndex(i => new { i.OrganizationId, i.Email })
                .IsUnique()
                .HasFilter("accepted_at IS NULL AND revoked_at IS NULL")
                .HasDatabaseName("ux_invitations_open_per_email");

            b.HasIndex(i => new { i.OrganizationId, i.CreatedAt });

            // Lower-cased at rest rather than citext: the unique index above has to be
            // case-insensitive (Ada@ and ada@ are one person), and a check constraint
            // buys that without asking every self-hosted deployment for an extension its
            // database user may not be allowed to create.
            b.ToTable(t => t.HasCheckConstraint("ck_invitations_email_normalized",
                "email = lower(email) AND length(btrim(email)) > 0"));

            // Owner is not an invitable role. Endpoint validation says so with a good
            // message; this is the guarantee, and it also covers the CLI and MCP.
            b.ToTable(t => t.HasCheckConstraint("ck_invitations_role_not_owner", "role > 0"));

            b.Property(i => i.CanOperateFactory).HasDefaultValue(true);
            b.ToTable(t => t.HasCheckConstraint("ck_invitations_guest_not_operator",
                "role <> 3 OR NOT can_operate_factory"));

            b.ToTable(t => t.HasCheckConstraint("ck_invitations_project_role_paired",
                "(project_id IS NULL) = (project_role IS NULL)"));

            // An accepted invitation always names who accepted it, and vice versa -
            // either half alone is a row nothing can explain.
            b.ToTable(t => t.HasCheckConstraint("ck_invitations_accepted_consistency",
                "(accepted_at IS NULL) = (accepted_by IS NULL)"));

            b.HasOne<Organization>()
                .WithMany()
                .HasForeignKey(i => i.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Project>(b =>
        {
            b.ToTable("projects");
            b.Property(p => p.Version).IsRowVersion();

            b.Property(p => p.Key).HasMaxLength(Project.MaxKeyLength);
            b.Property(p => p.Name).HasMaxLength(Project.MaxNameLength);
            b.Property(p => p.Description).HasMaxLength(Project.MaxDescriptionLength);
            b.Property(p => p.Visibility).HasConversion<short>();
            b.Property(p => p.Icon).HasMaxLength(16);
            b.Property(p => p.Color).HasMaxLength(9);
            b.Property(p => p.CreatedBy).HasMaxLength(64);

            // Tenant-scoped uniqueness, as every composite index on a TenantEntity must be:
            // two organizations may both call their project ACME.
            b.HasIndex(p => new { p.OrganizationId, p.Key }).IsUnique();
            // Case-insensitive uniqueness of the *name* is a raw index in the migration -
            // EF cannot express lower(name) - see ProjectNameUniqueness there.

            b.ToTable(t => t.HasCheckConstraint("ck_projects_key_format",
                $"key ~ '{KeyFormat.CheckConstraintPattern}'"));
            b.ToTable(t => t.HasCheckConstraint("ck_projects_name_not_blank",
                "length(btrim(name)) > 0"));
            b.ToTable(t => t.HasCheckConstraint("ck_projects_color_format",
                "color IS NULL OR color ~ '^#[0-9a-fA-F]{6}$'"));

            b.HasOne<Organization>()
                .WithMany()
                .HasForeignKey(p => p.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ProjectMember>(b =>
        {
            b.ToTable("project_members");

            // The pair is the identity, as for organization members.
            b.Ignore(m => m.Id);
            b.HasKey(m => new { m.ProjectId, m.UserId });

            b.Property(m => m.UserId).HasMaxLength(64);
            b.Property(m => m.Role).HasConversion<short>();

            // "Which projects am I on" - asked whenever the projects list is filtered for
            // someone who is not an organization admin.
            b.HasIndex(m => new { m.OrganizationId, m.UserId });

            b.HasOne<Project>()
                .WithMany()
                .HasForeignKey(m => m.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Team>(b =>
        {
            b.ToTable("teams");
            b.Property(t => t.Version).IsRowVersion();

            b.Property(t => t.Name).HasMaxLength(Team.MaxNameLength);
            b.Property(t => t.Key).HasMaxLength(KeyFormat.MaxLength);
            b.Property(t => t.EstimationUnit).HasConversion<short>();
            b.Property(t => t.TimeZone).HasMaxLength(64);
            b.Property(t => t.SprintLengthDays).HasDefaultValue(14);
            b.Property(t => t.WorkingDays).HasColumnType("integer[]");

            b.HasIndex(t => new { t.OrganizationId, t.ProjectId });
            b.HasIndex(t => new { t.ProjectId, t.Key }).IsUnique();

            // Exactly one default per project. A partial unique index rather than a rule
            // in the endpoint: two admins promoting different teams at the same instant
            // both pass any check the application can make on its own.
            b.HasIndex(t => t.ProjectId)
                .IsUnique()
                .HasFilter("is_default")
                .HasDatabaseName("ux_teams_default_per_project");

            // Case-insensitive uniqueness of the name is a raw index in the migration -
            // EF cannot express lower(name). See the Teams migration.

            b.ToTable(t => t.HasCheckConstraint("ck_teams_key_format",
                $"key ~ '{KeyFormat.CheckConstraintPattern}'"));
            b.ToTable(t => t.HasCheckConstraint("ck_teams_name_not_blank",
                "length(btrim(name)) > 0"));
            b.ToTable(t => t.HasCheckConstraint("ck_teams_sprint_length",
                $"sprint_length_days BETWEEN {Team.MinSprintLengthDays} AND {Team.MaxSprintLengthDays}"));
            // Days of the week, at least one of them: a team that works no days would
            // produce a sprint with no capacity and a burndown that divides by zero.
            b.ToTable(t => t.HasCheckConstraint("ck_teams_working_days",
                "array_length(working_days, 1) BETWEEN 1 AND 7 "
                + "AND working_days <@ ARRAY[0,1,2,3,4,5,6]"));

            b.HasOne<Project>()
                .WithMany()
                .HasForeignKey(t => t.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TeamMember>(b =>
        {
            b.ToTable("team_members");

            b.Ignore(m => m.Id);
            b.HasKey(m => new { m.TeamId, m.UserId });

            b.Property(m => m.UserId).HasMaxLength(64);
            b.Property(m => m.CapacityHoursPerDay).HasPrecision(4, 1);

            b.HasIndex(m => new { m.OrganizationId, m.UserId });

            b.ToTable(t => t.HasCheckConstraint("ck_team_members_capacity",
                "capacity_hours_per_day IS NULL OR capacity_hours_per_day BETWEEN 0 AND 24"));

            b.HasOne<Team>()
                .WithMany()
                .HasForeignKey(m => m.TeamId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Aictiq.SharedKernel;
using Aictiq.Modules.WorkItems.Domain;
using Aictiq.SharedKernel.Events;
using Aictiq.SharedKernel.Persistence;
using Aictiq.SharedKernel.Tenancy;

namespace Aictiq.Modules.WorkItems;

public sealed class WorkItemsDbContext(DbContextOptions<WorkItemsDbContext> options,
    IDomainEventDispatcher? dispatcher = null, ICurrentTenant? currentTenant = null, ICurrentUser? currentUser = null)
    : ModuleDbContext(options, dispatcher, currentTenant)
{
    private static readonly JsonSerializerOptions BoardJson = new(JsonSerializerDefaults.Web);
    protected override string Schema => "work";
    public DbSet<Workflow> Workflows => Set<Workflow>();
    public DbSet<WorkflowState> WorkflowStates => Set<WorkflowState>();
    public DbSet<WorkflowTransition> WorkflowTransitions => Set<WorkflowTransition>();
    public DbSet<WorkItem> Items => Set<WorkItem>();
    public DbSet<ProjectSequence> ProjectSequences => Set<ProjectSequence>();
    public DbSet<ItemTemplate> ItemTemplates => Set<ItemTemplate>();
    public DbSet<SavedView> SavedViews => Set<SavedView>();
    public DbSet<ItemHistory> ItemHistory => Set<ItemHistory>();
    public DbSet<Label> Labels => Set<Label>();
    public DbSet<ItemLabel> ItemLabels => Set<ItemLabel>();
    public DbSet<ItemRelation> ItemRelations => Set<ItemRelation>();
    public DbSet<ItemLink> ItemLinks => Set<ItemLink>();
    public DbSet<Comment> Comments => Set<Comment>();
    public DbSet<CommentRevision> CommentRevisions => Set<CommentRevision>();
    public DbSet<CommentReaction> CommentReactions => Set<CommentReaction>();
    public DbSet<ItemWatcher> ItemWatchers => Set<ItemWatcher>();
    public DbSet<Sprint> Sprints => Set<Sprint>();
    public DbSet<SprintScopeLog> SprintScopeLog => Set<SprintScopeLog>();
    public DbSet<SprintCapacity> SprintCapacities => Set<SprintCapacity>();
    public DbSet<Board> Boards => Set<Board>();
    public DbSet<Attachment> Attachments => Set<Attachment>();
    public DbSet<CsvImportJob> CsvImportJobs => Set<CsvImportJob>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<Workflow>(b =>
        {
            b.ToTable("workflows"); b.Property(x => x.Name).HasMaxLength(100); b.Property(x => x.Version).IsRowVersion();
            b.HasIndex(x => new { x.OrganizationId, x.ProjectId, x.Name }).IsUnique();
            b.HasIndex(x => new { x.ProjectId, x.IsDefault }).IsUnique().HasFilter("is_default").HasDatabaseName("ux_workflows_default_project");
            b.HasIndex(x => x.ProjectId);
        });
        modelBuilder.Entity<WorkflowState>(b =>
        {
            b.ToTable("workflow_states"); b.Property(x => x.Name).HasMaxLength(100); b.Property(x => x.Category).HasConversion<short>(); b.Property(x => x.Color).HasMaxLength(9);
            b.HasIndex(x => new { x.WorkflowId, x.Name }).IsUnique().HasDatabaseName("ux_workflow_states_name");
            b.HasIndex(x => new { x.WorkflowId, x.IsInitial }).IsUnique().HasFilter("is_initial").HasDatabaseName("ux_workflow_states_initial");
            b.HasOne<Workflow>().WithMany(x => x.States).HasForeignKey(x => x.WorkflowId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<WorkflowTransition>(b =>
        {
            b.ToTable("workflow_transitions");
            b.HasIndex(x => new { x.WorkflowId, x.FromStateId, x.ToStateId }).IsUnique();
            b.HasOne<Workflow>().WithMany(x => x.Transitions).HasForeignKey(x => x.WorkflowId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<WorkflowState>().WithMany().HasForeignKey(x => x.FromStateId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<WorkflowState>().WithMany().HasForeignKey(x => x.ToStateId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<WorkItem>(b =>
        {
            b.ToTable("items"); b.Property(x => x.Version).IsRowVersion(); b.Property(x => x.ProjectKey).HasMaxLength(12); b.Property(x => x.Title).HasMaxLength(500); b.Property(x => x.Resolution).HasMaxLength(64); b.Property(x => x.Rank).HasMaxLength(256); b.Property(x => x.ExternalRef).HasMaxLength(256); b.Property(x => x.AssigneeId).HasMaxLength(64); b.Property(x => x.CreatedBy).HasMaxLength(64); b.Property(x => x.ClaimedBy).HasMaxLength(64);
            b.Property(x => x.Search).HasColumnType("tsvector").HasComputedColumnSql("""
                setweight(to_tsvector('english', coalesce(title, '')), 'A') ||
                setweight(to_tsvector('simple', coalesce(title, '')), 'A') ||
                setweight(to_tsvector('english', coalesce(project_key || '-' || number::text, '')), 'A') ||
                setweight(to_tsvector('simple', coalesce(project_key || '-' || number::text, '')), 'A') ||
                setweight(to_tsvector('english', coalesce(label_search, '')), 'B') ||
                setweight(to_tsvector('simple', coalesce(label_search, '')), 'B') ||
                setweight(to_tsvector('english', coalesce(description_markdown, '')), 'C') ||
                setweight(to_tsvector('simple', coalesce(description_markdown, '')), 'C')
                """, stored: true);
            b.Property(x => x.Type).HasConversion<short>(); b.Property(x => x.Priority).HasConversion<short>();
            b.Property(x => x.EstimateHours).HasPrecision(6, 2); b.Property(x => x.RemainingHours).HasPrecision(6, 2); b.Property(x => x.CompletedHours).HasPrecision(6, 2);
            b.Ignore(x => x.LoggedHours);
            b.HasIndex(x => new { x.OrganizationId, x.ProjectId, x.Number }).IsUnique();
            // Items are addressed by their human key far more often than by project id — item
            // detail and every MCP tool taking a `project` argument carry project_key, which the
            // unique index above cannot serve (a seq scan of all 100k rows).
            b.HasIndex(x => new { x.OrganizationId, x.ProjectKey, x.Number }).HasDatabaseName("ix_items_organization_id_project_key_number");
            b.HasIndex(x => new { x.ProjectId, x.ExternalRef }).IsUnique().HasFilter("external_ref IS NOT NULL").HasDatabaseName("ux_items_project_external_ref"); b.HasIndex(x => new { x.ProjectId, x.Rank }).HasDatabaseName("ix_items_project_rank"); b.HasIndex(x => new { x.ProjectId, x.StateId }); b.HasIndex(x => new { x.TeamId, x.BoardColumnId, x.Rank }).HasDatabaseName("ix_items_team_board_column_rank"); b.HasIndex(x => new { x.TeamId, x.SprintId }); b.HasIndex(x => x.AssigneeId); b.HasIndex(x => x.ParentId);
            b.HasOne<WorkflowState>().WithMany().HasForeignKey(x => x.StateId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<WorkItem>().WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Restrict);
            b.ToTable(t => { t.HasCheckConstraint("ck_items_title_not_blank", "length(btrim(title)) > 0"); t.HasCheckConstraint("ck_items_parent_not_self", "parent_id IS NULL OR parent_id <> id"); t.HasCheckConstraint("ck_items_hours_nonnegative", "(estimate_hours IS NULL OR estimate_hours >= 0) AND (remaining_hours IS NULL OR remaining_hours >= 0) AND (completed_hours IS NULL OR completed_hours >= 0)"); t.HasCheckConstraint("ck_items_hours_task_or_bug", "type IN (3, 4) OR (estimate_hours IS NULL AND remaining_hours IS NULL AND completed_hours IS NULL)"); });
        });
        modelBuilder.Entity<CsvImportJob>(b =>
        {
            b.ToTable("csv_import_jobs");
            b.Property(x => x.ProjectKey).HasMaxLength(12); b.Property(x => x.RequestedBy).HasMaxLength(64);
            b.Property(x => x.Status).HasConversion<short>(); b.Property(x => x.Errors).HasColumnType("text[]");
            b.HasIndex(x => new { x.Status, x.CreatedAt }).HasDatabaseName("ix_csv_import_jobs_status_created_at");
            b.HasIndex(x => x.ProjectId);
        });
        modelBuilder.Entity<ProjectSequence>(b => { b.ToTable("project_sequences"); b.Ignore(x => x.Id); b.HasKey(x => x.ProjectId); });
        modelBuilder.Entity<ItemTemplate>(b =>
        {
            b.ToTable("item_templates");
            b.Property(x => x.Type).HasConversion<short>(); b.Property(x => x.Name).HasMaxLength(100);
            b.Property(x => x.DefaultLabelIds).HasColumnType("uuid[]"); b.Property(x => x.DefaultPriority).HasConversion<short>();
            b.Property(x => x.Version).IsRowVersion();
            b.HasIndex(x => new { x.OrganizationId, x.ProjectId, x.Name }).IsUnique().HasDatabaseName("ux_item_templates_name");
            b.HasIndex(x => new { x.ProjectId, x.Type, x.IsDefault }).IsUnique().HasFilter("is_default").HasDatabaseName("ux_item_templates_default_type");
            b.ToTable(t => t.HasCheckConstraint("ck_item_templates_name_not_blank", "length(btrim(name)) > 0"));
        });
        modelBuilder.Entity<SavedView>(b =>
        {
            b.ToTable("saved_views");
            b.Property(x => x.OwnerId).HasMaxLength(64);
            b.Property(x => x.Name).HasMaxLength(100);
            b.Property(x => x.Filter).HasMaxLength(2048);
            b.Property(x => x.Sort).HasMaxLength(64);
            b.Property(x => x.Columns).HasColumnType("text[]");
            b.Property(x => x.Version).IsRowVersion();
            b.HasIndex(x => new { x.ProjectId, x.IsShared, x.Name });
            b.HasIndex(x => new { x.ProjectId, x.OwnerId, x.Name }).IsUnique().HasDatabaseName("ux_saved_views_owner_name");
            b.ToTable(t => t.HasCheckConstraint("ck_saved_views_name_not_blank", "length(btrim(name)) > 0"));
        });
        modelBuilder.Entity<ItemHistory>(b => { b.ToTable("item_history"); b.Property(x => x.ActorId).HasMaxLength(64); b.Property(x => x.Field).HasMaxLength(64); b.HasIndex(x => new { x.ItemId, x.At }); b.HasIndex(x => x.EventId); b.HasOne<WorkItem>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Cascade); });
        modelBuilder.Entity<Label>(b =>
        {
            b.ToTable("labels"); b.Property(x => x.Name).HasMaxLength(50); b.Property(x => x.Color).HasMaxLength(7); b.Property(x => x.Description).HasMaxLength(280); b.Property(x => x.Group).HasColumnName("group_name").HasMaxLength(50); b.Property(x => x.Version).IsRowVersion();
            b.HasIndex(x => x.ProjectId);
            // (organization_id, project_id, lower(name)) is unique but EF's HasIndex only
            // takes columns, so the real guarantee is the raw-SQL index in the migration —
            // this fluent config exists only to declare the CLR shape.
            b.ToTable(t =>
            {
                t.HasCheckConstraint("ck_labels_name_not_blank", "length(btrim(name)) > 0 AND length(name) <= 50");
                t.HasCheckConstraint("ck_labels_color_format", "color IS NULL OR color ~ '^#[0-9A-Fa-f]{6}$'");
                t.HasCheckConstraint("ck_labels_group_not_blank", "group_name IS NULL OR length(btrim(group_name)) > 0");
            });
        });
        modelBuilder.Entity<ItemLabel>(b =>
        {
            b.ToTable("item_labels"); b.Ignore(x => x.Id); b.HasKey(x => new { x.ItemId, x.LabelId });
            b.Property(x => x.AddedBy).HasMaxLength(64);
            b.HasOne<WorkItem>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Label>().WithMany().HasForeignKey(x => x.LabelId).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(x => x.LabelId);
        });
        modelBuilder.Entity<ItemRelation>(b =>
        {
            b.ToTable("item_relations"); b.Ignore(x => x.Id); b.HasKey(x => new { x.SourceId, x.TargetId, x.Kind }); b.Property(x => x.Kind).HasConversion<short>();
            b.HasOne<WorkItem>().WithMany().HasForeignKey(x => x.SourceId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<WorkItem>().WithMany().HasForeignKey(x => x.TargetId).OnDelete(DeleteBehavior.Cascade); b.HasIndex(x => x.TargetId);
            b.ToTable(t => { t.HasCheckConstraint("ck_item_relations_not_self", "source_id <> target_id"); t.HasCheckConstraint("ck_item_relations_related_order", "kind <> 0 OR source_id < target_id"); });
        });
        modelBuilder.Entity<ItemLink>(b =>
        {
            b.ToTable("item_links"); b.Property(x => x.Kind).HasConversion<short>(); b.Property(x => x.Provider).HasMaxLength(32); b.Property(x => x.ExternalId).HasMaxLength(2048); b.Property(x => x.Url).HasMaxLength(2048); b.Property(x => x.Title).HasMaxLength(500); b.Property(x => x.State).HasMaxLength(64); b.Property(x => x.Meta).HasColumnType("jsonb");
            b.HasOne<WorkItem>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Cascade); b.HasIndex(x => new { x.ItemId, x.Provider, x.Kind, x.ExternalId }).IsUnique().HasDatabaseName("ux_item_links_idempotency");
        });
        modelBuilder.Entity<Comment>(b =>
        {
            b.ToTable("comments"); b.Property(x => x.AuthorId).HasMaxLength(64); b.Property(x => x.MentionedUserIds).HasColumnName("mentioned_user_ids");
            b.Property(x => x.Search).HasColumnType("tsvector").HasComputedColumnSql("""
                to_tsvector('english', coalesce(body_markdown, '')) ||
                to_tsvector('simple', coalesce(body_markdown, ''))
                """, stored: true);
            b.HasIndex(x => new { x.ItemId, x.CreatedAt });
            // A run-outcome comment carries the id of the event that produced it, and the
            // unique index on the pair is the database's idempotency guarantee for the
            // handler that writes it — a replayed RunFinished collides here and is
            // treated as the success it already was.
            b.HasIndex(x => new { x.ItemId, x.EventId }).IsUnique().HasFilter("event_id IS NOT NULL")
                .HasDatabaseName("ux_comments_item_id_event_id");
            b.HasOne<WorkItem>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<ItemWatcher>(b =>
        {
            b.ToTable("watchers"); b.Ignore(x => x.Id); b.HasKey(x => new { x.ItemId, x.UserId });
            b.Property(x => x.UserId).HasMaxLength(64); b.Property(x => x.Reason).HasConversion<short>();
            b.HasIndex(x => new { x.ItemId, x.MutedAt });
            b.HasOne<WorkItem>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<Sprint>(b =>
        {
            b.ToTable("sprints"); b.Property(x => x.Name).HasMaxLength(100); b.Property(x => x.Goal).HasMaxLength(2000); b.Property(x => x.State).HasConversion<short>(); b.Property(x => x.Version).IsRowVersion();
            b.HasIndex(x => new { x.TeamId, x.State }).HasDatabaseName("ix_sprints_team_state");
            b.HasIndex(x => new { x.TeamId, x.StartsOn }).HasDatabaseName("ix_sprints_team_starts_on");
            b.ToTable(t => t.HasCheckConstraint("ck_sprints_dates", "ends_on > starts_on"));
        });
        modelBuilder.Entity<SprintScopeLog>(b =>
        {
            b.ToTable("sprint_scope_log"); b.Property(x => x.Change).HasConversion<short>(); b.Property(x => x.Points).HasPrecision(10, 2); b.Property(x => x.RemainingHours).HasPrecision(10, 2);
            b.HasIndex(x => new { x.SprintId, x.At }); b.HasIndex(x => new { x.ItemId, x.At });
            b.HasOne<Sprint>().WithMany().HasForeignKey(x => x.SprintId).OnDelete(DeleteBehavior.Cascade); b.HasOne<WorkItem>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<SprintCapacity>(b =>
        {
            b.ToTable("sprint_capacity"); b.Ignore(x => x.Id); b.HasKey(x => new { x.SprintId, x.UserId });
            b.Property(x => x.UserId).HasMaxLength(64); b.Property(x => x.HoursPerDay).HasPrecision(4, 1);
            b.HasIndex(x => new { x.OrganizationId, x.UserId });
            b.ToTable(t => t.HasCheckConstraint("ck_sprint_capacity_hours_per_day", "hours_per_day BETWEEN 0 AND 24"));
            b.ToTable(t => t.HasCheckConstraint("ck_sprint_capacity_days_off", "days_off >= 0"));
            b.HasOne<Sprint>().WithMany().HasForeignKey(x => x.SprintId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<Board>(b =>
        {
            b.ToTable("boards"); b.Property(x => x.Kind).HasConversion<short>(); b.Property(x => x.Version).IsRowVersion();
            b.Property(x => x.Config).HasColumnType("jsonb").HasConversion(
                config => JsonSerializer.Serialize(config, BoardJson),
                json => JsonSerializer.Deserialize<BoardConfig>(json, BoardJson)!);
            b.HasIndex(x => new { x.OrganizationId, x.TeamId, x.Kind }).IsUnique().HasDatabaseName("ux_boards_team_kind");
            b.HasIndex(x => x.TeamId);
        });
        modelBuilder.Entity<CommentRevision>(b =>
        {
            b.ToTable("comment_revisions"); b.Property(x => x.EditedBy).HasMaxLength(64);
            b.HasIndex(x => new { x.CommentId, x.EditedAt });
            b.HasOne<Comment>().WithMany().HasForeignKey(x => x.CommentId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<CommentReaction>(b =>
        {
            b.ToTable("comment_reactions"); b.Ignore(x => x.Id); b.HasKey(x => new { x.CommentId, x.UserId, x.Emoji });
            b.Property(x => x.UserId).HasMaxLength(64); b.Property(x => x.Emoji).HasMaxLength(32);
            b.HasOne<Comment>().WithMany().HasForeignKey(x => x.CommentId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<Attachment>(b =>
        {
            b.ToTable("attachments");
            b.Property(x => x.ObjectKey).HasMaxLength(1024);
            b.Property(x => x.FileName).HasMaxLength(255);
            b.Property(x => x.ContentType).HasMaxLength(255);
            b.Property(x => x.Sha256).HasMaxLength(64);
            b.Property(x => x.UploadedBy).HasMaxLength(64);
            b.Property(x => x.Status).HasConversion<short>();
            b.HasIndex(x => x.ObjectKey).IsUnique();
            b.HasIndex(x => new { x.ProjectId, x.ItemId });
            b.HasIndex(x => new { x.ProjectId, x.CommentId });
            b.HasIndex(x => new { x.ProjectId, x.WikiPageId });
            b.HasIndex(x => new { x.Status, x.CreatedAt });
            b.HasOne<WorkItem>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Comment>().WithMany().HasForeignKey(x => x.CommentId).OnDelete(DeleteBehavior.Cascade);
            b.ToTable(t => t.HasCheckConstraint("ck_attachments_owner_and_status",
                "(status = 0 AND item_id IS NULL AND comment_id IS NULL AND wiki_page_id IS NULL) OR (status = 1 AND ((item_id IS NOT NULL)::integer + (comment_id IS NOT NULL)::integer + (wiki_page_id IS NOT NULL)::integer = 1))"));
        });
    }

    /// <summary>
    /// History is staged centrally so every WorkItem mutation path is audited. The
    /// history entries themselves are deliberately excluded to avoid recursion.
    /// </summary>
    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        ChangeTracker.DetectChanges();
        var actorId = currentUser?.UserId;
        if (!string.IsNullOrWhiteSpace(actorId)) await StageHistoryAsync(actorId, cancellationToken);
        return await base.SaveChangesAsync(cancellationToken);
    }

    private async Task StageHistoryAsync(string actorId, CancellationToken cancellationToken)
    {
        var itemEntries = ChangeTracker.Entries<WorkItem>().Where(x => x.State == EntityState.Modified).ToList();
        var labelEntries = ChangeTracker.Entries<ItemLabel>().Where(x => x.State is EntityState.Added or EntityState.Deleted).ToList();
        if (itemEntries.Count == 0 && labelEntries.Count == 0) return;

        var eventId = Guid.CreateVersion7();
        var at = DateTimeOffset.UtcNow;
        foreach (var entry in itemEntries)
        {
            var loggedHours = entry.Entity.LoggedHours;
            if (loggedHours is { } hours)
            {
                AddHistory(entry.Entity, actorId, at, "log-time", null, new { hours }, eventId);
                entry.Entity.ClearLoggedHours();
            }
            foreach (var field in TrackedFields)
            {
                var property = entry.Property(field.Property);
                if (!property.IsModified) continue;
                if (loggedHours is not null && field.Property is nameof(WorkItem.RemainingHours) or nameof(WorkItem.CompletedHours)) continue;
                var oldValue = field.Summarize is null ? property.OriginalValue : field.Summarize(property.OriginalValue);
                var newValue = field.Summarize is null ? property.CurrentValue : field.Summarize(property.CurrentValue);
                var historyField = field.Property == nameof(WorkItem.ClaimedBy)
                    ? property.OriginalValue is null ? "claim" : property.CurrentValue is null ? "release" : "claim"
                    : field.Name;
                AddHistory(entry.Entity, actorId, at, historyField, oldValue, newValue, eventId);
            }
        }

        if (labelEntries.Count == 0) return;
        var labelIds = labelEntries.Select(x => x.Entity.LabelId).Distinct().ToArray();
        var labels = await Labels.AsNoTracking().Where(x => labelIds.Contains(x.Id))
            .Select(x => new { x.Id, x.Name }).ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken);
        foreach (var entry in labelEntries)
        {
            var value = new { id = entry.Entity.LabelId, name = labels.GetValueOrDefault(entry.Entity.LabelId, entry.Entity.LabelId.ToString()) };
            var item = ChangeTracker.Entries<WorkItem>().FirstOrDefault(x => x.Entity.Id == entry.Entity.ItemId)?.Entity
                ?? await Items.AsNoTracking().SingleAsync(x => x.Id == entry.Entity.ItemId, cancellationToken);
            AddHistory(item, actorId, at, entry.State == EntityState.Added ? "label-added" : "label-removed",
                entry.State == EntityState.Added ? null : value, entry.State == EntityState.Added ? value : null, eventId);
        }
    }

    private void AddHistory(WorkItem item, string actorId, DateTimeOffset at, string field, object? oldValue, object? newValue, Guid eventId) =>
        ItemHistory.Add(new ItemHistory { OrganizationId = item.OrganizationId, ItemId = item.Id, ActorId = actorId, At = at, Field = field,
            OldValue = JsonSerializer.Serialize(oldValue), NewValue = JsonSerializer.Serialize(newValue), EventId = eventId });

    private sealed record HistoryField(string Property, string Name, Func<object?, object?>? Summarize = null);
    private static readonly HistoryField[] TrackedFields =
    [
        new(nameof(WorkItem.Title), "title"),
        new(nameof(WorkItem.DescriptionMarkdown), "description", value => new { summary = "updated", characters = ((string?)value)?.Length ?? 0 }),
        new(nameof(WorkItem.StateId), "state"), new(nameof(WorkItem.AssigneeId), "assignee"), new(nameof(WorkItem.Priority), "priority"),
        new(nameof(WorkItem.TeamId), "team"), new(nameof(WorkItem.SprintId), "sprint"), new(nameof(WorkItem.ParentId), "parent"),
        new(nameof(WorkItem.Points), "points"), new(nameof(WorkItem.EstimateHours), "estimate-hours"),
        new(nameof(WorkItem.RemainingHours), "remaining-hours"), new(nameof(WorkItem.CompletedHours), "completed-hours"),
        new(nameof(WorkItem.DueDate), "due-date"), new(nameof(WorkItem.ClaimedBy), "claim")
    ];
}

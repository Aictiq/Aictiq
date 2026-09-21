using Aictiq.Modules.WorkItems;
using Aictiq.SharedKernel.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aictiq.Modules.WorkItems.Migrations;

[DbContext(typeof(WorkItemsDbContext))]
[Migration("20260914100004_RlsTenantIsolation")]
public partial class RlsTenantIsolation : Migration
{
    private static readonly string[] Tables = ["workflows", "items", "project_sequences", "csv_import_jobs", "item_templates", "saved_views", "item_history", "item_relations", "item_links", "attachments", "labels", "item_labels", "watchers", "comments", "comment_reactions", "sprints", "sprint_scope_log", "sprint_capacity", "boards"];
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        TenantRls.Enable(migrationBuilder, "work", Tables);
        TenantRls.EnableViaParent(migrationBuilder, "work", "workflow_states", "workflow_id", "workflows");
        TenantRls.EnableViaParent(migrationBuilder, "work", "workflow_transitions", "workflow_id", "workflows");
        TenantRls.EnableViaParent(migrationBuilder, "work", "comment_revisions", "comment_id", "comments");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        TenantRls.Disable(migrationBuilder, "work", Tables);
        TenantRls.Disable(migrationBuilder, "work", "workflow_states", "workflow_transitions", "comment_revisions");
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aictiq.Modules.WorkItems.Migrations
{
    /// <inheritdoc />
    public partial class InitialWorkItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "work");

            migrationBuilder.CreateTable(
                name: "project_sequences",
                schema: "work",
                columns: table => new
                {
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    next_number = table.Column<int>(type: "integer", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_sequences", x => x.project_id);
                });

            migrationBuilder.CreateTable(
                name: "workflows",
                schema: "work",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    is_default = table.Column<bool>(type: "boolean", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workflows", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "workflow_states",
                schema: "work",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workflow_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    category = table.Column<short>(type: "smallint", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    color = table.Column<string>(type: "character varying(9)", maxLength: 9, nullable: true),
                    is_initial = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workflow_states", x => x.id);
                    table.ForeignKey(
                        name: "fk_workflow_states_workflows_workflow_id",
                        column: x => x.workflow_id,
                        principalSchema: "work",
                        principalTable: "workflows",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "items",
                schema: "work",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_key = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    type = table.Column<short>(type: "smallint", nullable: false),
                    title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    description_markdown = table.Column<string>(type: "text", nullable: false),
                    description_html = table.Column<string>(type: "text", nullable: false),
                    state_id = table.Column<Guid>(type: "uuid", nullable: false),
                    priority = table.Column<short>(type: "smallint", nullable: false),
                    assignee_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    team_id = table.Column<Guid>(type: "uuid", nullable: true),
                    sprint_id = table.Column<Guid>(type: "uuid", nullable: true),
                    parent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    rank = table.Column<string>(type: "text", nullable: true),
                    points = table.Column<decimal>(type: "numeric", nullable: true),
                    estimate_hours = table.Column<decimal>(type: "numeric", nullable: true),
                    remaining_hours = table.Column<decimal>(type: "numeric", nullable: true),
                    completed_hours = table.Column<decimal>(type: "numeric", nullable: true),
                    due_date = table.Column<DateOnly>(type: "date", nullable: true),
                    claimed_by = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    claimed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    claim_heartbeat_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    removed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_items", x => x.id);
                    table.CheckConstraint("ck_items_hours_nonnegative", "(estimate_hours IS NULL OR estimate_hours >= 0) AND (remaining_hours IS NULL OR remaining_hours >= 0) AND (completed_hours IS NULL OR completed_hours >= 0)");
                    table.CheckConstraint("ck_items_parent_not_self", "parent_id IS NULL OR parent_id <> id");
                    table.CheckConstraint("ck_items_title_not_blank", "length(btrim(title)) > 0");
                    table.ForeignKey(
                        name: "fk_items_items_parent_id",
                        column: x => x.parent_id,
                        principalSchema: "work",
                        principalTable: "items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_items_workflow_states_state_id",
                        column: x => x.state_id,
                        principalSchema: "work",
                        principalTable: "workflow_states",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "workflow_transitions",
                schema: "work",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workflow_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_state_id = table.Column<Guid>(type: "uuid", nullable: true),
                    to_state_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workflow_transitions", x => x.id);
                    table.ForeignKey(
                        name: "fk_workflow_transitions_workflow_states_from_state_id",
                        column: x => x.from_state_id,
                        principalSchema: "work",
                        principalTable: "workflow_states",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_workflow_transitions_workflow_states_to_state_id",
                        column: x => x.to_state_id,
                        principalSchema: "work",
                        principalTable: "workflow_states",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_workflow_transitions_workflows_workflow_id",
                        column: x => x.workflow_id,
                        principalSchema: "work",
                        principalTable: "workflows",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "item_history",
                schema: "work",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    field = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    old_value = table.Column<string>(type: "text", nullable: true),
                    new_value = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_item_history", x => x.id);
                    table.ForeignKey(
                        name: "fk_item_history_items_item_id",
                        column: x => x.item_id,
                        principalSchema: "work",
                        principalTable: "items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_item_history_item_id_at",
                schema: "work",
                table: "item_history",
                columns: new[] { "item_id", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_items_assignee_id",
                schema: "work",
                table: "items",
                column: "assignee_id");

            migrationBuilder.CreateIndex(
                name: "ix_items_organization_id_project_id_number",
                schema: "work",
                table: "items",
                columns: new[] { "organization_id", "project_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_items_parent_id",
                schema: "work",
                table: "items",
                column: "parent_id");

            migrationBuilder.CreateIndex(
                name: "ix_items_project_id_state_id",
                schema: "work",
                table: "items",
                columns: new[] { "project_id", "state_id" });

            migrationBuilder.CreateIndex(
                name: "ix_items_state_id",
                schema: "work",
                table: "items",
                column: "state_id");

            migrationBuilder.CreateIndex(
                name: "ix_items_team_id_sprint_id",
                schema: "work",
                table: "items",
                columns: new[] { "team_id", "sprint_id" });

            migrationBuilder.CreateIndex(
                name: "ux_workflow_states_initial",
                schema: "work",
                table: "workflow_states",
                columns: new[] { "workflow_id", "is_initial" },
                unique: true,
                filter: "is_initial");

            migrationBuilder.CreateIndex(
                name: "ux_workflow_states_name",
                schema: "work",
                table: "workflow_states",
                columns: new[] { "workflow_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_workflow_transitions_from_state_id",
                schema: "work",
                table: "workflow_transitions",
                column: "from_state_id");

            migrationBuilder.CreateIndex(
                name: "ix_workflow_transitions_to_state_id",
                schema: "work",
                table: "workflow_transitions",
                column: "to_state_id");

            migrationBuilder.CreateIndex(
                name: "ix_workflow_transitions_workflow_id_from_state_id_to_state_id",
                schema: "work",
                table: "workflow_transitions",
                columns: new[] { "workflow_id", "from_state_id", "to_state_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_workflows_organization_id_project_id_name",
                schema: "work",
                table: "workflows",
                columns: new[] { "organization_id", "project_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_workflows_project_id",
                schema: "work",
                table: "workflows",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ux_workflows_default_project",
                schema: "work",
                table: "workflows",
                columns: new[] { "project_id", "is_default" },
                unique: true,
                filter: "is_default");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "item_history",
                schema: "work");

            migrationBuilder.DropTable(
                name: "project_sequences",
                schema: "work");

            migrationBuilder.DropTable(
                name: "workflow_transitions",
                schema: "work");

            migrationBuilder.DropTable(
                name: "items",
                schema: "work");

            migrationBuilder.DropTable(
                name: "workflow_states",
                schema: "work");

            migrationBuilder.DropTable(
                name: "workflows",
                schema: "work");
        }
    }
}

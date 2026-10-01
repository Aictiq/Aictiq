using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Aictiq.SharedKernel.Persistence;

#nullable disable

namespace Aictiq.Modules.Automation.Migrations
{
    /// <inheritdoc />
    public partial class TicketRefinement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<short>(
                name: "kind",
                schema: "automation",
                table: "runs",
                type: "smallint",
                nullable: false,
                defaultValue: (short)0);

            migrationBuilder.CreateTable(
                name: "item_refinements",
                schema: "automation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_key = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    questions = table.Column<string[]>(type: "text[]", nullable: false),
                    answered_questions = table.Column<string[]>(type: "text[]", nullable: false),
                    answers = table.Column<string[]>(type: "text[]", nullable: false),
                    summary = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    last_run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    requested_by = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    confirmed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    confirmed_by = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_item_refinements", x => x.id);
                    table.CheckConstraint("ck_item_refinements_answers", "cardinality(answered_questions) = cardinality(answers)");
                    table.CheckConstraint("ck_item_refinements_confirmed", "(status = 4) = (confirmed_at IS NOT NULL)");
                    table.CheckConstraint("ck_item_refinements_status", "status BETWEEN 0 AND 4");
                });

            migrationBuilder.CreateTable(
                name: "refinement_settings",
                schema: "automation",
                columns: table => new
                {
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    playbook_id = table.Column<Guid>(type: "uuid", nullable: true),
                    agent_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    product_description = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    writing_instructions = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    naming_conventions = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    platforms = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    refined_state_id = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_refinement_settings", x => x.project_id);
                    table.ForeignKey(
                        name: "fk_refinement_settings_playbooks_playbook_id",
                        column: x => x.playbook_id,
                        principalSchema: "automation",
                        principalTable: "playbooks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_runs_kind",
                schema: "automation",
                table: "runs",
                sql: "kind IN (0, 1)");

            migrationBuilder.CreateIndex(
                name: "ix_item_refinements_project_id",
                schema: "automation",
                table: "item_refinements",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ux_item_refinements_item",
                schema: "automation",
                table: "item_refinements",
                column: "item_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_refinement_settings_playbook_id",
                schema: "automation",
                table: "refinement_settings",
                column: "playbook_id");

            TenantRls.Enable(migrationBuilder, "automation", "item_refinements", "refinement_settings");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            TenantRls.Disable(migrationBuilder, "automation", "refinement_settings", "item_refinements");

            migrationBuilder.DropTable(
                name: "item_refinements",
                schema: "automation");

            migrationBuilder.DropTable(
                name: "refinement_settings",
                schema: "automation");

            migrationBuilder.DropCheckConstraint(
                name: "ck_runs_kind",
                schema: "automation",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "kind",
                schema: "automation",
                table: "runs");
        }
    }
}

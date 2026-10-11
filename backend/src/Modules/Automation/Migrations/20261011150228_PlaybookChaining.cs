using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Aictiq.SharedKernel.Persistence;

#nullable disable

namespace Aictiq.Modules.Automation.Migrations
{
    /// <inheritdoc />
    public partial class PlaybookChaining : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_runs_follow_up_trigger",
                schema: "automation",
                table: "runs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_runs_kind",
                schema: "automation",
                table: "runs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_runs_trigger_instruction",
                schema: "automation",
                table: "runs");

            migrationBuilder.AddColumn<Guid>(
                name: "chain_playbook_id",
                schema: "automation",
                table: "runs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "rework_of_run_id",
                schema: "automation",
                table: "runs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "stage_findings",
                schema: "automation",
                table: "runs",
                type: "character varying(20000)",
                maxLength: 20000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "stage_index",
                schema: "automation",
                table: "runs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<short>(
                name: "stage_verdict",
                schema: "automation",
                table: "runs",
                type: "smallint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "chain_steps",
                schema: "automation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_key = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    after_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    chain_playbook_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<short>(type: "smallint", nullable: false),
                    stage_index = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    drop_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_chain_steps", x => x.id);
                    table.CheckConstraint("ck_chain_steps_decided", "(status = 0) = (decided_at IS NULL)");
                    table.CheckConstraint("ck_chain_steps_kind", "kind IN (0, 1)");
                    table.CheckConstraint("ck_chain_steps_started", "(status = 1) = (run_id IS NOT NULL)");
                    table.CheckConstraint("ck_chain_steps_status", "status BETWEEN 0 AND 2");
                });

            migrationBuilder.CreateTable(
                name: "playbook_stages",
                schema: "automation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    playbook_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stage_playbook_id = table.Column<Guid>(type: "uuid", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_playbook_stages", x => x.id);
                    table.CheckConstraint("ck_playbook_stages_not_self", "playbook_id <> stage_playbook_id");
                    table.CheckConstraint("ck_playbook_stages_position", "position BETWEEN 0 AND 9");
                    table.ForeignKey(
                        name: "fk_playbook_stages_playbooks_playbook_id",
                        column: x => x.playbook_id,
                        principalSchema: "automation",
                        principalTable: "playbooks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_playbook_stages_playbooks_stage_playbook_id",
                        column: x => x.stage_playbook_id,
                        principalSchema: "automation",
                        principalTable: "playbooks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_runs_follow_up_trigger",
                schema: "automation",
                table: "runs",
                sql: "follows_up_run_id IS NULL OR trigger_comment_id IS NOT NULL OR kind = 3 OR rework_of_run_id IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_runs_kind",
                schema: "automation",
                table: "runs",
                sql: "kind IN (0, 1, 2, 3)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_runs_stage",
                schema: "automation",
                table: "runs",
                sql: "(kind = 3) = (chain_playbook_id IS NOT NULL AND stage_index IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_runs_stage_verdict",
                schema: "automation",
                table: "runs",
                sql: "stage_verdict IS NULL OR (kind = 3 AND stage_verdict IN (0, 1))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_runs_trigger_instruction",
                schema: "automation",
                table: "runs",
                sql: "(trigger_comment_id IS NULL) = (instruction IS NULL) OR rework_of_run_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_chain_steps_pending",
                schema: "automation",
                table: "chain_steps",
                columns: new[] { "item_id", "created_at" },
                filter: "status = 0");

            migrationBuilder.CreateIndex(
                name: "ix_chain_steps_project_id",
                schema: "automation",
                table: "chain_steps",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ux_chain_steps_after_run",
                schema: "automation",
                table: "chain_steps",
                column: "after_run_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_playbook_stages_stage_playbook_id",
                schema: "automation",
                table: "playbook_stages",
                column: "stage_playbook_id");

            migrationBuilder.CreateIndex(
                name: "ux_playbook_stages_playbook_stage",
                schema: "automation",
                table: "playbook_stages",
                columns: new[] { "playbook_id", "stage_playbook_id" },
                unique: true);
            TenantRls.Enable(migrationBuilder, "automation", "playbook_stages");
            TenantRls.Enable(migrationBuilder, "automation", "chain_steps");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            TenantRls.Disable(migrationBuilder, "automation", "chain_steps");
            TenantRls.Disable(migrationBuilder, "automation", "playbook_stages");

            migrationBuilder.DropTable(
                name: "chain_steps",
                schema: "automation");

            migrationBuilder.DropTable(
                name: "playbook_stages",
                schema: "automation");

            migrationBuilder.DropCheckConstraint(
                name: "ck_runs_follow_up_trigger",
                schema: "automation",
                table: "runs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_runs_kind",
                schema: "automation",
                table: "runs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_runs_stage",
                schema: "automation",
                table: "runs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_runs_stage_verdict",
                schema: "automation",
                table: "runs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_runs_trigger_instruction",
                schema: "automation",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "chain_playbook_id",
                schema: "automation",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "rework_of_run_id",
                schema: "automation",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "stage_findings",
                schema: "automation",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "stage_index",
                schema: "automation",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "stage_verdict",
                schema: "automation",
                table: "runs");

            migrationBuilder.AddCheckConstraint(
                name: "ck_runs_follow_up_trigger",
                schema: "automation",
                table: "runs",
                sql: "follows_up_run_id IS NULL OR trigger_comment_id IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_runs_kind",
                schema: "automation",
                table: "runs",
                sql: "kind IN (0, 1, 2)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_runs_trigger_instruction",
                schema: "automation",
                table: "runs",
                sql: "(trigger_comment_id IS NULL) = (instruction IS NULL)");
        }
    }
}

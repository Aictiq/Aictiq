using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Aictiq.SharedKernel.Persistence;

#nullable disable

namespace Aictiq.Modules.Automation.Migrations
{
    /// <inheritdoc />
    public partial class AutomationRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "requested_by",
                schema: "automation",
                table: "runs",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64);

            migrationBuilder.AddColumn<Guid>(
                name: "rule_id",
                schema: "automation",
                table: "runs",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "rules",
                schema: "automation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    trigger_state_id = table.Column<Guid>(type: "uuid", nullable: false),
                    required_label_id = table.Column<Guid>(type: "uuid", nullable: true),
                    playbook_id = table.Column<Guid>(type: "uuid", nullable: false),
                    agent_user_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_by = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_rules", x => x.id);
                    table.CheckConstraint("ck_rules_agent_user_id", "length(btrim(agent_user_id)) BETWEEN 1 AND 64");
                    table.CheckConstraint("ck_rules_name", "length(btrim(name)) BETWEEN 1 AND 100");
                    table.ForeignKey(
                        name: "fk_rules_playbooks_playbook_id",
                        column: x => x.playbook_id,
                        principalSchema: "automation",
                        principalTable: "playbooks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "rule_firings",
                schema: "automation",
                columns: table => new
                {
                    rule_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_key = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    skip_reason = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_rule_firings", x => new { x.rule_id, x.item_id, x.event_id });
                    table.CheckConstraint("ck_rule_firings_run_or_skip", "run_id IS NULL OR skip_reason IS NULL");
                    table.ForeignKey(
                        name: "fk_rule_firings_rules_rule_id",
                        column: x => x.rule_id,
                        principalSchema: "automation",
                        principalTable: "rules",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_runs_rule_id",
                schema: "automation",
                table: "runs",
                column: "rule_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_runs_requested_by_xor_rule",
                schema: "automation",
                table: "runs",
                sql: "(requested_by IS NOT NULL) <> (rule_id IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "ix_rule_firings_rule_at",
                schema: "automation",
                table: "rule_firings",
                columns: new[] { "rule_id", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_rules_playbook_id",
                schema: "automation",
                table: "rules",
                column: "playbook_id");

            migrationBuilder.CreateIndex(
                name: "ix_rules_project_id",
                schema: "automation",
                table: "rules",
                column: "project_id");

            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX ux_rules_project_name
                    ON automation.rules (project_id, lower(name));
                """);

            TenantRls.Enable(migrationBuilder, "automation", "rules", "rule_firings");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            TenantRls.Disable(migrationBuilder, "automation", "rule_firings", "rules");

            migrationBuilder.DropTable(
                name: "rule_firings",
                schema: "automation");

            migrationBuilder.DropTable(
                name: "rules",
                schema: "automation");

            migrationBuilder.DropIndex(
                name: "ix_runs_rule_id",
                schema: "automation",
                table: "runs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_runs_requested_by_xor_rule",
                schema: "automation",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "rule_id",
                schema: "automation",
                table: "runs");

            migrationBuilder.AlterColumn<string>(
                name: "requested_by",
                schema: "automation",
                table: "runs",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64,
                oldNullable: true);
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Aictiq.SharedKernel.Persistence;

#nullable disable

namespace Aictiq.Modules.Automation.Migrations
{
    /// <inheritdoc />
    public partial class RunMentions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "follows_up_run_id",
                schema: "automation",
                table: "runs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "instruction",
                schema: "automation",
                table: "runs",
                type: "character varying(20000)",
                maxLength: 20000,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "trigger_comment_id",
                schema: "automation",
                table: "runs",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "run_mentions",
                schema: "automation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_key = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    comment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    agent_user_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    requested_by = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    refusal_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_run_mentions", x => x.id);
                    table.CheckConstraint("ck_run_mentions_decided", "(status = 0) = (decided_at IS NULL)");
                    table.CheckConstraint("ck_run_mentions_started", "(status = 1) = (run_id IS NOT NULL)");
                    table.CheckConstraint("ck_run_mentions_status", "status BETWEEN 0 AND 2");
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_runs_follow_up_trigger",
                schema: "automation",
                table: "runs",
                sql: "follows_up_run_id IS NULL OR trigger_comment_id IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_runs_trigger_instruction",
                schema: "automation",
                table: "runs",
                sql: "(trigger_comment_id IS NULL) = (instruction IS NULL)");

            migrationBuilder.CreateIndex(
                name: "ix_run_mentions_pending",
                schema: "automation",
                table: "run_mentions",
                columns: new[] { "item_id", "created_at" },
                filter: "status = 0");

            migrationBuilder.CreateIndex(
                name: "ix_run_mentions_project_id",
                schema: "automation",
                table: "run_mentions",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ux_run_mentions_comment_agent",
                schema: "automation",
                table: "run_mentions",
                columns: new[] { "comment_id", "agent_user_id" },
                unique: true);

            TenantRls.Enable(migrationBuilder, "automation", "run_mentions");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            TenantRls.Disable(migrationBuilder, "automation", "run_mentions");

            migrationBuilder.DropTable(
                name: "run_mentions",
                schema: "automation");

            migrationBuilder.DropCheckConstraint(
                name: "ck_runs_follow_up_trigger",
                schema: "automation",
                table: "runs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_runs_trigger_instruction",
                schema: "automation",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "follows_up_run_id",
                schema: "automation",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "instruction",
                schema: "automation",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "trigger_comment_id",
                schema: "automation",
                table: "runs");
        }
    }
}

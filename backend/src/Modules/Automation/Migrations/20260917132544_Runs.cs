using System;
using Aictiq.SharedKernel.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aictiq.Modules.Automation.Migrations
{
    /// <inheritdoc />
    public partial class Runs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "runs",
                schema: "automation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_key = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    playbook_id = table.Column<Guid>(type: "uuid", nullable: false),
                    agent_user_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    requested_by = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    runner_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    harness = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    prompt_snapshot = table.Column<string>(type: "text", nullable: false),
                    playbook_revision_id = table.Column<Guid>(type: "uuid", nullable: true),
                    agent_token_id = table.Column<Guid>(type: "uuid", nullable: true),
                    branch_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    max_minutes = table.Column<int>(type: "integer", nullable: false),
                    queued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    assigned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_heartbeat_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancel_requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    outcome_summary = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    pull_request_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    exit_code = table.Column<int>(type: "integer", nullable: true),
                    cost_usd = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    input_tokens = table.Column<long>(type: "bigint", nullable: true),
                    output_tokens = table.Column<long>(type: "bigint", nullable: true),
                    failure_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_runs", x => x.id);
                    table.CheckConstraint("ck_runs_finished_status", "(finished_at IS NOT NULL) = (status >= 3)");
                    table.CheckConstraint("ck_runs_max_minutes", "max_minutes BETWEEN 5 AND 720");
                    table.CheckConstraint("ck_runs_status", "status BETWEEN 0 AND 6");
                });

            migrationBuilder.CreateTable(
                name: "run_log_chunks",
                schema: "automation",
                columns: table => new
                {
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seq = table.Column<int>(type: "integer", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    stream = table.Column<short>(type: "smallint", nullable: false),
                    text = table.Column<string>(type: "character varying(65536)", maxLength: 65536, nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_run_log_chunks", x => new { x.run_id, x.seq });
                    table.CheckConstraint("ck_run_log_chunks_seq", "seq >= 0");
                    table.CheckConstraint("ck_run_log_chunks_stream", "stream BETWEEN 0 AND 2");
                    table.ForeignKey(
                        name: "fk_run_log_chunks_runs_run_id",
                        column: x => x.run_id,
                        principalSchema: "automation",
                        principalTable: "runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_runs_item",
                schema: "automation",
                table: "runs",
                column: "item_id");

            migrationBuilder.CreateIndex(
                name: "ix_runs_organization_status_queued",
                schema: "automation",
                table: "runs",
                columns: new[] { "organization_id", "status", "queued_at" });

            migrationBuilder.CreateIndex(
                name: "ix_runs_project_queued",
                schema: "automation",
                table: "runs",
                columns: new[] { "project_id", "queued_at" });

            migrationBuilder.CreateIndex(
                name: "ux_runs_item_live",
                schema: "automation",
                table: "runs",
                column: "item_id",
                unique: true,
                filter: "status < 3");

            TenantRls.Enable(migrationBuilder, "automation", "runs", "run_log_chunks");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            TenantRls.Disable(migrationBuilder, "automation", "run_log_chunks", "runs");

            migrationBuilder.DropTable(
                name: "run_log_chunks",
                schema: "automation");

            migrationBuilder.DropTable(
                name: "runs",
                schema: "automation");
        }
    }
}

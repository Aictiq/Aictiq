using System;
using Aictiq.SharedKernel.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aictiq.Modules.Automation.Migrations
{
    /// <inheritdoc />
    public partial class ProjectChat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_runs_kind",
                schema: "automation",
                table: "runs");

            migrationBuilder.AlterColumn<string>(
                name: "item_key",
                schema: "automation",
                table: "runs",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20);

            migrationBuilder.AlterColumn<Guid>(
                name: "item_id",
                schema: "automation",
                table: "runs",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.CreateTable(
                name: "project_chat_messages",
                schema: "automation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    question = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    answer = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    asked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    answered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_chat_messages", x => x.id);
                    table.CheckConstraint("ck_project_chat_messages_answered", "(status = 0) = (answered_at IS NULL)");
                    table.CheckConstraint("ck_project_chat_messages_question", "length(btrim(question)) BETWEEN 1 AND 4000");
                    table.CheckConstraint("ck_project_chat_messages_status", "status BETWEEN 0 AND 4");
                });

            migrationBuilder.CreateIndex(
                name: "ix_runs_project_chat_token",
                schema: "automation",
                table: "runs",
                column: "agent_token_id",
                filter: "kind = 4 AND agent_token_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_runs_project_chat_live",
                schema: "automation",
                table: "runs",
                columns: new[] { "project_id", "requested_by" },
                unique: true,
                filter: "kind = 4 AND status < 3");

            migrationBuilder.AddCheckConstraint(
                name: "ck_runs_item",
                schema: "automation",
                table: "runs",
                sql: "(item_id IS NULL) = (kind = 4) AND (item_id IS NULL) = (item_key IS NULL) AND (kind <> 4 OR requested_by IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_runs_kind",
                schema: "automation",
                table: "runs",
                sql: "kind IN (0, 1, 2, 3, 4)");

            migrationBuilder.CreateIndex(
                name: "ix_project_chat_messages_owner",
                schema: "automation",
                table: "project_chat_messages",
                columns: new[] { "project_id", "user_id", "asked_at" });

            migrationBuilder.CreateIndex(
                name: "ix_project_chat_messages_run",
                schema: "automation",
                table: "project_chat_messages",
                column: "run_id");

            TenantRls.Enable(migrationBuilder, "automation", "project_chat_messages");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            TenantRls.Disable(migrationBuilder, "automation", "project_chat_messages");

            // Runs without an item cannot survive item_id becoming required again.
            migrationBuilder.Sql("DELETE FROM automation.runs WHERE kind = 4;");

            migrationBuilder.DropTable(
                name: "project_chat_messages",
                schema: "automation");

            migrationBuilder.DropIndex(
                name: "ix_runs_project_chat_token",
                schema: "automation",
                table: "runs");

            migrationBuilder.DropIndex(
                name: "ux_runs_project_chat_live",
                schema: "automation",
                table: "runs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_runs_item",
                schema: "automation",
                table: "runs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_runs_kind",
                schema: "automation",
                table: "runs");

            migrationBuilder.AlterColumn<string>(
                name: "item_key",
                schema: "automation",
                table: "runs",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20,
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "item_id",
                schema: "automation",
                table: "runs",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_runs_kind",
                schema: "automation",
                table: "runs",
                sql: "kind IN (0, 1, 2, 3)");
        }
    }
}

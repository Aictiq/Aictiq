using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aictiq.Modules.Automation.Migrations
{
    /// <inheritdoc />
    public partial class RunContinuation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "auto_continue_due_at",
                schema: "automation",
                table: "runs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "auto_continued",
                schema: "automation",
                table: "runs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "auto_continues",
                schema: "automation",
                table: "runs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "continues_run_id",
                schema: "automation",
                table: "runs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "session_id",
                schema: "automation",
                table: "runs",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_runs_auto_continue_due",
                schema: "automation",
                table: "runs",
                column: "auto_continue_due_at",
                filter: "auto_continue_due_at IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_runs_continues",
                schema: "automation",
                table: "runs",
                column: "continues_run_id",
                unique: true,
                filter: "continues_run_id IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_runs_auto_continues",
                schema: "automation",
                table: "runs",
                sql: "auto_continues BETWEEN 0 AND 2");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_runs_auto_continue_due",
                schema: "automation",
                table: "runs");

            migrationBuilder.DropIndex(
                name: "ux_runs_continues",
                schema: "automation",
                table: "runs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_runs_auto_continues",
                schema: "automation",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "auto_continue_due_at",
                schema: "automation",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "auto_continued",
                schema: "automation",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "auto_continues",
                schema: "automation",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "continues_run_id",
                schema: "automation",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "session_id",
                schema: "automation",
                table: "runs");
        }
    }
}

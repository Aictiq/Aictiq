using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aictiq.Modules.Automation.Migrations
{
    /// <inheritdoc />
    public partial class ChatRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_runs_kind",
                schema: "automation",
                table: "runs");

            migrationBuilder.AddCheckConstraint(
                name: "ck_runs_kind",
                schema: "automation",
                table: "runs",
                sql: "kind IN (0, 1, 2)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_runs_kind",
                schema: "automation",
                table: "runs");

            migrationBuilder.AddCheckConstraint(
                name: "ck_runs_kind",
                schema: "automation",
                table: "runs",
                sql: "kind IN (0, 1)");
        }
    }
}

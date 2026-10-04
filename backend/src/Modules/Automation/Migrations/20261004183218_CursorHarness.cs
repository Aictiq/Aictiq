using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aictiq.Modules.Automation.Migrations
{
    /// <inheritdoc />
    public partial class CursorHarness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_playbooks_harness",
                schema: "automation",
                table: "playbooks");

            migrationBuilder.AddCheckConstraint(
                name: "ck_playbooks_harness",
                schema: "automation",
                table: "playbooks",
                sql: "harness IN ('claude', 'codex', 'opencode', 'cursor')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_playbooks_harness",
                schema: "automation",
                table: "playbooks");

            migrationBuilder.AddCheckConstraint(
                name: "ck_playbooks_harness",
                schema: "automation",
                table: "playbooks",
                sql: "harness IN ('claude', 'codex', 'opencode')");
        }
    }
}

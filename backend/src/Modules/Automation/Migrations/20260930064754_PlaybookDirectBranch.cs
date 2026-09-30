using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aictiq.Modules.Automation.Migrations
{
    /// <inheritdoc />
    public partial class PlaybookDirectBranch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "work_on_default_branch",
                schema: "automation",
                table: "runs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "work_on_default_branch",
                schema: "automation",
                table: "playbooks",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "work_on_default_branch",
                schema: "automation",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "work_on_default_branch",
                schema: "automation",
                table: "playbooks");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aictiq.Modules.Automation.Migrations
{
    /// <inheritdoc />
    public partial class PlaybookOutcomeColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "on_failure_column_id",
                schema: "automation",
                table: "playbooks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "on_success_column_id",
                schema: "automation",
                table: "playbooks",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "on_failure_column_id",
                schema: "automation",
                table: "playbooks");

            migrationBuilder.DropColumn(
                name: "on_success_column_id",
                schema: "automation",
                table: "playbooks");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aictiq.Modules.Automation.Migrations
{
    /// <inheritdoc />
    public partial class RunnerProjectDeclines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid[]>(
                name: "declined_runner_ids",
                schema: "automation",
                table: "runs",
                type: "uuid[]",
                nullable: false,
                defaultValueSql: "'{}'::uuid[]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "declined_runner_ids",
                schema: "automation",
                table: "runs");
        }
    }
}

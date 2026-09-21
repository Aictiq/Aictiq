using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aictiq.Modules.WorkItems.Migrations
{
    /// <inheritdoc />
    public partial class BoardColumnPlacement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "board_column_id",
                schema: "work",
                table: "items",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_items_team_board_column_rank",
                schema: "work",
                table: "items",
                columns: new[] { "team_id", "board_column_id", "rank" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_items_team_board_column_rank",
                schema: "work",
                table: "items");

            migrationBuilder.DropColumn(
                name: "board_column_id",
                schema: "work",
                table: "items");
        }
    }
}

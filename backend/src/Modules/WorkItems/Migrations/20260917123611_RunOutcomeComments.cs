using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aictiq.Modules.WorkItems.Migrations
{
    /// <inheritdoc />
    public partial class RunOutcomeComments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "event_id",
                schema: "work",
                table: "comments",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ux_comments_item_id_event_id",
                schema: "work",
                table: "comments",
                columns: new[] { "item_id", "event_id" },
                unique: true,
                filter: "event_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_comments_item_id_event_id",
                schema: "work",
                table: "comments");

            migrationBuilder.DropColumn(
                name: "event_id",
                schema: "work",
                table: "comments");
        }
    }
}

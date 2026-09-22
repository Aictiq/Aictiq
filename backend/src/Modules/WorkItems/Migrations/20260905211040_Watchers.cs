using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aictiq.Modules.WorkItems.Migrations
{
    /// <inheritdoc />
    public partial class Watchers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "watchers",
                schema: "work",
                columns: table => new
                {
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    reason = table.Column<short>(type: "smallint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    muted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_watchers", x => new { x.item_id, x.user_id });
                    table.ForeignKey(
                        name: "fk_watchers_items_item_id",
                        column: x => x.item_id,
                        principalSchema: "work",
                        principalTable: "items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_watchers_item_id_muted_at",
                schema: "work",
                table: "watchers",
                columns: new[] { "item_id", "muted_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "watchers",
                schema: "work");
        }
    }
}

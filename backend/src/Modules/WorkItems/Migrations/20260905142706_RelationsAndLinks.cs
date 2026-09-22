using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aictiq.Modules.WorkItems.Migrations
{
    /// <inheritdoc />
    public partial class RelationsAndLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "resolution",
                schema: "work",
                table: "items",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "item_links",
                schema: "work",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<short>(type: "smallint", nullable: false),
                    provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    external_id = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    state = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    meta = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_item_links", x => x.id);
                    table.ForeignKey(
                        name: "fk_item_links_items_item_id",
                        column: x => x.item_id,
                        principalSchema: "work",
                        principalTable: "items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "item_relations",
                schema: "work",
                columns: table => new
                {
                    source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<short>(type: "smallint", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_item_relations", x => new { x.source_id, x.target_id, x.kind });
                    table.CheckConstraint("ck_item_relations_not_self", "source_id <> target_id");
                    table.CheckConstraint("ck_item_relations_related_order", "kind <> 0 OR source_id < target_id");
                    table.ForeignKey(
                        name: "fk_item_relations_items_source_id",
                        column: x => x.source_id,
                        principalSchema: "work",
                        principalTable: "items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_item_relations_items_target_id",
                        column: x => x.target_id,
                        principalSchema: "work",
                        principalTable: "items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_item_links_idempotency",
                schema: "work",
                table: "item_links",
                columns: new[] { "item_id", "provider", "kind", "external_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_item_relations_target_id",
                schema: "work",
                table: "item_relations",
                column: "target_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "item_links",
                schema: "work");

            migrationBuilder.DropTable(
                name: "item_relations",
                schema: "work");

            migrationBuilder.DropColumn(
                name: "resolution",
                schema: "work",
                table: "items");
        }
    }
}

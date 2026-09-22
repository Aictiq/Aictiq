using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aictiq.Modules.WorkItems.Migrations
{
    /// <inheritdoc />
    public partial class ItemTemplates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "item_templates",
                schema: "work",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<short>(type: "smallint", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    description_markdown = table.Column<string>(type: "text", nullable: false),
                    default_label_ids = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    default_priority = table.Column<short>(type: "smallint", nullable: true),
                    is_default = table.Column<bool>(type: "boolean", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_item_templates", x => x.id);
                    table.CheckConstraint("ck_item_templates_name_not_blank", "length(btrim(name)) > 0");
                });

            migrationBuilder.CreateIndex(
                name: "ux_item_templates_default_type",
                schema: "work",
                table: "item_templates",
                columns: new[] { "project_id", "type", "is_default" },
                unique: true,
                filter: "is_default");

            migrationBuilder.CreateIndex(
                name: "ux_item_templates_name",
                schema: "work",
                table: "item_templates",
                columns: new[] { "organization_id", "project_id", "name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "item_templates",
                schema: "work");
        }
    }
}

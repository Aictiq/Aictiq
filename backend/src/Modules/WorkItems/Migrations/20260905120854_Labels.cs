using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aictiq.Modules.WorkItems.Migrations
{
    /// <summary>
    /// Project-scoped labels and the join table to items, plus one index EF cannot
    /// express. Two labels called "Frontend" and "frontend" on one project would be
    /// indistinguishable in a picker, so the name is unique case-insensitively — an index
    /// on <c>lower(name)</c>, which EF's <c>HasIndex</c> only takes columns for. Created
    /// here in SQL, same as <c>tenancy.projects</c>' <c>ux_projects_organization_id_name_lower</c>.
    /// </summary>
    public partial class Labels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "labels",
                schema: "work",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    color = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: true),
                    description = table.Column<string>(type: "character varying(280)", maxLength: 280, nullable: true),
                    group_name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_labels", x => x.id);
                    table.CheckConstraint("ck_labels_color_format", "color IS NULL OR color ~ '^#[0-9A-Fa-f]{6}$'");
                    table.CheckConstraint("ck_labels_group_not_blank", "group_name IS NULL OR length(btrim(group_name)) > 0");
                    table.CheckConstraint("ck_labels_name_not_blank", "length(btrim(name)) > 0 AND length(name) <= 50");
                });

            migrationBuilder.CreateTable(
                name: "item_labels",
                schema: "work",
                columns: table => new
                {
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    label_id = table.Column<Guid>(type: "uuid", nullable: false),
                    added_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    added_by = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_item_labels", x => new { x.item_id, x.label_id });
                    table.ForeignKey(
                        name: "fk_item_labels_items_item_id",
                        column: x => x.item_id,
                        principalSchema: "work",
                        principalTable: "items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_item_labels_labels_label_id",
                        column: x => x.label_id,
                        principalSchema: "work",
                        principalTable: "labels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_item_labels_label_id",
                schema: "work",
                table: "item_labels",
                column: "label_id");

            migrationBuilder.CreateIndex(
                name: "ix_labels_project_id",
                schema: "work",
                table: "labels",
                column: "project_id");

            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX ux_labels_project_name
                ON work.labels (organization_id, project_id, lower(name));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS work.ux_labels_project_name;");

            migrationBuilder.DropTable(
                name: "item_labels",
                schema: "work");

            migrationBuilder.DropTable(
                name: "labels",
                schema: "work");
        }
    }
}

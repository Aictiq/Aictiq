using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aictiq.Modules.WorkItems.Migrations
{
    /// <inheritdoc />
    public partial class SavedViews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "saved_views",
                schema: "work",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    filter = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    sort = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    columns = table.Column<string[]>(type: "text[]", nullable: false),
                    is_shared = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_saved_views", x => x.id);
                    table.CheckConstraint("ck_saved_views_name_not_blank", "length(btrim(name)) > 0");
                });

            migrationBuilder.CreateIndex(
                name: "ix_saved_views_project_id_is_shared_name",
                schema: "work",
                table: "saved_views",
                columns: new[] { "project_id", "is_shared", "name" });

            migrationBuilder.CreateIndex(
                name: "ux_saved_views_owner_name",
                schema: "work",
                table: "saved_views",
                columns: new[] { "project_id", "owner_id", "name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "saved_views",
                schema: "work");
        }
    }
}

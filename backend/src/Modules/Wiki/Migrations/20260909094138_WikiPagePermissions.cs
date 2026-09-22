using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aictiq.Modules.Wiki.Migrations
{
    /// <inheritdoc />
    public partial class WikiPagePermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "page_permissions",
                schema: "wiki",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    page_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject_kind = table.Column<short>(type: "smallint", nullable: false),
                    subject_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    access = table.Column<short>(type: "smallint", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_page_permissions", x => x.id);
                    table.CheckConstraint("ck_page_permissions_subject", "(subject_kind <> 2 OR subject_id IN ('guest', 'member', 'admin'))");
                    table.ForeignKey(
                        name: "fk_page_permissions_pages_page_id",
                        column: x => x.page_id,
                        principalSchema: "wiki",
                        principalTable: "pages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_page_permissions_page_id",
                schema: "wiki",
                table: "page_permissions",
                column: "page_id");

            migrationBuilder.CreateIndex(
                name: "ix_page_permissions_page_id_subject_kind_subject_id",
                schema: "wiki",
                table: "page_permissions",
                columns: new[] { "page_id", "subject_kind", "subject_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "page_permissions",
                schema: "wiki");
        }
    }
}

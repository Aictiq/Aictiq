using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aictiq.Modules.WorkItems.Migrations
{
    /// <inheritdoc />
    public partial class Attachments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "attachments",
                schema: "work",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    comment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    object_key = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    file_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    content_type = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    uploaded_by = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    committed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_attachments", x => x.id);
                    table.CheckConstraint("ck_attachments_owner_and_status", "(status = 0 AND item_id IS NULL AND comment_id IS NULL) OR (status = 1 AND ((item_id IS NOT NULL)::integer + (comment_id IS NOT NULL)::integer = 1))");
                    table.ForeignKey(
                        name: "fk_attachments_comments_comment_id",
                        column: x => x.comment_id,
                        principalSchema: "work",
                        principalTable: "comments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_attachments_items_item_id",
                        column: x => x.item_id,
                        principalSchema: "work",
                        principalTable: "items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_attachments_comment_id",
                schema: "work",
                table: "attachments",
                column: "comment_id");

            migrationBuilder.CreateIndex(
                name: "ix_attachments_item_id",
                schema: "work",
                table: "attachments",
                column: "item_id");

            migrationBuilder.CreateIndex(
                name: "ix_attachments_object_key",
                schema: "work",
                table: "attachments",
                column: "object_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_attachments_project_id_comment_id",
                schema: "work",
                table: "attachments",
                columns: new[] { "project_id", "comment_id" });

            migrationBuilder.CreateIndex(
                name: "ix_attachments_project_id_item_id",
                schema: "work",
                table: "attachments",
                columns: new[] { "project_id", "item_id" });

            migrationBuilder.CreateIndex(
                name: "ix_attachments_status_created_at",
                schema: "work",
                table: "attachments",
                columns: new[] { "status", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "attachments",
                schema: "work");
        }
    }
}

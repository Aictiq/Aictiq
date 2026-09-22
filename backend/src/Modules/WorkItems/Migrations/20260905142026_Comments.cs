using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aictiq.Modules.WorkItems.Migrations
{
    /// <inheritdoc />
    public partial class Comments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "comments",
                schema: "work",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    author_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    body_markdown = table.Column<string>(type: "text", nullable: false),
                    body_html = table.Column<string>(type: "text", nullable: false),
                    mentioned_user_ids = table.Column<string[]>(type: "text[]", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    edited_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_comments", x => x.id);
                    table.ForeignKey(
                        name: "fk_comments_items_item_id",
                        column: x => x.item_id,
                        principalSchema: "work",
                        principalTable: "items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "comment_reactions",
                schema: "work",
                columns: table => new
                {
                    comment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    emoji = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_comment_reactions", x => new { x.comment_id, x.user_id, x.emoji });
                    table.ForeignKey(
                        name: "fk_comment_reactions_comments_comment_id",
                        column: x => x.comment_id,
                        principalSchema: "work",
                        principalTable: "comments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "comment_revisions",
                schema: "work",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    comment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    body_markdown = table.Column<string>(type: "text", nullable: false),
                    body_html = table.Column<string>(type: "text", nullable: false),
                    edited_by = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    edited_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_comment_revisions", x => x.id);
                    table.ForeignKey(
                        name: "fk_comment_revisions_comments_comment_id",
                        column: x => x.comment_id,
                        principalSchema: "work",
                        principalTable: "comments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_comment_revisions_comment_id_edited_at",
                schema: "work",
                table: "comment_revisions",
                columns: new[] { "comment_id", "edited_at" });

            migrationBuilder.CreateIndex(
                name: "ix_comments_item_id_created_at",
                schema: "work",
                table: "comments",
                columns: new[] { "item_id", "created_at" });

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION work.reject_comment_revision_mutation()
                RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'comment revisions are append-only';
                END;
                $$;
                CREATE TRIGGER trg_comment_revisions_append_only
                BEFORE UPDATE OR DELETE ON work.comment_revisions
                FOR EACH ROW EXECUTE FUNCTION work.reject_comment_revision_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_comment_revisions_append_only ON work.comment_revisions; DROP FUNCTION IF EXISTS work.reject_comment_revision_mutation();");

            migrationBuilder.DropTable(
                name: "comment_reactions",
                schema: "work");

            migrationBuilder.DropTable(
                name: "comment_revisions",
                schema: "work");

            migrationBuilder.DropTable(
                name: "comments",
                schema: "work");
        }
    }
}

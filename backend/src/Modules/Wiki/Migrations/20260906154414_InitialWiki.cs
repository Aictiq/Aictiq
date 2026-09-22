using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aictiq.Modules.Wiki.Migrations
{
    /// <inheritdoc />
    public partial class InitialWiki : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "wiki");

            migrationBuilder.CreateTable(
                name: "pages",
                schema: "wiki",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    parent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    slug = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    current_revision_id = table.Column<Guid>(type: "uuid", nullable: true),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pages", x => x.id);
                    table.ForeignKey(
                        name: "fk_pages_pages_parent_id",
                        column: x => x.parent_id,
                        principalSchema: "wiki",
                        principalTable: "pages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "page_revisions",
                schema: "wiki",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    page_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    content_markdown = table.Column<string>(type: "character varying(100000)", maxLength: 100000, nullable: false),
                    content_html = table.Column<string>(type: "character varying(200000)", maxLength: 200000, nullable: false),
                    author_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    summary = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_page_revisions", x => x.id);
                    table.ForeignKey(
                        name: "fk_page_revisions_pages_page_id",
                        column: x => x.page_id,
                        principalSchema: "wiki",
                        principalTable: "pages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_page_revisions_number",
                schema: "wiki",
                table: "page_revisions",
                columns: new[] { "page_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_pages_parent_id",
                schema: "wiki",
                table: "pages",
                column: "parent_id");

            migrationBuilder.CreateIndex(
                name: "ix_pages_project_id_parent_id_position",
                schema: "wiki",
                table: "pages",
                columns: new[] { "project_id", "parent_id", "position" });

            migrationBuilder.CreateIndex(
                name: "ix_pages_project_id_updated_at",
                schema: "wiki",
                table: "pages",
                columns: new[] { "project_id", "updated_at" });

            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX ux_pages_live_parent_slug
                ON wiki.pages (organization_id, project_id, COALESCE(parent_id, '00000000-0000-0000-0000-000000000000'::uuid), slug)
                WHERE deleted_at IS NULL;
                ALTER TABLE wiki.pages ADD CONSTRAINT ck_pages_position_nonnegative CHECK (position >= 0);

                CREATE OR REPLACE FUNCTION wiki.validate_page_parent()
                RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE parent_depth integer; subtree_depth integer;
                BEGIN
                    IF NEW.parent_id IS NULL THEN RETURN NEW; END IF;
                    IF NEW.parent_id = NEW.id THEN RAISE EXCEPTION 'wiki_cycle'; END IF;
                    IF NOT EXISTS (SELECT 1 FROM wiki.pages p WHERE p.id = NEW.parent_id
                        AND p.project_id = NEW.project_id AND p.organization_id = NEW.organization_id) THEN
                        RAISE EXCEPTION 'invalid_parent';
                    END IF;
                    IF EXISTS (
                        WITH RECURSIVE ancestors AS (
                            SELECT id, parent_id FROM wiki.pages WHERE id = NEW.parent_id
                            UNION ALL SELECT p.id, p.parent_id FROM wiki.pages p JOIN ancestors a ON p.id = a.parent_id
                        ) SELECT 1 FROM ancestors WHERE id = NEW.id
                    ) THEN RAISE EXCEPTION 'wiki_cycle'; END IF;
                    WITH RECURSIVE ancestors AS (
                        SELECT id, parent_id, 1 AS depth FROM wiki.pages WHERE id = NEW.parent_id
                        UNION ALL SELECT p.id, p.parent_id, a.depth + 1 FROM wiki.pages p JOIN ancestors a ON p.id = a.parent_id
                    ) SELECT max(depth) INTO parent_depth FROM ancestors;
                    WITH RECURSIVE descendants AS (
                        SELECT id, 1 AS depth FROM wiki.pages WHERE id = NEW.id
                        UNION ALL SELECT p.id, d.depth + 1 FROM wiki.pages p JOIN descendants d ON p.parent_id = d.id
                    ) SELECT COALESCE(max(depth), 1) INTO subtree_depth FROM descendants;
                    IF parent_depth + subtree_depth > 10 THEN RAISE EXCEPTION 'wiki_depth'; END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER trg_pages_validate_parent
                BEFORE INSERT OR UPDATE OF parent_id, project_id, organization_id ON wiki.pages
                FOR EACH ROW EXECUTE FUNCTION wiki.validate_page_parent();

                CREATE OR REPLACE FUNCTION wiki.reject_page_revision_mutation()
                RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'page revisions are append-only'; END;
                $$;
                CREATE TRIGGER trg_page_revisions_append_only
                BEFORE UPDATE OR DELETE ON wiki.page_revisions
                FOR EACH ROW EXECUTE FUNCTION wiki.reject_page_revision_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_page_revisions_append_only ON wiki.page_revisions;
                DROP FUNCTION IF EXISTS wiki.reject_page_revision_mutation();
                DROP TRIGGER IF EXISTS trg_pages_validate_parent ON wiki.pages;
                DROP FUNCTION IF EXISTS wiki.validate_page_parent();
                """);
            migrationBuilder.DropTable(
                name: "page_revisions",
                schema: "wiki");

            migrationBuilder.DropTable(
                name: "pages",
                schema: "wiki");
        }
    }
}

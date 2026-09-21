using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aictiq.Modules.Wiki.Migrations
{
    /// <summary>
    /// Wiki pages are deleted for good rather than hidden. Deleting a page takes every page
    /// below it (the parent key cascades) and, through the page keys that already cascade,
    /// its revisions, permissions and item links.
    ///
    /// Revisions stay append-only: UPDATE is still impossible, and a bare DELETE still raises.
    /// Only wiki.delete_page_subtree() holds the transaction-local flag that lets the cascade
    /// through — the same shape as shared.purge_audit_log.
    /// </summary>
    public partial class WikiHardDelete : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_pages_pages_parent_id",
                schema: "wiki",
                table: "pages");

            migrationBuilder.AddForeignKey(
                name: "fk_pages_pages_parent_id",
                schema: "wiki",
                table: "pages",
                column: "parent_id",
                principalSchema: "wiki",
                principalTable: "pages",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION wiki.reject_page_revision_mutation()
                RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    -- UPDATE is never permitted: a revision is history. DELETE is permitted
                    -- only while wiki.delete_page_subtree() holds this transaction-local flag.
                    IF TG_OP = 'DELETE' AND current_setting('app.wiki_page_purge', true) = 'on' THEN
                        RETURN OLD;
                    END IF;
                    RAISE EXCEPTION 'page revisions are append-only';
                END;
                $$;

                CREATE FUNCTION wiki.delete_page_subtree(root uuid)
                RETURNS SETOF uuid LANGUAGE plpgsql AS $$
                BEGIN
                    RETURN QUERY
                        WITH RECURSIVE subtree AS (
                            SELECT id FROM wiki.pages WHERE id = root
                            UNION ALL SELECT p.id FROM wiki.pages p JOIN subtree s ON p.parent_id = s.id
                        ) SELECT id FROM subtree;

                    -- Held for exactly this DELETE and its cascades, then cleared, so the
                    -- permission never outlives the call even inside the caller's transaction.
                    SET LOCAL app.wiki_page_purge = 'on';
                    DELETE FROM wiki.pages WHERE id = root;
                    SET LOCAL app.wiki_page_purge = 'off';
                END;
                $$;

                -- Pages soft-deleted before this migration are deleted for real.
                SET LOCAL app.wiki_page_purge = 'on';
                DELETE FROM wiki.pages WHERE deleted_at IS NOT NULL;
                SET LOCAL app.wiki_page_purge = 'off';

                DROP INDEX wiki.ux_pages_live_parent_slug;
                """);

            migrationBuilder.DropColumn(
                name: "deleted_at",
                schema: "wiki",
                table: "pages");

            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX ux_pages_parent_slug
                ON wiki.pages (organization_id, project_id, COALESCE(parent_id, '00000000-0000-0000-0000-000000000000'::uuid), slug);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deleted pages are gone; this restores the shape, not the rows.
            migrationBuilder.Sql("""
                DROP INDEX IF EXISTS wiki.ux_pages_parent_slug;
                DROP FUNCTION IF EXISTS wiki.delete_page_subtree(uuid);
                CREATE OR REPLACE FUNCTION wiki.reject_page_revision_mutation()
                RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'page revisions are append-only'; END;
                $$;
                """);

            migrationBuilder.DropForeignKey(
                name: "fk_pages_pages_parent_id",
                schema: "wiki",
                table: "pages");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "deleted_at",
                schema: "wiki",
                table: "pages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX ux_pages_live_parent_slug
                ON wiki.pages (organization_id, project_id, COALESCE(parent_id, '00000000-0000-0000-0000-000000000000'::uuid), slug)
                WHERE deleted_at IS NULL;
                """);

            migrationBuilder.AddForeignKey(
                name: "fk_pages_pages_parent_id",
                schema: "wiki",
                table: "pages",
                column: "parent_id",
                principalSchema: "wiki",
                principalTable: "pages",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}

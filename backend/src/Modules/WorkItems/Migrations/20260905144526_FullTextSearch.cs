using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;

#nullable disable

namespace Aictiq.Modules.WorkItems.Migrations
{
    /// <inheritdoc />
    public partial class FullTextSearch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS pg_trgm;");

            migrationBuilder.AddColumn<string>(
                name: "label_search",
                schema: "work",
                table: "items",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<NpgsqlTsVector>(
                name: "search",
                schema: "work",
                table: "items",
                type: "tsvector",
                nullable: false,
                computedColumnSql: "setweight(to_tsvector('english', coalesce(title, '')), 'A') ||\nsetweight(to_tsvector('simple', coalesce(title, '')), 'A') ||\nsetweight(to_tsvector('english', coalesce(project_key || '-' || number::text, '')), 'A') ||\nsetweight(to_tsvector('simple', coalesce(project_key || '-' || number::text, '')), 'A') ||\nsetweight(to_tsvector('english', coalesce(label_search, '')), 'B') ||\nsetweight(to_tsvector('simple', coalesce(label_search, '')), 'B') ||\nsetweight(to_tsvector('english', coalesce(description_markdown, '')), 'C') ||\nsetweight(to_tsvector('simple', coalesce(description_markdown, '')), 'C')",
                stored: true);

            migrationBuilder.AddColumn<NpgsqlTsVector>(
                name: "search",
                schema: "work",
                table: "comments",
                type: "tsvector",
                nullable: false,
                computedColumnSql: "to_tsvector('english', coalesce(body_markdown, '')) ||\nto_tsvector('simple', coalesce(body_markdown, ''))",
                stored: true);

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION work.refresh_item_label_search(item uuid)
                RETURNS void LANGUAGE sql AS $$
                    UPDATE work.items target
                    SET label_search = COALESCE((
                        SELECT string_agg(label.name, ' ' ORDER BY label.name)
                        FROM work.item_labels item_label
                        JOIN work.labels label ON label.id = item_label.label_id
                        WHERE item_label.item_id = item
                    ), '')
                    WHERE target.id = item;
                $$;
                CREATE OR REPLACE FUNCTION work.sync_item_label_search()
                RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        PERFORM work.refresh_item_label_search(OLD.item_id);
                    ELSE
                        PERFORM work.refresh_item_label_search(NEW.item_id);
                    END IF;
                    RETURN NULL;
                END;
                $$;
                CREATE TRIGGER trg_item_labels_search
                AFTER INSERT OR UPDATE OR DELETE ON work.item_labels
                FOR EACH ROW EXECUTE FUNCTION work.sync_item_label_search();
                CREATE OR REPLACE FUNCTION work.sync_label_name_search()
                RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW.name IS DISTINCT FROM OLD.name THEN
                        UPDATE work.items target
                        SET label_search = COALESCE((
                            SELECT string_agg(label.name, ' ' ORDER BY label.name)
                            FROM work.item_labels item_label
                            JOIN work.labels label ON label.id = item_label.label_id
                            WHERE item_label.item_id = target.id
                        ), '')
                        WHERE target.id IN (SELECT item_id FROM work.item_labels WHERE label_id = NEW.id);
                    END IF;
                    RETURN NULL;
                END;
                $$;
                CREATE TRIGGER trg_labels_search
                AFTER UPDATE OF name ON work.labels
                FOR EACH ROW EXECUTE FUNCTION work.sync_label_name_search();
                UPDATE work.items target
                SET label_search = COALESCE((
                    SELECT string_agg(label.name, ' ' ORDER BY label.name)
                    FROM work.item_labels item_label
                    JOIN work.labels label ON label.id = item_label.label_id
                    WHERE item_label.item_id = target.id
                ), '');
                CREATE INDEX ix_items_search ON work.items USING GIN (search);
                CREATE INDEX ix_comments_search ON work.comments USING GIN (search);
                CREATE INDEX ix_items_title_trgm ON work.items USING GIN (title gin_trgm_ops);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_labels_search ON work.labels;
                DROP TRIGGER IF EXISTS trg_item_labels_search ON work.item_labels;
                DROP FUNCTION IF EXISTS work.sync_label_name_search();
                DROP FUNCTION IF EXISTS work.sync_item_label_search();
                DROP FUNCTION IF EXISTS work.refresh_item_label_search(uuid);
                DROP INDEX IF EXISTS work.ix_items_title_trgm;
                DROP INDEX IF EXISTS work.ix_comments_search;
                DROP INDEX IF EXISTS work.ix_items_search;
                """);
            migrationBuilder.DropColumn(name: "search", schema: "work", table: "comments");
            migrationBuilder.DropColumn(name: "search", schema: "work", table: "items");
            migrationBuilder.DropColumn(name: "label_search", schema: "work", table: "items");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aictiq.Modules.WorkItems.Migrations
{
    /// <summary>
    /// Makes the work-item hierarchy an invariant of the database, so bulk imports and
    /// direct SQL cannot bypass the endpoint's friendlier validation.
    /// </summary>
    public partial class ItemParentHierarchy : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE FUNCTION work.check_item_parent_type()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                DECLARE
                    parent_item work.items%ROWTYPE;
                    contains_self boolean;
                    exceeds_max_depth boolean;
                BEGIN
                    IF NEW.parent_id IS NULL THEN
                        IF NEW.type IN (1, 2, 3) THEN
                            RAISE EXCEPTION 'invalid_parent'
                                USING ERRCODE = 'P0001',
                                      DETAIL = 'Feature, Story, and Task items require a parent.';
                        END IF;

                        RETURN NEW;
                    END IF;

                    IF NEW.parent_id = NEW.id THEN
                        RAISE EXCEPTION 'invalid_parent'
                            USING ERRCODE = 'P0001', DETAIL = 'An item cannot be its own parent.';
                    END IF;

                    SELECT * INTO parent_item
                    FROM work.items
                    WHERE id = NEW.parent_id;

                    IF NOT FOUND THEN
                        RAISE EXCEPTION 'invalid_parent'
                            USING ERRCODE = 'P0001', DETAIL = 'The selected parent does not exist.';
                    END IF;

                    IF parent_item.project_id <> NEW.project_id THEN
                        RAISE EXCEPTION 'invalid_parent'
                            USING ERRCODE = 'P0001', DETAIL = 'The parent must belong to the same project.';
                    END IF;

                    CASE NEW.type
                        WHEN 0 THEN
                            RAISE EXCEPTION 'invalid_parent'
                                USING ERRCODE = 'P0001', DETAIL = 'Epic items cannot have a parent.';
                        WHEN 1 THEN
                            IF parent_item.type <> 0 THEN
                                RAISE EXCEPTION 'invalid_parent'
                                    USING ERRCODE = 'P0001', DETAIL = 'A Feature must have an Epic parent.';
                            END IF;
                        WHEN 2 THEN
                            IF parent_item.type <> 1 THEN
                                RAISE EXCEPTION 'invalid_parent'
                                    USING ERRCODE = 'P0001', DETAIL = 'A Story must have a Feature parent.';
                            END IF;
                        WHEN 3 THEN
                            IF parent_item.type <> 2 THEN
                                RAISE EXCEPTION 'invalid_parent'
                                    USING ERRCODE = 'P0001', DETAIL = 'A Task must have a Story parent.';
                            END IF;
                        WHEN 4 THEN
                            IF parent_item.type NOT IN (1, 2) THEN
                                RAISE EXCEPTION 'invalid_parent'
                                    USING ERRCODE = 'P0001', DETAIL = 'A Bug may have only a Feature or Story parent.';
                            END IF;
                        ELSE
                            RAISE EXCEPTION 'invalid_parent'
                                USING ERRCODE = 'P0001', DETAIL = 'Unknown work-item type.';
                    END CASE;

                    WITH RECURSIVE ancestors AS (
                        SELECT id, parent_id, 1 AS depth
                        FROM work.items
                        WHERE id = NEW.parent_id

                        UNION ALL

                        SELECT item.id, item.parent_id, ancestor.depth + 1
                        FROM work.items AS item
                        INNER JOIN ancestors AS ancestor ON item.id = ancestor.parent_id
                        WHERE ancestor.parent_id IS NOT NULL AND ancestor.depth < 6
                    )
                    SELECT COALESCE(bool_or(id = NEW.id), false),
                           COALESCE(bool_or(depth = 6 AND parent_id IS NOT NULL), false)
                    INTO contains_self, exceeds_max_depth
                    FROM ancestors;

                    IF contains_self THEN
                        RAISE EXCEPTION 'invalid_parent'
                            USING ERRCODE = 'P0001', DETAIL = 'An item cannot be parented beneath itself.';
                    END IF;

                    IF exceeds_max_depth THEN
                        RAISE EXCEPTION 'invalid_parent'
                            USING ERRCODE = 'P0001', DETAIL = 'The hierarchy may not exceed six levels.';
                    END IF;

                    RETURN NEW;
                END;
                $$;

                CREATE TRIGGER check_item_parent_type
                BEFORE INSERT OR UPDATE OF parent_id, type, project_id ON work.items
                FOR EACH ROW
                EXECUTE FUNCTION work.check_item_parent_type();
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS check_item_parent_type ON work.items;
                DROP FUNCTION IF EXISTS work.check_item_parent_type();
                """);
        }
    }
}

using Aictiq.Modules.WorkItems;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aictiq.Modules.WorkItems.Migrations;

/// <summary>
/// Work items, projects and organizations are deleted for good. Everything under an item
/// already cascades from it; what stood in the way is the history an item keeps. Item history
/// and comment revisions stay append-only — UPDATE always raises, a bare DELETE still raises —
/// and DELETE is let through only while one of the purge functions below holds the
/// transaction-local <c>app.work_purge</c> flag: the shape of wiki.delete_page_subtree.
/// </summary>
[DbContext(typeof(WorkItemsDbContext))]
[Migration("20260916200100_WorkItemsHardDelete")]
public partial class WorkItemsHardDelete : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE OR REPLACE FUNCTION work.reject_item_history_mutation()
            RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF TG_OP = 'DELETE' AND current_setting('app.work_purge', true) = 'on' THEN
                    RETURN OLD;
                END IF;
                RAISE EXCEPTION 'item history is append-only';
            END;
            $$;

            CREATE OR REPLACE FUNCTION work.reject_comment_revision_mutation()
            RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF TG_OP = 'DELETE' AND current_setting('app.work_purge', true) = 'on' THEN
                    RETURN OLD;
                END IF;
                RAISE EXCEPTION 'comment revisions are append-only';
            END;
            $$;

            -- The item and every item below it. One DELETE for the whole subtree, so the
            -- restricting parent key is satisfied at the end of the statement: nothing is
            -- left pointing at a deleted parent. Returns the ids it deleted.
            CREATE FUNCTION work.delete_item_subtree(root uuid)
            RETURNS SETOF uuid LANGUAGE plpgsql AS $$
            DECLARE
                doomed uuid[];
            BEGIN
                WITH RECURSIVE subtree AS (
                    SELECT id FROM work.items WHERE id = root
                    UNION SELECT i.id FROM work.items i JOIN subtree s ON i.parent_id = s.id
                ) SELECT array_agg(id) INTO doomed FROM subtree;

                IF doomed IS NULL THEN
                    RETURN;
                END IF;

                SET LOCAL app.work_purge = 'on';
                DELETE FROM work.items WHERE id = ANY(doomed);
                SET LOCAL app.work_purge = 'off';

                RETURN QUERY SELECT unnest(doomed);
            END;
            $$;

            -- Everything WorkItems keeps for one project. Sprints and boards belong to teams,
            -- which Tenancy has already deleted, so their ids are passed in.
            CREATE FUNCTION work.purge_project(project uuid, team_ids uuid[])
            RETURNS void LANGUAGE plpgsql AS $$
            BEGIN
                SET LOCAL app.work_purge = 'on';
                DELETE FROM work.items WHERE project_id = project;
                DELETE FROM work.attachments WHERE project_id = project;
                DELETE FROM work.sprints WHERE team_id = ANY(team_ids);
                DELETE FROM work.boards WHERE team_id = ANY(team_ids);
                DELETE FROM work.workflows WHERE project_id = project;
                DELETE FROM work.labels WHERE project_id = project;
                DELETE FROM work.item_templates WHERE project_id = project;
                DELETE FROM work.saved_views WHERE project_id = project;
                DELETE FROM work.csv_import_jobs WHERE project_id = project;
                DELETE FROM work.project_sequences WHERE project_id = project;
                SET LOCAL app.work_purge = 'off';
            END;
            $$;

            -- Everything WorkItems keeps for one organization. Children first, so the
            -- restricting keys (item parent, item state) never see a dangling reference.
            CREATE FUNCTION work.purge_organization(org uuid)
            RETURNS void LANGUAGE plpgsql AS $$
            BEGIN
                SET LOCAL app.work_purge = 'on';
                DELETE FROM work.items WHERE organization_id = org;
                DELETE FROM work.attachments WHERE organization_id = org;
                DELETE FROM work.sprint_capacity WHERE organization_id = org;
                DELETE FROM work.sprint_scope_log WHERE organization_id = org;
                DELETE FROM work.sprints WHERE organization_id = org;
                DELETE FROM work.boards WHERE organization_id = org;
                DELETE FROM work.workflows WHERE organization_id = org;
                DELETE FROM work.labels WHERE organization_id = org;
                DELETE FROM work.item_templates WHERE organization_id = org;
                DELETE FROM work.saved_views WHERE organization_id = org;
                DELETE FROM work.csv_import_jobs WHERE organization_id = org;
                DELETE FROM work.project_sequences WHERE organization_id = org;
                SET LOCAL app.work_purge = 'off';
            END;
            $$;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Deleted rows are gone; this restores the guarantees, not the rows.
        migrationBuilder.Sql("""
            DROP FUNCTION IF EXISTS work.purge_organization(uuid);
            DROP FUNCTION IF EXISTS work.purge_project(uuid, uuid[]);
            DROP FUNCTION IF EXISTS work.delete_item_subtree(uuid);
            CREATE OR REPLACE FUNCTION work.reject_item_history_mutation()
            RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'item history is append-only'; END;
            $$;
            CREATE OR REPLACE FUNCTION work.reject_comment_revision_mutation()
            RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'comment revisions are append-only'; END;
            $$;
            """);
    }
}

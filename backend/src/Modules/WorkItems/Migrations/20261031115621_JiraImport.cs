using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Aictiq.SharedKernel.Persistence;

#nullable disable

namespace Aictiq.Modules.WorkItems.Migrations
{
    /// <inheritdoc />
    public partial class JiraImport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "external_ref",
                schema: "work",
                table: "sprints",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "jira_import_jobs",
                schema: "work",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_key = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    organization_slug = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    requested_by = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    site_url = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    protected_token = table.Column<string>(type: "text", nullable: true),
                    jira_project_key = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    jira_project_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    issue_type_map = table.Column<string>(type: "jsonb", nullable: false),
                    status_map = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    total_issues = table.Column<int>(type: "integer", nullable: false),
                    processed_issues = table.Column<int>(type: "integer", nullable: false),
                    created_items = table.Column<int>(type: "integer", nullable: false),
                    skipped_issues = table.Column<int>(type: "integer", nullable: false),
                    created_comments = table.Column<int>(type: "integer", nullable: false),
                    created_attachments = table.Column<int>(type: "integer", nullable: false),
                    created_sprints = table.Column<int>(type: "integer", nullable: false),
                    failures = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    heartbeat_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_jira_import_jobs", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ux_sprints_team_external_ref",
                schema: "work",
                table: "sprints",
                columns: new[] { "team_id", "external_ref" },
                unique: true,
                filter: "external_ref IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_jira_import_jobs_project_id_created_at",
                schema: "work",
                table: "jira_import_jobs",
                columns: new[] { "project_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_jira_import_jobs_status_created_at",
                schema: "work",
                table: "jira_import_jobs",
                columns: new[] { "status", "created_at" });

            TenantRls.Enable(migrationBuilder, "work", "jira_import_jobs");
            // A deleted project or organization takes its import history with it.
            migrationBuilder.Sql("""
            CREATE OR REPLACE FUNCTION work.purge_project(project uuid, team_ids uuid[])
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
                DELETE FROM work.jira_import_jobs WHERE project_id = project;
                DELETE FROM work.project_sequences WHERE project_id = project;
                SET LOCAL app.work_purge = 'off';
            END;
            $$;

            CREATE OR REPLACE FUNCTION work.purge_organization(org uuid)
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
                DELETE FROM work.jira_import_jobs WHERE organization_id = org;
                DELETE FROM work.project_sequences WHERE organization_id = org;
                SET LOCAL app.work_purge = 'off';
            END;
            $$;
            """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
            CREATE OR REPLACE FUNCTION work.purge_project(project uuid, team_ids uuid[])
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

            CREATE OR REPLACE FUNCTION work.purge_organization(org uuid)
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

            migrationBuilder.DropTable(
                name: "jira_import_jobs",
                schema: "work");

            migrationBuilder.DropIndex(
                name: "ux_sprints_team_external_ref",
                schema: "work",
                table: "sprints");

            migrationBuilder.DropColumn(
                name: "external_ref",
                schema: "work",
                table: "sprints");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Aictiq.SharedKernel.Persistence;

#nullable disable

namespace Aictiq.Modules.Automation.Migrations
{
    /// <inheritdoc />
    public partial class PlaybooksAndProjectFactorySettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "playbooks",
                schema: "automation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    wiki_page_id = table.Column<Guid>(type: "uuid", nullable: true),
                    harness = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    on_success_state_id = table.Column<Guid>(type: "uuid", nullable: true),
                    on_failure_state_id = table.Column<Guid>(type: "uuid", nullable: true),
                    max_minutes = table.Column<int>(type: "integer", nullable: false),
                    is_default = table.Column<bool>(type: "boolean", nullable: false),
                    created_by = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_playbooks", x => x.id);
                    table.CheckConstraint("ck_playbooks_harness", "harness IN ('claude', 'codex', 'opencode')");
                    table.CheckConstraint("ck_playbooks_max_minutes", "max_minutes BETWEEN 5 AND 720");
                    table.CheckConstraint("ck_playbooks_name", "length(btrim(name)) BETWEEN 1 AND 100");
                });

            migrationBuilder.CreateTable(
                name: "project_settings",
                schema: "automation",
                columns: table => new
                {
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    repo_source = table.Column<short>(type: "smallint", nullable: false),
                    repo_full_name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    default_branch = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false, defaultValue: "main"),
                    local_path_hint = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    default_agent_id = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_settings", x => x.project_id);
                    table.CheckConstraint("ck_project_settings_default_branch", "length(btrim(default_branch)) BETWEEN 1 AND 255");
                    table.CheckConstraint("ck_project_settings_github_repo", "repo_source <> 0 OR repo_full_name IS NOT NULL");
                    table.CheckConstraint("ck_project_settings_repo_source", "repo_source IN (0, 1)");
                });

            migrationBuilder.CreateIndex(
                name: "ix_playbooks_project_id",
                schema: "automation",
                table: "playbooks",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ux_playbooks_default_project",
                schema: "automation",
                table: "playbooks",
                columns: new[] { "project_id", "is_default" },
                unique: true,
                filter: "is_default");

            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX ux_playbooks_project_name
                    ON automation.playbooks (project_id, lower(name));
                """);

            TenantRls.Enable(migrationBuilder, "automation", "playbooks");
            TenantRls.Enable(migrationBuilder, "automation", "project_settings");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            TenantRls.Disable(migrationBuilder, "automation", "project_settings");
            TenantRls.Disable(migrationBuilder, "automation", "playbooks");

            migrationBuilder.DropTable(
                name: "playbooks",
                schema: "automation");

            migrationBuilder.DropTable(
                name: "project_settings",
                schema: "automation");
        }
    }
}

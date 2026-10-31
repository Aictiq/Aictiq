using System;
using Aictiq.SharedKernel.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aictiq.Modules.Automation.Migrations
{
    /// <inheritdoc />
    public partial class MultiRepo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_chain_steps_kind",
                schema: "automation",
                table: "chain_steps");

            migrationBuilder.AddColumn<string[]>(
                name: "pull_request_urls",
                schema: "automation",
                table: "runs",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'::text[]");

            migrationBuilder.AddColumn<string>(
                name: "repositories",
                schema: "automation",
                table: "runs",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[]'::jsonb");

            migrationBuilder.AddColumn<string[]>(
                name: "repository_aliases",
                schema: "automation",
                table: "runs",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'::text[]");

            migrationBuilder.AddColumn<string>(
                name: "repository_plan",
                schema: "automation",
                table: "runs",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "repository_root_run_id",
                schema: "automation",
                table: "runs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "working_directory",
                schema: "automation",
                table: "runs",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "multi_repo_enabled",
                schema: "automation",
                table: "project_settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "primary_alias",
                schema: "automation",
                table: "project_settings",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "primary_working_directory",
                schema: "automation",
                table: "project_settings",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "repository_steps",
                schema: "automation",
                table: "playbooks",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[]'::jsonb");

            migrationBuilder.AddColumn<string[]>(
                name: "repository_aliases",
                schema: "automation",
                table: "chain_steps",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'::text[]");

            migrationBuilder.AddColumn<string[]>(
                name: "wait_for_merge_urls",
                schema: "automation",
                table: "chain_steps",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'::text[]");

            migrationBuilder.CreateTable(
                name: "project_repositories",
                schema: "automation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    alias = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    repo_source = table.Column<short>(type: "smallint", nullable: false),
                    repo_full_name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    default_branch = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false, defaultValue: "main"),
                    local_path_hint = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    working_directory = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_repositories", x => x.id);
                    table.CheckConstraint("ck_project_repositories_alias", "alias ~ '^[a-z0-9][a-z0-9._-]{0,39}$'");
                    table.CheckConstraint("ck_project_repositories_default_branch", "length(btrim(default_branch)) BETWEEN 1 AND 255");
                    table.CheckConstraint("ck_project_repositories_github_repo", "repo_source <> 0 OR repo_full_name IS NOT NULL");
                    table.CheckConstraint("ck_project_repositories_repo_source", "repo_source IN (0, 1)");
                });

            migrationBuilder.CreateIndex(
                name: "ix_runs_repository_root",
                schema: "automation",
                table: "runs",
                column: "repository_root_run_id",
                filter: "repository_root_run_id IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_chain_steps_kind",
                schema: "automation",
                table: "chain_steps",
                sql: "kind IN (0, 1, 2)");

            migrationBuilder.CreateIndex(
                name: "ux_project_repositories_project_alias",
                schema: "automation",
                table: "project_repositories",
                columns: new[] { "project_id", "alias" },
                unique: true);

            TenantRls.Enable(migrationBuilder, "automation", "project_repositories");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            TenantRls.Disable(migrationBuilder, "automation", "project_repositories");

            migrationBuilder.DropTable(
                name: "project_repositories",
                schema: "automation");

            migrationBuilder.DropIndex(
                name: "ix_runs_repository_root",
                schema: "automation",
                table: "runs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_chain_steps_kind",
                schema: "automation",
                table: "chain_steps");

            migrationBuilder.DropColumn(
                name: "pull_request_urls",
                schema: "automation",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "repositories",
                schema: "automation",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "repository_aliases",
                schema: "automation",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "repository_plan",
                schema: "automation",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "repository_root_run_id",
                schema: "automation",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "working_directory",
                schema: "automation",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "multi_repo_enabled",
                schema: "automation",
                table: "project_settings");

            migrationBuilder.DropColumn(
                name: "primary_alias",
                schema: "automation",
                table: "project_settings");

            migrationBuilder.DropColumn(
                name: "primary_working_directory",
                schema: "automation",
                table: "project_settings");

            migrationBuilder.DropColumn(
                name: "repository_steps",
                schema: "automation",
                table: "playbooks");

            migrationBuilder.DropColumn(
                name: "repository_aliases",
                schema: "automation",
                table: "chain_steps");

            migrationBuilder.DropColumn(
                name: "wait_for_merge_urls",
                schema: "automation",
                table: "chain_steps");

            // Repository steps cannot survive the kind check without them.
            migrationBuilder.Sql("DELETE FROM automation.chain_steps WHERE kind = 2;");

            migrationBuilder.AddCheckConstraint(
                name: "ck_chain_steps_kind",
                schema: "automation",
                table: "chain_steps",
                sql: "kind IN (0, 1)");
        }
    }
}

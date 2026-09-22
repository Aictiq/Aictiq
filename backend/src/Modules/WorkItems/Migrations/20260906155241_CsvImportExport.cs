using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aictiq.Modules.WorkItems.Migrations
{
    /// <inheritdoc />
    public partial class CsvImportExport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "external_ref",
                schema: "work",
                table: "items",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "csv_import_jobs",
                schema: "work",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_key = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    requested_by = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    csv = table.Column<string>(type: "text", nullable: false),
                    mapping_json = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    total_rows = table.Column<int>(type: "integer", nullable: false),
                    processed_rows = table.Column<int>(type: "integer", nullable: false),
                    created_rows = table.Column<int>(type: "integer", nullable: false),
                    skipped_rows = table.Column<int>(type: "integer", nullable: false),
                    errors = table.Column<string[]>(type: "text[]", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_csv_import_jobs", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ux_items_project_external_ref",
                schema: "work",
                table: "items",
                columns: new[] { "project_id", "external_ref" },
                unique: true,
                filter: "external_ref IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_csv_import_jobs_project_id",
                schema: "work",
                table: "csv_import_jobs",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ix_csv_import_jobs_status_created_at",
                schema: "work",
                table: "csv_import_jobs",
                columns: new[] { "status", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "csv_import_jobs",
                schema: "work");

            migrationBuilder.DropIndex(
                name: "ux_items_project_external_ref",
                schema: "work",
                table: "items");

            migrationBuilder.DropColumn(
                name: "external_ref",
                schema: "work",
                table: "items");
        }
    }
}

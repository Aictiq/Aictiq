using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aictiq.Modules.WorkItems.Migrations
{
    /// <inheritdoc />
    public partial class SeedItemTemplatesOnce : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "templates_seeded",
                schema: "work",
                table: "project_sequences",
                type: "boolean",
                nullable: false,
                defaultValue: false);
            // Existing templates, including renamed built-ins, are already initialized.
            // Do not reset item numbering when creating or updating the project marker.
            migrationBuilder.Sql("""
                INSERT INTO work.project_sequences (project_id, organization_id, next_number, templates_seeded)
                SELECT DISTINCT project_id, organization_id, 1, true FROM work.item_templates
                ON CONFLICT (project_id) DO UPDATE SET templates_seeded = true;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "templates_seeded",
                schema: "work",
                table: "project_sequences");
        }
    }
}

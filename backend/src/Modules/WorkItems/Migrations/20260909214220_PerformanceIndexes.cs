using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aictiq.Modules.WorkItems.Migrations
{
    /// <inheritdoc />
    public partial class PerformanceIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // EXPLAIN ANALYZE at 100k items / 20 projects showed every lookup that
            // addresses an item by its human key seq-scanning the table: GET /items/{key}
            // (FindVisible) walked 924 buffers to return one row (2.1 ms), and the MCP tools
            // that filter on project_key alone (list_ready_work, search_items, bulk_update)
            // read all 100k rows and discarded 95k (25 ms). The unique
            // (organization_id, project_id, number) index cannot serve these - they carry
            // project_key, not project_id.
            migrationBuilder.CreateIndex(
                name: "ix_items_organization_id_project_key_number",
                schema: "work",
                table: "items",
                columns: new[] { "organization_id", "project_key", "number" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_items_organization_id_project_key_number",
                schema: "work",
                table: "items");
        }
    }
}

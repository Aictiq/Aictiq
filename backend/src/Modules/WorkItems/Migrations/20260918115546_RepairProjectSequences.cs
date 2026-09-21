using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aictiq.Modules.WorkItems.Migrations
{
    /// <summary>
    /// Repairs the item-number sequences the MCP <c>create_item</c> tool left behind: it took
    /// max + 1 instead of a number from <c>work.project_sequences</c>, so once an agent had
    /// created an item, every create through the API collided on the unique number and
    /// answered 409 — and, the failed insert rolling back its own increment, kept doing so.
    /// </summary>
    public partial class RepairProjectSequences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                INSERT INTO work.project_sequences (project_id, organization_id, next_number)
                SELECT project_id, organization_id, max(number) + 1
                FROM work.items
                GROUP BY project_id, organization_id
                ON CONFLICT (project_id) DO UPDATE
                SET next_number = GREATEST(work.project_sequences.next_number, excluded.next_number);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // A repair of broken rows: there is nothing worth breaking again.
        }
    }
}

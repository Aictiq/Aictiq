using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aictiq.Modules.Wiki.Migrations
{
    /// <summary>
    /// Repairs the Home pages `WikiProjectCreatedHandler` created without a current revision:
    /// its trailing UPDATE could not see the page inserted earlier in the same statement. Each
    /// such page gets its latest revision, which for an untouched Home is the empty #1.
    /// </summary>
    public partial class WikiHomeCurrentRevision : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE wiki.pages p
                SET current_revision_id = latest.id
                FROM (SELECT DISTINCT ON (page_id) id, page_id FROM wiki.page_revisions ORDER BY page_id, number DESC) latest
                WHERE latest.page_id = p.id AND p.current_revision_id IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // A repair of broken rows: there is nothing worth breaking again.
        }
    }
}

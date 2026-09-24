using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aictiq.Modules.Wiki.Migrations
{
    /// <summary>
    /// Marks each project's Factory section. The starter playbook has always filed its page
    /// under a top-level page with the slug <c>factory</c>, so that page is the section.
    /// </summary>
    public partial class WikiFactorySection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_factory_section",
                schema: "wiki",
                table: "pages",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "ux_pages_project_id_factory_section",
                schema: "wiki",
                table: "pages",
                column: "project_id",
                unique: true,
                filter: "is_factory_section");

            migrationBuilder.Sql("UPDATE wiki.pages SET is_factory_section = true WHERE parent_id IS NULL AND slug = 'factory';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_pages_project_id_factory_section",
                schema: "wiki",
                table: "pages");

            migrationBuilder.DropColumn(
                name: "is_factory_section",
                schema: "wiki",
                table: "pages");
        }
    }
}

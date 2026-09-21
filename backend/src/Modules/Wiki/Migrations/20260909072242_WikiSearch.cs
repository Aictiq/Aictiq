using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;

#nullable disable

namespace Aictiq.Modules.Wiki.Migrations
{
    /// <inheritdoc />
    public partial class WikiSearch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<NpgsqlTsVector>(
                name: "search",
                schema: "wiki",
                table: "pages",
                type: "tsvector",
                nullable: false,
                computedColumnSql: "setweight(to_tsvector('english', coalesce(title, '')), 'A') || setweight(to_tsvector('simple', coalesce(title, '')), 'A')",
                stored: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "search",
                schema: "wiki",
                table: "pages");
        }
    }
}

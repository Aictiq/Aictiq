using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aictiq.Modules.Wiki.Migrations
{
    /// <inheritdoc />
    public partial class WikiRevisionContentLimit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "content_markdown",
                schema: "wiki",
                table: "page_revisions",
                type: "character varying(1048576)",
                maxLength: 1048576,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(100000)",
                oldMaxLength: 100000);

            migrationBuilder.AlterColumn<string>(
                name: "content_html",
                schema: "wiki",
                table: "page_revisions",
                type: "character varying(2097152)",
                maxLength: 2097152,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(200000)",
                oldMaxLength: 200000);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "content_markdown",
                schema: "wiki",
                table: "page_revisions",
                type: "character varying(100000)",
                maxLength: 100000,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(1048576)",
                oldMaxLength: 1048576);

            migrationBuilder.AlterColumn<string>(
                name: "content_html",
                schema: "wiki",
                table: "page_revisions",
                type: "character varying(200000)",
                maxLength: 200000,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(2097152)",
                oldMaxLength: 2097152);
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aictiq.Modules.Identity.Migrations
{
    /// <inheritdoc />
    public partial class ProjectChatTokenScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_personal_access_tokens_scopes",
                schema: "identity",
                table: "personal_access_tokens");

            migrationBuilder.AddCheckConstraint(
                name: "ck_personal_access_tokens_scopes",
                schema: "identity",
                table: "personal_access_tokens",
                sql: "scopes <@ ARRAY['read','write','admin','mcp','project-chat']::text[]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Project-chat run tokens live minutes; they cannot survive the narrower constraint.
            migrationBuilder.Sql("DELETE FROM identity.personal_access_tokens WHERE 'project-chat' = ANY(scopes);");

            migrationBuilder.DropCheckConstraint(
                name: "ck_personal_access_tokens_scopes",
                schema: "identity",
                table: "personal_access_tokens");

            migrationBuilder.AddCheckConstraint(
                name: "ck_personal_access_tokens_scopes",
                schema: "identity",
                table: "personal_access_tokens",
                sql: "scopes <@ ARRAY['read','write','admin','mcp']::text[]");
        }
    }
}

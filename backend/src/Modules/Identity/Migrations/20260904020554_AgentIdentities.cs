using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aictiq.Modules.Identity.Migrations
{
    /// <inheritdoc />
    public partial class AgentIdentities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "agent_owner_user_id",
                schema: "identity",
                table: "AspNetUsers",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "avatar_key",
                schema: "identity",
                table: "AspNetUsers",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_agent",
                schema: "identity",
                table: "AspNetUsers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "ix_asp_net_users_agent_owner_user_id",
                schema: "identity",
                table: "AspNetUsers",
                column: "agent_owner_user_id",
                filter: "agent_owner_user_id IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_users_agent_has_owner",
                schema: "identity",
                table: "AspNetUsers",
                sql: "is_agent = (agent_owner_user_id IS NOT NULL)");

            migrationBuilder.AddForeignKey(
                name: "fk_asp_net_users_asp_net_users_agent_owner_user_id",
                schema: "identity",
                table: "AspNetUsers",
                column: "agent_owner_user_id",
                principalSchema: "identity",
                principalTable: "AspNetUsers",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_asp_net_users_asp_net_users_agent_owner_user_id",
                schema: "identity",
                table: "AspNetUsers");

            migrationBuilder.DropIndex(
                name: "ix_asp_net_users_agent_owner_user_id",
                schema: "identity",
                table: "AspNetUsers");

            migrationBuilder.DropCheckConstraint(
                name: "ck_users_agent_has_owner",
                schema: "identity",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "agent_owner_user_id",
                schema: "identity",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "avatar_key",
                schema: "identity",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "is_agent",
                schema: "identity",
                table: "AspNetUsers");
        }
    }
}

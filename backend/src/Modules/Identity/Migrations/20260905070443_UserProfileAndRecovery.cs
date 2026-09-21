using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aictiq.Modules.Identity.Migrations
{
    /// <inheritdoc />
    public partial class UserProfileAndRecovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "EmailIndex",
                schema: "identity",
                table: "AspNetUsers");

            migrationBuilder.AddColumn<string>(
                name: "user_agent",
                schema: "identity",
                table: "refresh_tokens",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "time_zone",
                schema: "identity",
                table: "AspNetUsers",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "user_security_tokens",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    purpose = table.Column<int>(type: "integer", nullable: false),
                    token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    new_email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_security_tokens", x => x.id);
                    table.CheckConstraint("ck_user_security_tokens_new_email", "(purpose = 1) = (new_email IS NOT NULL)");
                    table.CheckConstraint("ck_user_security_tokens_new_email_lower", "new_email IS NULL OR new_email = lower(new_email)");
                    table.ForeignKey(
                        name: "fk_user_security_tokens_asp_net_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_users_normalized_email",
                schema: "identity",
                table: "AspNetUsers",
                column: "normalized_email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_user_security_tokens_token_hash",
                schema: "identity",
                table: "user_security_tokens",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_user_security_tokens_live",
                schema: "identity",
                table: "user_security_tokens",
                columns: new[] { "user_id", "purpose" },
                unique: true,
                filter: "used_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "user_security_tokens",
                schema: "identity");

            migrationBuilder.DropIndex(
                name: "ux_users_normalized_email",
                schema: "identity",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "user_agent",
                schema: "identity",
                table: "refresh_tokens");

            migrationBuilder.DropColumn(
                name: "time_zone",
                schema: "identity",
                table: "AspNetUsers");

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                schema: "identity",
                table: "AspNetUsers",
                column: "normalized_email");
        }
    }
}

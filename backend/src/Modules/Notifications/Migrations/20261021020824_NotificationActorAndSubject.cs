using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aictiq.Modules.Notifications.Migrations
{
    /// <inheritdoc />
    public partial class NotificationActorAndSubject : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "actor_id",
                schema: "notify",
                table: "notifications",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "emoji",
                schema: "notify",
                table: "notifications",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "from_state",
                schema: "notify",
                table: "notifications",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "subject",
                schema: "notify",
                table: "notifications",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "to_state",
                schema: "notify",
                table: "notifications",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "actor_id",
                schema: "notify",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "emoji",
                schema: "notify",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "from_state",
                schema: "notify",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "subject",
                schema: "notify",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "to_state",
                schema: "notify",
                table: "notifications");
        }
    }
}

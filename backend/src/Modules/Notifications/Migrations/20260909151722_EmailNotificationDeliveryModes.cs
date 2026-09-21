using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aictiq.Modules.Notifications.Migrations
{
    /// <inheritdoc />
    public partial class EmailNotificationDeliveryModes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<short>(
                name: "email_mode",
                schema: "notify",
                table: "preferences",
                type: "smallint",
                nullable: false,
                // Existing true meant "send as soon as it happens", while false meant off.
                // Add then copy before dropping the source column so an upgrade preserves it.
                defaultValue: (short)1);

            migrationBuilder.Sql("UPDATE notify.preferences SET email_mode = CASE WHEN email THEN 1 ELSE 0 END");

            migrationBuilder.DropColumn(
                name: "email",
                schema: "notify",
                table: "preferences");

            migrationBuilder.CreateTable(
                name: "digests",
                schema: "notify",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    last_delivered_local_date = table.Column<DateOnly>(type: "date", nullable: true),
                    last_delivered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_digests", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "presence",
                schema: "notify",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_presence", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_digests_user_id",
                schema: "notify",
                table: "digests",
                column: "user_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_presence_user_id",
                schema: "notify",
                table: "presence",
                column: "user_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "digests",
                schema: "notify");

            migrationBuilder.DropTable(
                name: "presence",
                schema: "notify");

            migrationBuilder.AddColumn<bool>(
                name: "email",
                schema: "notify",
                table: "preferences",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql("UPDATE notify.preferences SET email = email_mode <> 0");

            migrationBuilder.DropColumn(
                name: "email_mode",
                schema: "notify",
                table: "preferences");
        }
    }
}

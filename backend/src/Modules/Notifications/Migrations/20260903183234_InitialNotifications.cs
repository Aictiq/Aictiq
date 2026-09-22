using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aictiq.Modules.Notifications.Migrations
{
    /// <inheritdoc />
    public partial class InitialNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "notify");

            migrationBuilder.CreateTable(
                name: "email_outbox",
                schema: "notify",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    to_address = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    subject = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    body_html = table.Column<string>(type: "text", nullable: false),
                    body_text = table.Column<string>(type: "text", nullable: false),
                    template = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    last_error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    send_after = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_email_outbox", x => x.id);
                    table.CheckConstraint("ck_email_outbox_recipient_not_blank", "length(btrim(to_address)) > 0");
                    table.CheckConstraint("ck_email_outbox_sent_consistency", "(status = 'sent') = (sent_at IS NOT NULL)");
                    table.CheckConstraint("ck_email_outbox_status", "status IN ('pending','sent','failed','skipped')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_email_outbox_pending",
                schema: "notify",
                table: "email_outbox",
                column: "send_after",
                filter: "status = 'pending'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "email_outbox",
                schema: "notify");
        }
    }
}

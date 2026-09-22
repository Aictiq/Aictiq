using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aictiq.Modules.Identity.Migrations
{
    /// <inheritdoc />
    public partial class OutboxDeadLetter : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_outbox_messages_pending",
                schema: "shared",
                table: "outbox_messages");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "dead_lettered_at",
                schema: "shared",
                table: "outbox_messages",
                type: "timestamp with time zone",
                nullable: true);

            // Rows that already exhausted the attempt budget were silently excluded by the
            // old "attempts < 10" filter. Mark them dead-lettered so they surface on the
            // health check instead of quietly resuming delivery under the new query.
            migrationBuilder.Sql(
                """
                UPDATE shared.outbox_messages
                SET dead_lettered_at = now()
                WHERE processed_at IS NULL AND attempts >= 10;
                """);

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_dead_lettered",
                schema: "shared",
                table: "outbox_messages",
                column: "dead_lettered_at",
                filter: "dead_lettered_at IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_pending",
                schema: "shared",
                table: "outbox_messages",
                column: "occurred_at",
                filter: "processed_at IS NULL AND dead_lettered_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_outbox_messages_dead_lettered",
                schema: "shared",
                table: "outbox_messages");

            migrationBuilder.DropIndex(
                name: "ix_outbox_messages_pending",
                schema: "shared",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "dead_lettered_at",
                schema: "shared",
                table: "outbox_messages");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_pending",
                schema: "shared",
                table: "outbox_messages",
                column: "occurred_at",
                filter: "processed_at IS NULL");
        }
    }
}

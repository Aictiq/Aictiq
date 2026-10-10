using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aictiq.Modules.Notifications.Migrations
{
    /// <inheritdoc />
    public partial class PersonalChatNotificationSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<short>(
                name: "kind",
                schema: "notify",
                table: "chat_outbox",
                type: "smallint",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "source_organization_id",
                schema: "notify",
                table: "chat_outbox",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<short>(
                name: "kind",
                schema: "notify",
                table: "chat_digest_entries",
                type: "smallint",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "source_organization_id",
                schema: "notify",
                table: "chat_digest_entries",
                type: "uuid",
                nullable: true);

            // Personal queue ids were already derived from notification/channel ids.
            // Recover their origin so pre-upgrade messages obey the same access checks.
            // Guid(byte[]) reverses the first three fields relative to UUID byte order.
            foreach (var table in new[] { "chat_outbox", "chat_digest_entries" })
                migrationBuilder.Sql($"""
                    WITH sources AS (
                        SELECT n.organization_id, n.kind, c.id AS channel_id,
                            encode(sha256(convert_to('chat:' || n.id::text || ':' || c.id::text, 'UTF8')), 'hex') AS hash
                        FROM notify.notifications n
                        JOIN notify.user_channels c ON c.user_id = n.user_id
                        WHERE c.id IN (SELECT channel_id FROM notify.{table} WHERE organization_id IS NULL)
                    ), matches AS (
                        SELECT organization_id, kind, channel_id,
                            (substr(hash, 7, 2) || substr(hash, 5, 2) || substr(hash, 3, 2) || substr(hash, 1, 2)
                             || substr(hash, 11, 2) || substr(hash, 9, 2)
                             || substr(hash, 15, 2) || substr(hash, 13, 2) || substr(hash, 17, 16))::uuid AS id
                        FROM sources
                    )
                    UPDATE notify.{table} q
                    SET source_organization_id = m.organization_id, kind = m.kind
                    FROM matches m
                    WHERE q.id = m.id AND q.channel_id = m.channel_id AND q.organization_id IS NULL;
                    """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "kind",
                schema: "notify",
                table: "chat_outbox");

            migrationBuilder.DropColumn(
                name: "source_organization_id",
                schema: "notify",
                table: "chat_outbox");

            migrationBuilder.DropColumn(
                name: "kind",
                schema: "notify",
                table: "chat_digest_entries");

            migrationBuilder.DropColumn(
                name: "source_organization_id",
                schema: "notify",
                table: "chat_digest_entries");
        }
    }
}

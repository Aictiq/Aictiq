using System;
using Aictiq.SharedKernel.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Aictiq.Modules.Notifications.Migrations
{
    /// <inheritdoc />
    public partial class ChatNotificationChannels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<short>(
                name: "discord_mode",
                schema: "notify",
                table: "preferences",
                type: "smallint",
                nullable: true);

            migrationBuilder.AddColumn<short>(
                name: "slack_mode",
                schema: "notify",
                table: "preferences",
                type: "smallint",
                nullable: true);

            migrationBuilder.AddColumn<short>(
                name: "telegram_mode",
                schema: "notify",
                table: "preferences",
                type: "smallint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "chat_connect_codes",
                schema: "notify",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    channel_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_chat_connect_codes", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "chat_digest_entries",
                schema: "notify",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: true),
                    line = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_chat_digest_entries", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "chat_outbox",
                schema: "notify",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: true),
                    text = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    last_error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    send_after = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_chat_outbox", x => x.id);
                    table.CheckConstraint("ck_chat_outbox_status", "status IN ('pending','sent','failed','skipped')");
                });

            migrationBuilder.CreateTable(
                name: "data_protection_keys",
                schema: "notify",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    friendly_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    xml = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_data_protection_keys", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "org_channels",
                schema: "notify",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    type = table.Column<short>(type: "smallint", nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    protected_target = table.Column<string>(type: "text", nullable: true),
                    target_hint = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    last_error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    consecutive_failures = table.Column<int>(type: "integer", nullable: false),
                    modes = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    connected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_digest_date = table.Column<DateOnly>(type: "date", nullable: true),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_org_channels", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "org_defaults",
                schema: "notify",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<short>(type: "smallint", nullable: false),
                    email_mode = table.Column<short>(type: "smallint", nullable: true),
                    telegram_mode = table.Column<short>(type: "smallint", nullable: true),
                    slack_mode = table.Column<short>(type: "smallint", nullable: true),
                    discord_mode = table.Column<short>(type: "smallint", nullable: true),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_org_defaults", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "user_channels",
                schema: "notify",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    type = table.Column<short>(type: "smallint", nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    protected_target = table.Column<string>(type: "text", nullable: true),
                    target_hint = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    last_error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    consecutive_failures = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    connected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_digest_local_date = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_channels", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_chat_connect_codes_channel_id",
                schema: "notify",
                table: "chat_connect_codes",
                column: "channel_id");

            migrationBuilder.CreateIndex(
                name: "ix_chat_connect_codes_code_hash",
                schema: "notify",
                table: "chat_connect_codes",
                column: "code_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_chat_digest_entries_channel_id_created_at",
                schema: "notify",
                table: "chat_digest_entries",
                columns: new[] { "channel_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_chat_outbox_channel_id",
                schema: "notify",
                table: "chat_outbox",
                column: "channel_id");

            migrationBuilder.CreateIndex(
                name: "ix_chat_outbox_pending",
                schema: "notify",
                table: "chat_outbox",
                column: "send_after",
                filter: "status = 'pending'");

            migrationBuilder.CreateIndex(
                name: "ix_org_channels_organization_id",
                schema: "notify",
                table: "org_channels",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "ix_org_defaults_organization_id_kind",
                schema: "notify",
                table: "org_defaults",
                columns: new[] { "organization_id", "kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_user_channels_user_id_type",
                schema: "notify",
                table: "user_channels",
                columns: new[] { "user_id", "type" },
                unique: true);

            // Shared channels and defaults are tenant rows. The rest are read by sweeps that
            // span every organization, like email_outbox; Enable also grants on those.
            TenantRls.Enable(migrationBuilder, "notify", "org_channels", "org_defaults");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            TenantRls.Disable(migrationBuilder, "notify", "org_channels", "org_defaults");
            migrationBuilder.DropTable(
                name: "chat_connect_codes",
                schema: "notify");

            migrationBuilder.DropTable(
                name: "chat_digest_entries",
                schema: "notify");

            migrationBuilder.DropTable(
                name: "chat_outbox",
                schema: "notify");

            migrationBuilder.DropTable(
                name: "data_protection_keys",
                schema: "notify");

            migrationBuilder.DropTable(
                name: "org_channels",
                schema: "notify");

            migrationBuilder.DropTable(
                name: "org_defaults",
                schema: "notify");

            migrationBuilder.DropTable(
                name: "user_channels",
                schema: "notify");

            migrationBuilder.DropColumn(
                name: "discord_mode",
                schema: "notify",
                table: "preferences");

            migrationBuilder.DropColumn(
                name: "slack_mode",
                schema: "notify",
                table: "preferences");

            migrationBuilder.DropColumn(
                name: "telegram_mode",
                schema: "notify",
                table: "preferences");
        }
    }
}

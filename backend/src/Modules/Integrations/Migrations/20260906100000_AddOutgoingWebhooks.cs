using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aictiq.Modules.Integrations.Migrations;

[DbContext(typeof(IntegrationsDbContext))]
[Migration("20260906100000_AddOutgoingWebhooks")]
public partial class AddOutgoingWebhooks : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "webhook_subscriptions", schema: "integrations",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                project_id = table.Column<Guid>(type: "uuid", nullable: true),
                url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                secret_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                secret_protected = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                events = table.Column<string[]>(type: "text[]", nullable: false),
                active = table.Column<bool>(type: "boolean", nullable: false),
                consecutive_failures = table.Column<int>(type: "integer", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                organization_id = table.Column<Guid>(type: "uuid", nullable: false)
            }, constraints: table => table.PrimaryKey("pk_webhook_subscriptions", x => x.id));
        migrationBuilder.CreateTable(
            name: "webhook_deliveries", schema: "integrations",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                subscription_id = table.Column<Guid>(type: "uuid", nullable: false),
                event_id = table.Column<Guid>(type: "uuid", nullable: false),
                event_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                payload = table.Column<string>(type: "jsonb", nullable: false),
                attempt = table.Column<int>(type: "integer", nullable: false),
                status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                status_code = table.Column<int>(type: "integer", nullable: true),
                response_excerpt = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                last_error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                delivered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                organization_id = table.Column<Guid>(type: "uuid", nullable: false)
            }, constraints: table => table.PrimaryKey("pk_webhook_deliveries", x => x.id));
        migrationBuilder.CreateIndex(name: "ix_webhook_subscriptions_organization_id_project_id_active", schema: "integrations", table: "webhook_subscriptions", columns: new[] { "organization_id", "project_id", "active" });
        migrationBuilder.CreateIndex(name: "ux_webhook_deliveries_subscription_event", schema: "integrations", table: "webhook_deliveries", columns: new[] { "subscription_id", "event_id" }, unique: true);
        migrationBuilder.CreateIndex(name: "ix_webhook_deliveries_status_next_attempt_at", schema: "integrations", table: "webhook_deliveries", columns: new[] { "status", "next_attempt_at" });
    }
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "webhook_deliveries", schema: "integrations");
        migrationBuilder.DropTable(name: "webhook_subscriptions", schema: "integrations");
    }
}

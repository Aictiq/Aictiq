using System;
using Aictiq.SharedKernel.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aictiq.Modules.Automation.Migrations
{
    /// <inheritdoc />
    public partial class FleetMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string[]>(
                name: "required_tags",
                schema: "automation",
                table: "runs",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'::text[]");

            migrationBuilder.AddColumn<bool>(
                name: "ephemeral",
                schema: "automation",
                table: "runners",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string[]>(
                name: "tags",
                schema: "automation",
                table: "runners",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'::text[]");

            migrationBuilder.AddColumn<string[]>(
                name: "required_tags",
                schema: "automation",
                table: "rules",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'::text[]");

            migrationBuilder.AddColumn<string[]>(
                name: "required_tags",
                schema: "automation",
                table: "playbooks",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'::text[]");

            migrationBuilder.CreateTable(
                name: "fleet_settings",
                schema: "automation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    scale_webhook_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    scale_webhook_secret_protected = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    scale_after_seconds = table.Column<int>(type: "integer", nullable: false),
                    scale_cooldown_seconds = table.Column<int>(type: "integer", nullable: false),
                    last_scale_call_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_scale_call_error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    enroll_token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    enroll_token_prefix = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_fleet_settings", x => x.id);
                    table.CheckConstraint("ck_fleet_settings_scale_after", "scale_after_seconds BETWEEN 30 AND 86400");
                    table.CheckConstraint("ck_fleet_settings_scale_cooldown", "scale_cooldown_seconds BETWEEN 60 AND 86400");
                    table.CheckConstraint("ck_fleet_settings_webhook_secret", "scale_webhook_url IS NULL OR scale_webhook_secret_protected IS NOT NULL");
                });

            migrationBuilder.CreateIndex(
                name: "ux_fleet_settings_organization_id",
                schema: "automation",
                table: "fleet_settings",
                column: "organization_id",
                unique: true);

            TenantRls.Enable(migrationBuilder, "automation", "fleet_settings");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            TenantRls.Disable(migrationBuilder, "automation", "fleet_settings");

            migrationBuilder.DropTable(
                name: "fleet_settings",
                schema: "automation");

            migrationBuilder.DropColumn(
                name: "required_tags",
                schema: "automation",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "ephemeral",
                schema: "automation",
                table: "runners");

            migrationBuilder.DropColumn(
                name: "tags",
                schema: "automation",
                table: "runners");

            migrationBuilder.DropColumn(
                name: "required_tags",
                schema: "automation",
                table: "rules");

            migrationBuilder.DropColumn(
                name: "required_tags",
                schema: "automation",
                table: "playbooks");
        }
    }
}

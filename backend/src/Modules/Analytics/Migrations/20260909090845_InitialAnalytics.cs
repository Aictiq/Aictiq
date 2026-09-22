using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aictiq.Modules.Analytics.Migrations
{
    /// <inheritdoc />
    public partial class InitialAnalytics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "analytics");

            migrationBuilder.CreateTable(
                name: "item_state_daily",
                schema: "analytics",
                columns: table => new
                {
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    state_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sprint_id = table.Column<Guid>(type: "uuid", nullable: true),
                    points = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    estimate_hours = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    remaining_hours = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    completed_hours = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    captured_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_item_state_daily", x => new { x.organization_id, x.item_id, x.day });
                });

            migrationBuilder.CreateTable(
                name: "item_transitions",
                schema: "analytics",
                columns: table => new
                {
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_state_id = table.Column<Guid>(type: "uuid", nullable: false),
                    to_state_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sprint_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_item_transitions", x => x.event_id);
                });

            migrationBuilder.CreateTable(
                name: "sprint_scope_log",
                schema: "analytics",
                columns: table => new
                {
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sprint_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    added = table.Column<bool>(type: "boolean", nullable: false),
                    points = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    remaining_hours = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sprint_scope_log", x => x.event_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_item_state_daily_organization_id_day_project_id",
                schema: "analytics",
                table: "item_state_daily",
                columns: new[] { "organization_id", "day", "project_id" });

            migrationBuilder.CreateIndex(
                name: "ix_item_transitions_organization_id_item_id_at",
                schema: "analytics",
                table: "item_transitions",
                columns: new[] { "organization_id", "item_id", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_item_transitions_organization_id_project_id_at",
                schema: "analytics",
                table: "item_transitions",
                columns: new[] { "organization_id", "project_id", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_sprint_scope_log_organization_id_sprint_id_at",
                schema: "analytics",
                table: "sprint_scope_log",
                columns: new[] { "organization_id", "sprint_id", "at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "item_state_daily",
                schema: "analytics");

            migrationBuilder.DropTable(
                name: "item_transitions",
                schema: "analytics");

            migrationBuilder.DropTable(
                name: "sprint_scope_log",
                schema: "analytics");
        }
    }
}

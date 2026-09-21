using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aictiq.Modules.WorkItems.Migrations
{
    /// <inheritdoc />
    public partial class Sprints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "sprints",
                schema: "work",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    team_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    goal = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    starts_on = table.Column<DateOnly>(type: "date", nullable: false),
                    ends_on = table.Column<DateOnly>(type: "date", nullable: false),
                    state = table.Column<short>(type: "smallint", nullable: false),
                    auto_create_next = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sprints", x => x.id);
                    table.CheckConstraint("ck_sprints_dates", "ends_on > starts_on");
                });

            migrationBuilder.CreateTable(
                name: "sprint_scope_log",
                schema: "work",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    sprint_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    change = table.Column<short>(type: "smallint", nullable: false),
                    points = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    remaining_hours = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sprint_scope_log", x => x.id);
                    table.ForeignKey(
                        name: "fk_sprint_scope_log_items_item_id",
                        column: x => x.item_id,
                        principalSchema: "work",
                        principalTable: "items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_sprint_scope_log_sprints_sprint_id",
                        column: x => x.sprint_id,
                        principalSchema: "work",
                        principalTable: "sprints",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_sprint_scope_log_item_id_at",
                schema: "work",
                table: "sprint_scope_log",
                columns: new[] { "item_id", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_sprint_scope_log_sprint_id_at",
                schema: "work",
                table: "sprint_scope_log",
                columns: new[] { "sprint_id", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_sprints_team_starts_on",
                schema: "work",
                table: "sprints",
                columns: new[] { "team_id", "starts_on" });

            migrationBuilder.CreateIndex(
                name: "ix_sprints_team_state",
                schema: "work",
                table: "sprints",
                columns: new[] { "team_id", "state" });

            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS btree_gist;");
            migrationBuilder.Sql("CREATE UNIQUE INDEX ux_sprints_one_active_per_team ON work.sprints (team_id) WHERE state = 1;");
            migrationBuilder.Sql("ALTER TABLE work.sprints ADD CONSTRAINT ex_sprints_team_dates EXCLUDE USING gist (team_id WITH =, daterange(starts_on, ends_on, '[)') WITH &&);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sprint_scope_log",
                schema: "work");

            migrationBuilder.DropTable(
                name: "sprints",
                schema: "work");
        }
    }
}

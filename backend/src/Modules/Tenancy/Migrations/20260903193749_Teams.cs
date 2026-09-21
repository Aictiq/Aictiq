using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aictiq.Modules.Tenancy.Migrations
{
    /// <summary>
    /// Teams and their rosters, plus the case-insensitive name index EF cannot express.
    ///
    /// Note <c>ux_teams_default_per_project</c>: exactly one default team per project,
    /// guaranteed by a partial unique index rather than by the endpoint. Two admins
    /// promoting different teams at the same instant each read a database in which the
    /// other has not, so no check the application makes on its own would catch it.
    /// </summary>
    public partial class Teams : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "teams",
                schema: "tenancy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    key = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    sprint_length_days = table.Column<int>(type: "integer", nullable: false, defaultValue: 14),
                    working_days = table.Column<int[]>(type: "integer[]", nullable: false),
                    estimation_unit = table.Column<short>(type: "smallint", nullable: false),
                    time_zone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    is_default = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_teams", x => x.id);
                    table.CheckConstraint("ck_teams_key_format", "key ~ '^[A-Z][A-Z0-9]{1,9}$'");
                    table.CheckConstraint("ck_teams_name_not_blank", "length(btrim(name)) > 0");
                    table.CheckConstraint("ck_teams_sprint_length", "sprint_length_days BETWEEN 1 AND 28");
                    table.CheckConstraint("ck_teams_working_days", "array_length(working_days, 1) BETWEEN 1 AND 7 AND working_days <@ ARRAY[0,1,2,3,4,5,6]");
                    table.ForeignKey(
                        name: "fk_teams_projects_project_id",
                        column: x => x.project_id,
                        principalSchema: "tenancy",
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "team_members",
                schema: "tenancy",
                columns: table => new
                {
                    team_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    is_lead = table.Column<bool>(type: "boolean", nullable: false),
                    capacity_hours_per_day = table.Column<decimal>(type: "numeric(4,1)", precision: 4, scale: 1, nullable: true),
                    added_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_team_members", x => new { x.team_id, x.user_id });
                    table.CheckConstraint("ck_team_members_capacity", "capacity_hours_per_day IS NULL OR capacity_hours_per_day BETWEEN 0 AND 24");
                    table.ForeignKey(
                        name: "fk_team_members_teams_team_id",
                        column: x => x.team_id,
                        principalSchema: "tenancy",
                        principalTable: "teams",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_team_members_organization_id_user_id",
                schema: "tenancy",
                table: "team_members",
                columns: new[] { "organization_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_teams_organization_id_project_id",
                schema: "tenancy",
                table: "teams",
                columns: new[] { "organization_id", "project_id" });

            migrationBuilder.CreateIndex(
                name: "ix_teams_project_id_key",
                schema: "tenancy",
                table: "teams",
                columns: new[] { "project_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_teams_default_per_project",
                schema: "tenancy",
                table: "teams",
                column: "project_id",
                unique: true,
                filter: "is_default");

            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX ux_teams_project_id_name_lower
                ON tenancy.teams (project_id, lower(name));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS tenancy.ux_teams_project_id_name_lower;");

            migrationBuilder.DropTable(
                name: "team_members",
                schema: "tenancy");

            migrationBuilder.DropTable(
                name: "teams",
                schema: "tenancy");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aictiq.Modules.WorkItems.Migrations
{
    /// <inheritdoc />
    public partial class SprintCapacity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "sprint_capacity",
                schema: "work",
                columns: table => new
                {
                    sprint_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    hours_per_day = table.Column<decimal>(type: "numeric(4,1)", precision: 4, scale: 1, nullable: false),
                    days_off = table.Column<int>(type: "integer", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sprint_capacity", x => new { x.sprint_id, x.user_id });
                    table.CheckConstraint("ck_sprint_capacity_days_off", "days_off >= 0");
                    table.CheckConstraint("ck_sprint_capacity_hours_per_day", "hours_per_day BETWEEN 0 AND 24");
                    table.ForeignKey(
                        name: "fk_sprint_capacity_sprints_sprint_id",
                        column: x => x.sprint_id,
                        principalSchema: "work",
                        principalTable: "sprints",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_sprint_capacity_organization_id_user_id",
                schema: "work",
                table: "sprint_capacity",
                columns: new[] { "organization_id", "user_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sprint_capacity",
                schema: "work");
        }
    }
}

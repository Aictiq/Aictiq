using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aictiq.Modules.Identity.Migrations
{
    /// <inheritdoc />
    public partial class AddUserOnboarding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "user_onboarding",
                schema: "identity",
                columns: table => new
                {
                    user_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    tour_version = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    last_step_id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_onboarding", x => x.user_id);
                    table.CheckConstraint("ck_user_onboarding_completed_at", "(status = 'completed') = (completed_at IS NOT NULL)");
                    table.CheckConstraint("ck_user_onboarding_status", "status IN ('not_started', 'in_progress', 'deferred', 'dismissed', 'completed')");
                    table.CheckConstraint("ck_user_onboarding_tour_version", "tour_version >= 1");
                    table.ForeignKey(
                        name: "fk_user_onboarding_asp_net_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Release-day rollout: everyone with an account already got here without a
            // tour, so their preference starts dismissed - no popup interrupts established
            // users on upgrade, and any of them can replay from the account menu. Agents
            // get no row at all: a tour is for people.
            migrationBuilder.Sql("""
                INSERT INTO identity.user_onboarding (user_id, tour_version, status, updated_at)
                SELECT id, 1, 'dismissed', now()
                FROM identity."AspNetUsers"
                WHERE is_agent = FALSE
                ON CONFLICT (user_id) DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "user_onboarding",
                schema: "identity");
        }
    }
}

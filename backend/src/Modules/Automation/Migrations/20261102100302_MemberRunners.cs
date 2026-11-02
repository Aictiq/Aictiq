using System;
using Aictiq.SharedKernel.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aictiq.Modules.Automation.Migrations
{
    /// <inheritdoc />
    public partial class MemberRunners : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "member_runners",
                schema: "automation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    runner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assigned_by = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_member_runners", x => x.id);
                    table.ForeignKey(
                        name: "fk_member_runners_runners_runner_id",
                        column: x => x.runner_id,
                        principalSchema: "automation",
                        principalTable: "runners",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_member_runners_runner",
                schema: "automation",
                table: "member_runners",
                column: "runner_id");

            migrationBuilder.CreateIndex(
                name: "ux_member_runners_member",
                schema: "automation",
                table: "member_runners",
                columns: new[] { "project_id", "user_id" },
                unique: true);

            TenantRls.Enable(migrationBuilder, "automation", "member_runners");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            TenantRls.Disable(migrationBuilder, "automation", "member_runners");

            migrationBuilder.DropTable(
                name: "member_runners",
                schema: "automation");
        }
    }
}

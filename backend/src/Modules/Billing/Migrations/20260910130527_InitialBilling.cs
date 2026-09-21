using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Aictiq.Modules.Billing.Migrations
{
    /// <inheritdoc />
    public partial class InitialBilling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "billing");

            migrationBuilder.CreateTable(
                name: "plans",
                schema: "billing",
                columns: table => new
                {
                    code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    limits = table.Column<string>(type: "jsonb", nullable: false),
                    human_seat_price = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_plans", x => x.code);
                });

            migrationBuilder.CreateTable(
                name: "usage_snapshots",
                schema: "billing",
                columns: table => new
                {
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    humans = table.Column<int>(type: "integer", nullable: false),
                    agents = table.Column<int>(type: "integer", nullable: false),
                    projects = table.Column<int>(type: "integer", nullable: false),
                    storage_bytes = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_usage_snapshots", x => new { x.organization_id, x.day });
                });

            migrationBuilder.InsertData(
                schema: "billing",
                table: "plans",
                columns: new[] { "code", "human_seat_price", "limits" },
                values: new object[,]
                {
                    { "enterprise", 19m, "{\"SeatsHuman\":null,\"SeatsAgent\":null,\"Projects\":null,\"StorageBytes\":null,\"Features\":[\"webhooks\",\"page_permissions\",\"audit_export\",\"sso\",\"scim\"]}" },
                    { "free", 0m, "{\"SeatsHuman\":5,\"SeatsAgent\":1,\"Projects\":3,\"StorageBytes\":1073741824,\"Features\":null}" },
                    { "self_hosted", 0m, "{\"SeatsHuman\":null,\"SeatsAgent\":null,\"Projects\":null,\"StorageBytes\":null,\"Features\":[\"webhooks\",\"page_permissions\",\"audit_export\"]}" },
                    { "starter", 9m, "{\"SeatsHuman\":20,\"SeatsAgent\":5,\"Projects\":20,\"StorageBytes\":10737418240,\"Features\":null}" },
                    { "team", 15m, "{\"SeatsHuman\":null,\"SeatsAgent\":25,\"Projects\":null,\"StorageBytes\":107374182400,\"Features\":[\"webhooks\",\"page_permissions\",\"audit_export\"]}" }
                });

            migrationBuilder.CreateIndex(
                name: "ix_usage_snapshots_organization_id_day",
                schema: "billing",
                table: "usage_snapshots",
                columns: new[] { "organization_id", "day" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "plans",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "usage_snapshots",
                schema: "billing");
        }
    }
}

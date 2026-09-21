using System;
using Aictiq.SharedKernel.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aictiq.Modules.Billing.Migrations
{
    /// <inheritdoc />
    public partial class HostedOfferEvaluationAndFoundingPrice : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "founding_converted_at",
                schema: "billing",
                table: "subscriptions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "founding_periods",
                schema: "billing",
                table: "subscriptions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "founding_periods_billed",
                schema: "billing",
                table: "subscriptions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "founding_price",
                schema: "billing",
                table: "subscriptions",
                type: "numeric(10,2)",
                precision: 10,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "organization_price",
                schema: "billing",
                table: "plans",
                type: "numeric(10,2)",
                precision: 10,
                scale: 2,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "evaluations",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_evaluations", x => x.id);
                    table.CheckConstraint("ck_evaluations_window", "ends_at > started_at");
                });

            migrationBuilder.UpdateData(
                schema: "billing",
                table: "plans",
                keyColumn: "code",
                keyValue: "enterprise",
                columns: new[] { "limits", "organization_price" },
                values: new object[] { "{\"SeatsHuman\":null,\"SeatsAgent\":null,\"Projects\":null,\"StorageBytes\":null,\"Features\":[\"webhooks\",\"page_permissions\",\"audit_export\",\"sso\",\"scim\"],\"RunLogDays\":null,\"AnalyticsDays\":null}", 0m });

            migrationBuilder.UpdateData(
                schema: "billing",
                table: "plans",
                keyColumn: "code",
                keyValue: "free",
                columns: new[] { "limits", "organization_price" },
                values: new object[] { "{\"SeatsHuman\":5,\"SeatsAgent\":1,\"Projects\":3,\"StorageBytes\":1073741824,\"Features\":null,\"RunLogDays\":null,\"AnalyticsDays\":null}", 0m });

            migrationBuilder.UpdateData(
                schema: "billing",
                table: "plans",
                keyColumn: "code",
                keyValue: "self_hosted",
                columns: new[] { "limits", "organization_price" },
                values: new object[] { "{\"SeatsHuman\":null,\"SeatsAgent\":null,\"Projects\":null,\"StorageBytes\":null,\"Features\":[\"webhooks\",\"page_permissions\",\"audit_export\"],\"RunLogDays\":null,\"AnalyticsDays\":null}", 0m });

            migrationBuilder.UpdateData(
                schema: "billing",
                table: "plans",
                keyColumn: "code",
                keyValue: "starter",
                columns: new[] { "limits", "organization_price" },
                values: new object[] { "{\"SeatsHuman\":20,\"SeatsAgent\":5,\"Projects\":20,\"StorageBytes\":10737418240,\"Features\":null,\"RunLogDays\":null,\"AnalyticsDays\":null}", 0m });

            migrationBuilder.UpdateData(
                schema: "billing",
                table: "plans",
                keyColumn: "code",
                keyValue: "team",
                columns: new[] { "limits", "organization_price" },
                values: new object[] { "{\"SeatsHuman\":null,\"SeatsAgent\":25,\"Projects\":null,\"StorageBytes\":107374182400,\"Features\":[\"webhooks\",\"page_permissions\",\"audit_export\"],\"RunLogDays\":null,\"AnalyticsDays\":null}", 0m });

            migrationBuilder.InsertData(
                schema: "billing",
                table: "plans",
                columns: new[] { "code", "human_seat_price", "included_agents_per_human", "limits", "organization_price" },
                values: new object[] { "hosted", 0m, null, "{\"SeatsHuman\":null,\"SeatsAgent\":null,\"Projects\":null,\"StorageBytes\":10737418240,\"Features\":[\"webhooks\",\"page_permissions\",\"audit_export\"],\"RunLogDays\":90,\"AnalyticsDays\":365}", 49m });

            migrationBuilder.AddCheckConstraint(
                name: "ck_subscriptions_founding",
                schema: "billing",
                table: "subscriptions",
                sql: "(founding_price IS NULL) = (founding_periods IS NULL) AND founding_periods_billed >= 0 AND (founding_converted_at IS NULL OR founding_price IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "ux_evaluations_organization_id",
                schema: "billing",
                table: "evaluations",
                column: "organization_id",
                unique: true);

            // Every tenant table gets row-level security, not only the query filter: the
            // evaluation decides whether an organization may write at all, so a raw query
            // that forgot its tenant must find nothing rather than everything.
            TenantRls.Enable(migrationBuilder, "billing", "evaluations");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            TenantRls.Disable(migrationBuilder, "billing", "evaluations");

            migrationBuilder.DropTable(
                name: "evaluations",
                schema: "billing");

            migrationBuilder.DropCheckConstraint(
                name: "ck_subscriptions_founding",
                schema: "billing",
                table: "subscriptions");

            migrationBuilder.DeleteData(
                schema: "billing",
                table: "plans",
                keyColumn: "code",
                keyValue: "hosted");

            migrationBuilder.DropColumn(
                name: "founding_converted_at",
                schema: "billing",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "founding_periods",
                schema: "billing",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "founding_periods_billed",
                schema: "billing",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "founding_price",
                schema: "billing",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "organization_price",
                schema: "billing",
                table: "plans");

            migrationBuilder.UpdateData(
                schema: "billing",
                table: "plans",
                keyColumn: "code",
                keyValue: "enterprise",
                column: "limits",
                value: "{\"SeatsHuman\":null,\"SeatsAgent\":null,\"Projects\":null,\"StorageBytes\":null,\"Features\":[\"webhooks\",\"page_permissions\",\"audit_export\",\"sso\",\"scim\"]}");

            migrationBuilder.UpdateData(
                schema: "billing",
                table: "plans",
                keyColumn: "code",
                keyValue: "free",
                column: "limits",
                value: "{\"SeatsHuman\":5,\"SeatsAgent\":1,\"Projects\":3,\"StorageBytes\":1073741824,\"Features\":null}");

            migrationBuilder.UpdateData(
                schema: "billing",
                table: "plans",
                keyColumn: "code",
                keyValue: "self_hosted",
                column: "limits",
                value: "{\"SeatsHuman\":null,\"SeatsAgent\":null,\"Projects\":null,\"StorageBytes\":null,\"Features\":[\"webhooks\",\"page_permissions\",\"audit_export\"]}");

            migrationBuilder.UpdateData(
                schema: "billing",
                table: "plans",
                keyColumn: "code",
                keyValue: "starter",
                column: "limits",
                value: "{\"SeatsHuman\":20,\"SeatsAgent\":5,\"Projects\":20,\"StorageBytes\":10737418240,\"Features\":null}");

            migrationBuilder.UpdateData(
                schema: "billing",
                table: "plans",
                keyColumn: "code",
                keyValue: "team",
                column: "limits",
                value: "{\"SeatsHuman\":null,\"SeatsAgent\":25,\"Projects\":null,\"StorageBytes\":107374182400,\"Features\":[\"webhooks\",\"page_permissions\",\"audit_export\"]}");
        }
    }
}

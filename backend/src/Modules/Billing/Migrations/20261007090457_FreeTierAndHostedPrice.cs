using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aictiq.Modules.Billing.Migrations
{
    /// <inheritdoc />
    public partial class FreeTierAndHostedPrice : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                schema: "billing",
                table: "plans",
                keyColumn: "code",
                keyValue: "hosted",
                column: "organization_price",
                value: 79m);

            migrationBuilder.InsertData(
                schema: "billing",
                table: "plans",
                columns: new[] { "code", "human_seat_price", "included_agents_per_human", "limits", "organization_price" },
                values: new object[] { "hosted_free", 0m, null, "{\"SeatsHuman\":null,\"SeatsAgent\":null,\"Projects\":null,\"StorageBytes\":209715200,\"Features\":[\"webhooks\",\"page_permissions\",\"audit_export\"],\"RunLogDays\":30,\"AnalyticsDays\":365}", 0m });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                schema: "billing",
                table: "plans",
                keyColumn: "code",
                keyValue: "hosted_free");

            migrationBuilder.UpdateData(
                schema: "billing",
                table: "plans",
                keyColumn: "code",
                keyValue: "hosted",
                column: "organization_price",
                value: 49m);
        }
    }
}

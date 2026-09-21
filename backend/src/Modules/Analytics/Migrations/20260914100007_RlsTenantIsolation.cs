using Aictiq.Modules.Analytics;
using Aictiq.SharedKernel.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aictiq.Modules.Analytics.Migrations;

[DbContext(typeof(AnalyticsDbContext))]
[Migration("20260914100007_RlsTenantIsolation")]
public partial class RlsTenantIsolation : Migration
{
    private static readonly string[] Tables = ["item_transitions", "item_state_daily", "sprint_scope_log", "dashboards"];
    protected override void Up(MigrationBuilder migrationBuilder) => TenantRls.Enable(migrationBuilder, "analytics", Tables);
    protected override void Down(MigrationBuilder migrationBuilder) => TenantRls.Disable(migrationBuilder, "analytics", Tables);
}

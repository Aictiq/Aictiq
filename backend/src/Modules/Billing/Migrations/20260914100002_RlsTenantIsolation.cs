using Aictiq.Modules.Billing;
using Aictiq.SharedKernel.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aictiq.Modules.Billing.Migrations;

[DbContext(typeof(BillingDbContext))]
[Migration("20260914100002_RlsTenantIsolation")]
public partial class RlsTenantIsolation : Migration
{
    private static readonly string[] Tables = ["usage_snapshots", "subscriptions"];
    protected override void Up(MigrationBuilder migrationBuilder) => TenantRls.Enable(migrationBuilder, "billing", Tables);
    protected override void Down(MigrationBuilder migrationBuilder) => TenantRls.Disable(migrationBuilder, "billing", Tables);
}

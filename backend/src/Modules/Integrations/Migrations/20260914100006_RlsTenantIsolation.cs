using Aictiq.Modules.Integrations;
using Aictiq.SharedKernel.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aictiq.Modules.Integrations.Migrations;

[DbContext(typeof(IntegrationsDbContext))]
[Migration("20260914100006_RlsTenantIsolation")]
public partial class RlsTenantIsolation : Migration
{
    private static readonly string[] Tables = ["github_installations", "repo_bindings", "webhook_subscriptions", "webhook_deliveries"];
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        TenantRls.Enable(migrationBuilder, "integrations", Tables);
        // The webhook inbox is global until a signed GitHub delivery is resolved to an
        // installation; once assigned it is subject to the same tenant policy.
        TenantRls.EnableNullableTenant(migrationBuilder, "integrations", "github_deliveries");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        TenantRls.Disable(migrationBuilder, "integrations", Tables);
        TenantRls.Disable(migrationBuilder, "integrations", "github_deliveries");
    }
}

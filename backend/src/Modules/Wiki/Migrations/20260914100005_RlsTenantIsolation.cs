using Aictiq.Modules.Wiki;
using Aictiq.SharedKernel.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aictiq.Modules.Wiki.Migrations;

[DbContext(typeof(WikiDbContext))]
[Migration("20260914100005_RlsTenantIsolation")]
public partial class RlsTenantIsolation : Migration
{
    private static readonly string[] Tables = ["pages", "page_revisions", "page_item_links", "page_permissions"];
    protected override void Up(MigrationBuilder migrationBuilder) => TenantRls.Enable(migrationBuilder, "wiki", Tables);
    protected override void Down(MigrationBuilder migrationBuilder) => TenantRls.Disable(migrationBuilder, "wiki", Tables);
}

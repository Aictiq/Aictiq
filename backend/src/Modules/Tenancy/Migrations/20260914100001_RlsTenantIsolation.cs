using Aictiq.Modules.Tenancy;
using Aictiq.SharedKernel.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aictiq.Modules.Tenancy.Migrations;

[DbContext(typeof(TenancyDbContext))]
[Migration("20260914100001_RlsTenantIsolation")]
public partial class RlsTenantIsolation : Migration
{
    private static readonly string[] Tables = ["organization_members", "invitations", "projects", "project_members", "teams", "team_members"];
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        TenantRls.Enable(migrationBuilder, "tenancy", Tables);

        // These are the three deliberate pre-tenant reads in the authorization model.
        // They remain constrained to the authenticated caller or the exact opaque
        // invitation capability; IgnoreQueryFilters must not turn into a tenant scan.
        migrationBuilder.Sql("""
            CREATE POLICY aictiq_membership_self ON tenancy.organization_members
                FOR SELECT TO aictiq_app
                USING (user_id = NULLIF(current_setting('app.user_id', true), ''));
            CREATE POLICY aictiq_project_membership_self ON tenancy.project_members
                FOR SELECT TO aictiq_app
                USING (user_id = NULLIF(current_setting('app.user_id', true), ''));
            CREATE POLICY aictiq_visible_project_lookup ON tenancy.projects
                FOR SELECT TO aictiq_app
                USING (EXISTS (
                    SELECT 1 FROM tenancy.organization_members membership
                    WHERE membership.organization_id = projects.organization_id
                      AND membership.user_id = NULLIF(current_setting('app.user_id', true), '')
                ));
            CREATE POLICY aictiq_invitation_token_lookup ON tenancy.invitations
                FOR SELECT TO aictiq_app
                USING (token_hash = decode(NULLIF(current_setting('app.invitation_token_hash', true), ''), 'hex'));
            """);
    }
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP POLICY IF EXISTS aictiq_membership_self ON tenancy.organization_members;
            DROP POLICY IF EXISTS aictiq_project_membership_self ON tenancy.project_members;
            DROP POLICY IF EXISTS aictiq_visible_project_lookup ON tenancy.projects;
            DROP POLICY IF EXISTS aictiq_invitation_token_lookup ON tenancy.invitations;
            """);
        TenantRls.Disable(migrationBuilder, "tenancy", Tables);
    }
}

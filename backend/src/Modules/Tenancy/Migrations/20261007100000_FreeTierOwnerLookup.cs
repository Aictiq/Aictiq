using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aictiq.Modules.Tenancy.Migrations;

/// <summary>
/// The hosted free tier's limits belong to a person, across every organization they own,
/// and the Owner in question is often not the caller (an invitee accepting, an admin
/// inviting). <c>app.owner_lookup</c> is a capability set only by
/// <c>TenancyPeopleSource</c> for the length of one read: it admits that user's Owner
/// memberships and nothing else - no other role, no other person, no other table.
/// </summary>
[DbContext(typeof(TenancyDbContext))]
[Migration("20261007100000_FreeTierOwnerLookup")]
public partial class FreeTierOwnerLookup : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE POLICY aictiq_owner_lookup ON tenancy.organization_members
                FOR SELECT TO aictiq_app
                USING (role = 0 AND user_id = NULLIF(current_setting('app.owner_lookup', true), ''));
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP POLICY IF EXISTS aictiq_owner_lookup ON tenancy.organization_members;");
    }
}

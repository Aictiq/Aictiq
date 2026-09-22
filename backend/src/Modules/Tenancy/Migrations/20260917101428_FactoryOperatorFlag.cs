using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aictiq.Modules.Tenancy.Migrations
{
    /// <inheritdoc />
    public partial class FactoryOperatorFlag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "can_operate_factory",
                schema: "tenancy",
                table: "organization_members",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "can_operate_factory",
                schema: "tenancy",
                table: "invitations",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            // Every existing row defaulted to true, Guests included; they have to be cleared
            // before the constraints can hold. Owners and Admins keep true, which the rules
            // ignore anyway, and existing Members keep operating: nothing changes for anyone
            // until an administrator says so.
            //
            // Both tables FORCE row-level security and their policies name aictiq_app only, so
            // a migrator that owns the tables without being a superuser (the external-Postgres
            // deployment in docs/self-host.md) would see no rows: the UPDATE would do nothing
            // and the constraint below would then fail on the first Guest. The force is lifted
            // for exactly this statement, inside the migration's transaction, and restored.
            migrationBuilder.Sql("""
                ALTER TABLE tenancy.organization_members NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE tenancy.invitations NO FORCE ROW LEVEL SECURITY;
                UPDATE tenancy.organization_members SET can_operate_factory = false WHERE role = 3;
                UPDATE tenancy.invitations SET can_operate_factory = false WHERE role = 3;
                ALTER TABLE tenancy.organization_members FORCE ROW LEVEL SECURITY;
                ALTER TABLE tenancy.invitations FORCE ROW LEVEL SECURITY;
                """);

            migrationBuilder.AddCheckConstraint(
                name: "ck_organization_members_guest_not_operator",
                schema: "tenancy",
                table: "organization_members",
                sql: "role <> 3 OR NOT can_operate_factory");

            migrationBuilder.AddCheckConstraint(
                name: "ck_invitations_guest_not_operator",
                schema: "tenancy",
                table: "invitations",
                sql: "role <> 3 OR NOT can_operate_factory");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_organization_members_guest_not_operator",
                schema: "tenancy",
                table: "organization_members");

            migrationBuilder.DropCheckConstraint(
                name: "ck_invitations_guest_not_operator",
                schema: "tenancy",
                table: "invitations");

            migrationBuilder.DropColumn(
                name: "can_operate_factory",
                schema: "tenancy",
                table: "organization_members");

            migrationBuilder.DropColumn(
                name: "can_operate_factory",
                schema: "tenancy",
                table: "invitations");
        }
    }
}

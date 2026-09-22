using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aictiq.Modules.Tenancy.Migrations
{
    /// <summary>
    /// An organization always has at least one Owner, guaranteed where it has to be.
    ///
    /// The endpoint can refuse the obvious cases, but not the one that matters: two Owners
    /// demoting each other at the same instant each read a database in which the other is
    /// still an Owner, both pass, and the organization is left with none. So the check
    /// runs after the write, inside the same transaction, behind a lock on the
    /// organization row - which turns the interleaving into a queue and lets the second
    /// transaction see what the first actually did.
    /// </summary>
    public partial class OrganizationOwnerGuard : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE FUNCTION tenancy.ensure_org_has_owner() RETURNS trigger AS $$
                BEGIN
                    -- Serializes membership changes within one organization. Without it two
                    -- transactions demoting different Owners would each still see the other,
                    -- and both would commit; with it the second one waits and then reads the
                    -- first one's committed work.
                    PERFORM 1 FROM tenancy.organizations
                    WHERE id = OLD.organization_id
                    FOR UPDATE;

                    -- The organization itself is gone: this is a hard purge cascading into
                    -- the membership table, and there is nothing left to own. Refusing here
                    -- would make an organization undeletable.
                    IF NOT FOUND THEN
                        RETURN NULL;
                    END IF;

                    IF NOT EXISTS (
                        SELECT 1 FROM tenancy.organization_members
                        WHERE organization_id = OLD.organization_id
                          AND role = 0
                    ) THEN
                        -- SQLSTATE P0001 plus this exact text; the API matches on both
                        -- (SharedKernel DatabaseSignals.LastOwner) to answer 409 last-owner.
                        RAISE EXCEPTION 'last_owner' USING ERRCODE = 'P0001';
                    END IF;

                    RETURN NULL;
                END;
                $$ LANGUAGE plpgsql;

                COMMENT ON FUNCTION tenancy.ensure_org_has_owner() IS
                    'Refuses any UPDATE or DELETE of tenancy.organization_members that would leave an organization without an Owner (role 0).';

                -- Two triggers rather than one: a WHEN clause may not mention NEW on a
                -- DELETE, and an UPDATE that does not touch the role cannot remove an Owner,
                -- so it should not take the organization lock either.
                CREATE TRIGGER trg_organization_members_owner_guard_update
                AFTER UPDATE ON tenancy.organization_members
                FOR EACH ROW
                WHEN (OLD.role IS DISTINCT FROM NEW.role)
                EXECUTE FUNCTION tenancy.ensure_org_has_owner();

                CREATE TRIGGER trg_organization_members_owner_guard_delete
                AFTER DELETE ON tenancy.organization_members
                FOR EACH ROW
                EXECUTE FUNCTION tenancy.ensure_org_has_owner();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_organization_members_owner_guard_delete ON tenancy.organization_members;
                DROP TRIGGER IF EXISTS trg_organization_members_owner_guard_update ON tenancy.organization_members;
                DROP FUNCTION IF EXISTS tenancy.ensure_org_has_owner();
                """);
        }
    }
}

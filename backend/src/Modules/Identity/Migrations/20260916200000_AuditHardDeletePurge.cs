using Aictiq.Modules.Identity;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aictiq.Modules.Identity.Migrations;

/// <summary>
/// Items, projects and organizations are deleted for good, and an audit row carries the
/// values a record had — so their audit trail has to be deletable too. Two more narrow ways
/// past the append-only trigger, in the shape of shared.purge_audit_log: each holds the
/// transaction-local flag for exactly its own DELETE, UPDATE stays impossible under any flag,
/// and a bare DELETE still raises.
///
/// Under RLS the application role may delete only its own organization's rows, which is the
/// scope both functions are called in.
/// </summary>
[DbContext(typeof(IdentityDbContext))]
[Migration("20260916200000_AuditHardDeletePurge")]
public partial class AuditHardDeletePurge : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE FUNCTION shared.purge_audit_entities(org uuid, kind text, ids text[])
            RETURNS bigint AS $$
            DECLARE
                deleted bigint;
            BEGIN
                IF org IS NULL OR ids IS NULL OR cardinality(ids) = 0 THEN
                    RETURN 0;
                END IF;

                SET LOCAL app.audit_retention = 'on';

                -- A composite key is stored as its parts joined by '/', so an id also
                -- matches the keys it leads: {teamId}/{userId} goes with its team.
                DELETE FROM audit.audit_log
                WHERE (organization_id = org OR organization_id IS NULL)
                  AND entity_type = kind
                  AND (entity_id = ANY(ids) OR split_part(entity_id, '/', 1) = ANY(ids));

                GET DIAGNOSTICS deleted = ROW_COUNT;

                SET LOCAL app.audit_retention = 'off';
                RETURN deleted;
            END;
            $$ LANGUAGE plpgsql;

            CREATE FUNCTION shared.purge_audit_organization(org uuid)
            RETURNS bigint AS $$
            DECLARE
                deleted bigint;
            BEGIN
                IF org IS NULL THEN
                    RETURN 0;
                END IF;

                SET LOCAL app.audit_retention = 'on';

                -- Rows written before the log carried an organization still name it as
                -- the entity they are about.
                DELETE FROM audit.audit_log
                WHERE organization_id = org
                   OR (organization_id IS NULL AND entity_type = 'Organization' AND entity_id = org::text);

                GET DIAGNOSTICS deleted = ROW_COUNT;

                SET LOCAL app.audit_retention = 'off';
                RETURN deleted;
            END;
            $$ LANGUAGE plpgsql;

            DROP POLICY IF EXISTS aictiq_tenant_delete ON audit.audit_log;
            CREATE POLICY aictiq_tenant_delete ON audit.audit_log
                FOR DELETE TO aictiq_app
                USING (organization_id = NULLIF(current_setting('app.org_id', true), '')::uuid);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP POLICY IF EXISTS aictiq_tenant_delete ON audit.audit_log;
            DROP FUNCTION IF EXISTS shared.purge_audit_organization(uuid);
            DROP FUNCTION IF EXISTS shared.purge_audit_entities(uuid, text, text[]);
            """);
    }
}

using Aictiq.Modules.Identity;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aictiq.Modules.Identity.Migrations;

[DbContext(typeof(IdentityDbContext))]
[Migration("20260914100000_RlsRoles")]
public partial class RlsRoles : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Login/password management is deployment bootstrap (Compose or the external
        // Postgres guide). These fallback roles are NOLOGIN; deployment bootstrap turns
        // them into the distinct login principals before this migration runs.
        migrationBuilder.Sql("""
            -- Roles are cluster-wide, so a check-then-create races any other database
            -- migrating at the same moment: both see the role missing, both create it,
            -- and one loses. Catching is the only form that is actually atomic. Both
            -- codes are needed - CREATE ROLE's own pre-check raises duplicate_object
            -- (42710), and a race that slips past it is caught by the unique index on
            -- pg_authid.rolname as unique_violation (23505). The test suite migrates
            -- dozens of databases in parallel and hits the second one constantly; so
            -- would two deployments bootstrapping against one cluster.
            DO $$
            BEGIN
                CREATE ROLE aictiq_admin NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT;
            EXCEPTION WHEN duplicate_object OR unique_violation THEN
                NULL;
            END $$;
            DO $$
            BEGIN
                CREATE ROLE aictiq_app NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT;
            EXCEPTION WHEN duplicate_object OR unique_violation THEN
                NULL;
            END $$;
            GRANT USAGE ON SCHEMA identity, audit, shared TO aictiq_app;
            GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA identity, audit, shared TO aictiq_app;
            GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA identity, audit, shared TO aictiq_app;
            ALTER DEFAULT PRIVILEGES IN SCHEMA identity GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO aictiq_app;
            ALTER DEFAULT PRIVILEGES IN SCHEMA audit GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO aictiq_app;
            ALTER DEFAULT PRIVILEGES IN SCHEMA shared GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO aictiq_app;
            ALTER DEFAULT PRIVILEGES IN SCHEMA identity GRANT USAGE, SELECT ON SEQUENCES TO aictiq_app;
            ALTER DEFAULT PRIVILEGES IN SCHEMA audit GRANT USAGE, SELECT ON SEQUENCES TO aictiq_app;
            ALTER DEFAULT PRIVILEGES IN SCHEMA shared GRANT USAGE, SELECT ON SEQUENCES TO aictiq_app;

            ALTER TABLE audit.audit_log ENABLE ROW LEVEL SECURITY;
            ALTER TABLE audit.audit_log FORCE ROW LEVEL SECURITY;
            DROP POLICY IF EXISTS aictiq_tenant_isolation ON audit.audit_log;
            CREATE POLICY aictiq_tenant_isolation ON audit.audit_log
                FOR SELECT TO aictiq_app
                USING (organization_id = NULLIF(current_setting('app.org_id', true), '')::uuid);
            CREATE POLICY aictiq_tenant_insert ON audit.audit_log
                FOR INSERT TO aictiq_app
                WITH CHECK (organization_id = NULLIF(current_setting('app.org_id', true), '')::uuid OR organization_id IS NULL);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Roles can be shared with another Aictiq database; never drop principals from a
        // schema migration. Revoking grants would also make rolling back one database
        // affect another, so down is intentionally a no-op.
    }
}

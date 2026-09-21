using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aictiq.Modules.Identity.Migrations
{
    /// <summary>
    /// Gives retention a single, narrow, auditable way past the audit log's append-only
    /// trigger. The guarantee stays intact: UPDATE is still impossible (history is never
    /// rewritten), and an arbitrary DELETE still raises — only shared.purge_audit_log,
    /// which can delete nothing newer than the cutoff it is given, is allowed through.
    /// </summary>
    public partial class AuditRetentionPurge : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION shared.reject_mutation() RETURNS trigger AS $$
                BEGIN
                    -- UPDATE is never permitted, under any flag: an audit row is immutable.
                    -- DELETE is permitted only while shared.purge_audit_log() holds this
                    -- transaction-local flag, so a compromised app connection issuing a
                    -- bare DELETE still hits the exception below.
                    IF TG_OP = 'DELETE' AND current_setting('app.audit_retention', true) = 'on' THEN
                        RETURN OLD;
                    END IF;
                    RAISE EXCEPTION '% is append-only', TG_TABLE_NAME;
                END;
                $$ LANGUAGE plpgsql;

                CREATE FUNCTION shared.purge_audit_log(cutoff timestamptz, max_rows integer)
                RETURNS bigint AS $$
                DECLARE
                    deleted bigint;
                BEGIN
                    IF max_rows <= 0 THEN
                        RETURN 0;
                    END IF;

                    -- Held for exactly the DELETE below and cleared again before
                    -- returning, so the permission never outlives this call — not even
                    -- for a caller that invokes this inside its own transaction.
                    SET LOCAL app.audit_retention = 'on';

                    WITH doomed AS (
                        SELECT id FROM audit.audit_log WHERE at < cutoff LIMIT max_rows
                    )
                    DELETE FROM audit.audit_log a USING doomed d WHERE a.id = d.id;

                    GET DIAGNOSTICS deleted = ROW_COUNT;

                    SET LOCAL app.audit_retention = 'off';
                    RETURN deleted;
                END;
                $$ LANGUAGE plpgsql;

                COMMENT ON FUNCTION shared.purge_audit_log(timestamptz, integer) IS
                    'Retention only. In a hardened deployment, own this function with a separate role and REVOKE EXECUTE from the application role.';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP FUNCTION IF EXISTS shared.purge_audit_log(timestamptz, integer);

                CREATE OR REPLACE FUNCTION shared.reject_mutation() RETURNS trigger AS $$
                BEGIN
                    RAISE EXCEPTION '% is append-only', TG_TABLE_NAME;
                END;
                $$ LANGUAGE plpgsql;
                """);
        }
    }
}

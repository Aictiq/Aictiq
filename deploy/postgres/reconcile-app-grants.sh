#!/bin/sh
# Reapply the runtime role's schema/table privileges after a restore made with
# pg_restore --no-privileges. It is idempotent and intentionally does not grant DDL,
# ownership, superuser or BYPASSRLS capabilities.
set -eu

psql --set=ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<'SQL'
DO $$
DECLARE
    schema_name text;
BEGIN
    FOREACH schema_name IN ARRAY ARRAY[
        'identity', 'tenancy', 'work', 'notify', 'wiki', 'analytics', 'integrations',
        'billing', 'audit', 'shared'
    ]
    LOOP
        IF EXISTS (SELECT 1 FROM pg_namespace WHERE nspname = schema_name) THEN
            EXECUTE format('GRANT USAGE ON SCHEMA %I TO aictiq_app', schema_name);
            EXECUTE format(
                'GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA %I TO aictiq_app',
                schema_name);
            EXECUTE format(
                'GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA %I TO aictiq_app',
                schema_name);
        END IF;
    END LOOP;
END
$$;
SQL

-- Executed by the Aspire-only postgres-roles sidecar on every local start. The
-- generated Aspire user remains the database owner for migrations; application
-- connections use this role and are therefore subject to tenant RLS policies.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'aictiq_app') THEN
        CREATE ROLE aictiq_app LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT;
    END IF;
END
$$;

ALTER ROLE aictiq_app LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT PASSWORD :'app_password';

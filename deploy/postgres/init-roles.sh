#!/bin/sh
# Runs only while PostgreSQL initializes a new pgdata volume. The initial PostgreSQL
# role is the migration/admin login; this creates the distinct request-time login that
# RLS policies grant to. Keep it here rather than in an EF migration: migrations run as
# the admin and must never need the application's password.
set -eu

psql --set=ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" \
  --set=app_password="$POSTGRES_APP_PASSWORD" <<'SQL'
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'aictiq_app') THEN
        CREATE ROLE aictiq_app LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT;
    END IF;
END
$$;
ALTER ROLE aictiq_app LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT PASSWORD :'app_password';
SQL

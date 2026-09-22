#!/usr/bin/env bash
set -euo pipefail

backup_dir="${1:?usage: ./restore.sh BACKUP_DIRECTORY}"
test -f "$backup_dir/postgres.dump" || { echo "postgres.dump not found in $backup_dir" >&2; exit 2; }
test -d "$backup_dir/objects" || { echo "objects directory not found in $backup_dir" >&2; exit 2; }
compose=(docker compose)

echo "This replaces the compose database and bucket contents with $backup_dir."
read -r -p "Type RESTORE to continue: " confirmation
test "$confirmation" = RESTORE || { echo "Restore cancelled."; exit 1; }
"${compose[@]}" stop api workers
"${compose[@]}" exec -T postgres sh -ec 'dropdb -U "$POSTGRES_USER" --if-exists "$POSTGRES_DB"; createdb -U "$POSTGRES_USER" "$POSTGRES_DB"'
"${compose[@]}" exec -T postgres pg_restore -U aictiq_admin -d aictiq --no-owner --no-privileges < "$backup_dir/postgres.dump"
# Privileges are intentionally excluded from the dump. Regrant only the app role's
# runtime DML capabilities; its RLS policies remain database-owned and cannot be bypassed.
"${compose[@]}" exec -T postgres sh /docker-entrypoint-initdb.d/reconcile-app-grants.sh
"${compose[@]}" run --rm --no-deps -v "$(cd "$backup_dir/objects" && pwd):/backup:ro" \
  aws-cli s3 sync /backup "s3://${S3_BUCKET:-aictiq}" --delete --endpoint-url http://garage:3900
"${compose[@]}" start api workers
echo "Restore complete. The API applies forward migrations at startup; downgrade is unsupported."

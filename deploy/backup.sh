#!/usr/bin/env bash
set -euo pipefail

# A portable backup is a Postgres custom-format dump plus an S3-level copy. Never copy
# Garage's data volume: its internal layout is not a backup interface.
backup_dir="${1:-./backups/$(date -u +%Y%m%dT%H%M%SZ)}"
mkdir -p "$backup_dir/objects"
compose=(docker compose)

echo "Stopping writers for a consistent backup window..."
"${compose[@]}" stop api workers >/dev/null
trap '"${compose[@]}" start api workers >/dev/null' EXIT
"${compose[@]}" exec -T postgres pg_dump -U aictiq_admin -Fc aictiq > "$backup_dir/postgres.dump"
# aws-cli joins the compose network and talks to Garage over its internal endpoint.
"${compose[@]}" run --rm --no-deps -v "$(cd "$backup_dir" && pwd)/objects:/backup" \
  aws-cli s3 sync "s3://${S3_BUCKET:-aictiq}" /backup --endpoint-url http://garage:3900
printf 'created_at=%s\n' "$(date -u +%FT%TZ)" > "$backup_dir/manifest"
echo "Backup written to $backup_dir"

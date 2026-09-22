#!/bin/sh
# Brings a fresh Garage node to the state Aictiq needs: a layout (without one the S3 API
# refuses everything), a bucket, and a key allowed to read and write it.
#
# Every step is guarded by a read, so re-running this is a no-op - Garage answers 409 to
# a repeated create and 500 to a repeated layout apply, and a container that restarts
# must not fail on either.
#
# Talks the admin HTTP API rather than the `garage` CLI because the Garage image has no
# shell, so a sidecar cannot chain CLI calls. The same script runs under Aspire and
# docker compose.
#
# Required: GARAGE_ADMIN_URL, GARAGE_ADMIN_TOKEN, GARAGE_BUCKET,
#           GARAGE_ACCESS_KEY, GARAGE_SECRET_KEY, GARAGE_CAPACITY_BYTES, GARAGE_ZONE

set -eu

AUTH="Authorization: Bearer ${GARAGE_ADMIN_TOKEN}"
JSON="Content-Type: application/json"
ZONE="${GARAGE_ZONE:-dc1}"
CAPACITY="${GARAGE_CAPACITY_BYTES:-10737418240}"

log() { echo "garage-init: $*"; }

api() {
  method=$1
  path=$2
  shift 2
  curl -sS -X "$method" -H "$AUTH" -H "$JSON" "$@" "${GARAGE_ADMIN_URL}${path}"
}

status_of() {
  method=$1
  path=$2
  shift 2
  curl -sS -o /dev/null -w '%{http_code}' -X "$method" -H "$AUTH" -H "$JSON" "$@" \
    "${GARAGE_ADMIN_URL}${path}"
}

# ---------------------------------------------------------------- wait for the node
attempt=0
until api GET /v2/GetClusterStatus >/dev/null 2>&1; do
  attempt=$((attempt + 1))
  if [ "$attempt" -ge 60 ]; then
    log "admin API never answered at ${GARAGE_ADMIN_URL}"
    exit 1
  fi
  sleep 1
done
log "admin API is up"

status=$(api GET /v2/GetClusterStatus)

# ---------------------------------------------------------------- layout
# layoutVersion 0 means no layout has ever been applied. Anything higher was set by a
# previous run (or by an operator) and must be left alone.
layout_version=$(echo "$status" | sed -n 's/.*"layoutVersion"[[:space:]]*:[[:space:]]*\([0-9]*\).*/\1/p' | head -1)

if [ "${layout_version:-0}" = "0" ]; then
  # The only 64-hex-character values in this payload are node ids.
  node=$(echo "$status" | grep -o '"id"[[:space:]]*:[[:space:]]*"[0-9a-f]\{64\}"' | head -1 |
    grep -o '[0-9a-f]\{64\}')
  if [ -z "$node" ]; then
    log "could not read the node id from GetClusterStatus"
    exit 1
  fi

  log "assigning layout to node ${node}"
  api POST /v2/UpdateClusterLayout \
    -d "{\"roles\":[{\"id\":\"${node}\",\"zone\":\"${ZONE}\",\"capacity\":${CAPACITY},\"tags\":[]}]}" \
    >/dev/null
  api POST /v2/ApplyClusterLayout -d '{"version":1}' >/dev/null
  log "layout applied"
else
  log "layout version ${layout_version} already applied"
fi

# ---------------------------------------------------------------- key
if [ "$(status_of GET "/v2/GetKeyInfo?id=${GARAGE_ACCESS_KEY}")" = "200" ]; then
  log "key ${GARAGE_ACCESS_KEY} already exists"
else
  log "importing key ${GARAGE_ACCESS_KEY}"
  # Imported rather than generated so the credentials are known up front and can be
  # handed to the API and Workers as configuration.
  api POST /v2/ImportKey \
    -d "{\"accessKeyId\":\"${GARAGE_ACCESS_KEY}\",\"secretAccessKey\":\"${GARAGE_SECRET_KEY}\",\"name\":\"${GARAGE_BUCKET}\"}" \
    >/dev/null
fi

# ---------------------------------------------------------------- bucket
if [ "$(status_of GET "/v2/GetBucketInfo?globalAlias=${GARAGE_BUCKET}")" = "200" ]; then
  log "bucket ${GARAGE_BUCKET} already exists"
else
  log "creating bucket ${GARAGE_BUCKET}"
  api POST /v2/CreateBucket -d "{\"globalAlias\":\"${GARAGE_BUCKET}\"}" >/dev/null
fi

bucket_id=$(api GET "/v2/GetBucketInfo?globalAlias=${GARAGE_BUCKET}" |
  grep -o '"id"[[:space:]]*:[[:space:]]*"[0-9a-f]\{64\}"' | head -1 |
  grep -o '[0-9a-f]\{64\}')
if [ -z "$bucket_id" ]; then
  log "could not read the id of bucket ${GARAGE_BUCKET}"
  exit 1
fi

# Granting an already-granted permission is accepted, so this needs no guard.
log "granting read/write on ${GARAGE_BUCKET} to ${GARAGE_ACCESS_KEY}"
api POST /v2/AllowBucketKey \
  -d "{\"bucketId\":\"${bucket_id}\",\"accessKeyId\":\"${GARAGE_ACCESS_KEY}\",\"permissions\":{\"read\":true,\"write\":true,\"owner\":false}}" \
  >/dev/null

log "ready"

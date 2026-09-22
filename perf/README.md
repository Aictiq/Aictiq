# Performance

k6 load-test scripts for the compose stack, run nightly by
[`.github/workflows/perf.yml`](../.github/workflows/perf.yml). They measure the endpoints
a busy org hammers - list, board, search, detail and the MCP tool an agent loops on -
against the seeded perf organization.

## Targets

The product's non-functional budget:

| Script | Endpoint | Target |
| --- | --- | --- |
| `list.js` | `GET /api/v1/orgs/perf/projects/{key}/items/` | p95 < 200 ms (p99 < 500 ms) |
| `board.js` | `GET /api/v1/orgs/perf/teams/{teamId}/board` | p95 < 200 ms |
| `search.js` | `GET /api/v1/orgs/perf/search?q=…` | p95 < 300 ms |
| `detail.js` | `GET /api/v1/orgs/perf/items/{KEY}-{n}` | p95 < 200 ms |
| `mcp.js` | `POST /mcp` → `tools/call list_ready_work` | p95 < 300 ms |

Every script also fails if more than 1% of requests error or more than 1% of checks fail -
a 429 storm makes latency look wonderful while serving nothing.

## Running locally

```bash
# 1. The stack, from source
cd deploy
./build-local.sh
cat > .env <<EOF         # or cp .env.example .env and fill in real secrets
AICTIQ_URL=http://localhost:8080
HTTP_PORT=8080
HTTPS_PORT=8443
JWT_KEY=$(openssl rand -base64 48)
POSTGRES_PASSWORD=$(openssl rand -hex 24)
GARAGE_RPC_SECRET=$(openssl rand -hex 32)
GARAGE_ADMIN_TOKEN=$(openssl rand -hex 32)
S3_ACCESS_KEY=GK$(openssl rand -hex 12)
S3_SECRET_KEY=$(openssl rand -hex 32)
SEED_ADMIN_EMAIL=admin@example.com
SEED_ADMIN_PASSWORD=admin-password-at-least-12
AICTIQ_IMAGE_REGISTRY=aictiq-local
AICTIQ_IMAGE_TAG=local
EOF
docker compose up -d --wait --wait-timeout 300
curl -fsS http://localhost:8080/health/ready

# 2. The perf organization: 20 projects P01..P20, 5000 items each, 3 sprints and one
#    default team per project (~100k items - takes a while, and logs progress)
docker compose run --rm api aictiq-seed-perf

# 3. k6 - see https://grafana.com/docs/k6/latest/set-up/install-k6/
cd ..
mkdir -p perf/results
BASE_URL=http://localhost:8080 \
ADMIN_EMAIL=admin@example.com \
ADMIN_PASSWORD=admin-password-at-least-12 \
k6 run perf/list.js
```

Each run writes `perf/results/<name>-summary.json` (the full end-of-test JSON, uploaded
as a CI artifact by the nightly) plus a short table to stdout. The directory must exist -
k6 cannot create it.

## What each script measures

- **`list.js`** - the backlog/list page at `pageSize=100`: 40% unfiltered, then the filter
  grammar the SPA issues (`state:proposed,active`, `sprint:current`, a label,
  `type:bug priority:high&sort=updated`), across the 20 seeded projects. 20 arrivals/s.
- **`board.js`** - the kanban board, the heaviest page in the app: every team card comes
  back grouped by column. 5 arrivals/s (megabyte-scale payloads; a small runner would
  otherwise end up measuring itself). Teams are discovered in `setup()` via
  `GET /orgs/perf/projects/{key}/teams/` - the same call the SPA's board page makes.
- **`search.js`** - org-wide multi-word search (the expensive route: it checks project
  visibility before ranking), mixed with `types=items` and per-project variants. 10/s.
- **`detail.js`** - single item by key (`P07-1234`), rotating over the whole seeded space
  so the buffer cache cannot carry the run. 10/s.
- **`mcp.js`** - the MCP handshake once in `setup()` (`initialize` → `initialized` → one
  validating `tools/call`), then `list_ready_work` per iteration with a PAT bound to the
  perf org. 1/s for 50s: the shipped stack rate-limits MCP tool calls to 60/min per token
  (`RateLimiting:McpPermitLimitPerMinute`), and the script should measure the tool, not
  the limiter.

## Knobs

| Env | Default | Meaning |
| --- | --- | --- |
| `BASE_URL` | `http://localhost:8080` | The stack via Caddy |
| `ORG_SLUG` | `perf` | The seeded organization |
| `ADMIN_EMAIL` / `ADMIN_PASSWORD` | `admin@example.com` / required | The seeded admin (`Seed:AdminEmail` / `Seed:AdminPassword`) |
| `RESULTS_DIR` | `perf/results` | Where summaries land (must exist) |
| `LIST_RATE` / `BOARD_RATE` / `SEARCH_RATE` / `DETAIL_RATE` / `MCP_RATE` | 20 / 5 / 10 / 10 / 1 | Arrival rate per second |
| `MCP_DURATION` | `50s` | How long the MCP script runs |

## pg_stat_statements

The compose Postgres preloads `pg_stat_statements`
(`deploy/docker-compose.yml`) and creates the extension on first init of a fresh volume
(`deploy/postgres/init-statements.sql`). The nightly resets it before the runs and dumps
the top-10 queries by `total_exec_time` afterwards, next to the k6 summaries. Locally:

```bash
docker compose -f deploy/docker-compose.yml exec postgres \
  psql -U aictiq -d aictiq -c "SELECT calls, round(total_exec_time::numeric,1) AS total_ms, \
  round(mean_exec_time::numeric,2) AS mean_ms, left(query, 120) FROM pg_stat_statements \
  ORDER BY total_exec_time DESC LIMIT 10"
```

## Interpreting a nightly failure

The workflow fails when a threshold fails (k6 exits non-zero) or the seeding fails.
Artifacts (`perf-results`, kept 14 days) hold each script's summary JSON, the seed log
and the query dump - compare p95 against the table above, then look for the query that
regressed in the dump before reaching for an index.

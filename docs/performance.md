# Performance

The product budget is **p95 < 200 ms** for list/board endpoints at 100k items per org,
and **search < 300 ms**. This page gives the current numbers and what keeps them there.

## Baseline (September 2026)

Measured locally against the compose stack seeded with the perf organization:

| Script | Load | p95 | Target |
| --- | --- | --- | --- |
| `list.js` | 20 req/s | 15 ms | < 200 ms |
| `board.js` | 5 req/s | 77 ms | < 200 ms |
| `search.js` | 10 req/s | 20 ms | < 300 ms |
| `detail.js` | 10 req/s | 6 ms | < 200 ms |
| `mcp.js` (`list_ready_work`) | 1 req/s | 50 ms | < 300 ms |

## What guards the budget

- **Nightly k6 run against compose** - `perf/*.js`, driven by
  `.github/workflows/perf.yml` (cron, plus `workflow_dispatch`). It builds the branch,
  boots the deploy stack, seeds the perf organization, runs five load scripts whose
  thresholds *are* the budget, and uploads the summaries plus a `pg_stat_statements`
  top-10 dump as the `perf-results` artifact. A failed threshold fails the job. See
  `perf/README.md` for running it locally.
- **The perf dataset** - `dotnet run --project backend/src/Api -- aictiq-seed-perf`
  (or `docker compose run --rm api aictiq-seed-perf`) seeds an organization `perf`
  with 20 projects × 5,000 items (100k), 400k history rows, 50k comments, sprints,
  labels and one default team per project, in about 2½ minutes. Deterministic; skips
  if the organization already exists.
- **Query-count tests** - `backend/tests/IntegrationTests/Perf/QueryCountTests.cs`
  locks the number of SQL queries the hot endpoints issue (via a `DbCommandInterceptor`
  wired up by `ApiTestContext.CreateAsync(countQueries: true)` and asserted with
  `AssertAtMost`/`AssertSameAs` in `QueryCount.cs`). An N+1 that sneaks into a page
  assembly fails the suite long before the nightly does.
- **`pg_stat_statements`** - preloaded by the compose Postgres and created on first
  init of a fresh volume (`deploy/postgres/init-statements.sql`).

## How the heavy paths work

- **Board columns are paged.** Every matching card's placement is read as a narrow
  `(id, state_id, board_column_id)` projection so counts and WIP stay exact, but full
  cards are built only for the first `take` of each column (default 50, the SPA asks
  for 30); `expand=columnId:n` grows one column as it is scrolled.
- **Subtasks load on demand.** A card's subtasks are fetched only when the card is
  opened, not for every card on the board.
- **Key lookups use an index.** `work.items (organization_id, project_key, number)`
  serves every lookup that addresses an item by its human key: item detail and the MCP
  tools that filter on `project_key`.

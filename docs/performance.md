# Performance

The baseline for a busy organization and the machinery that guards it. The product
budget: **p95 < 200 ms** for list/board endpoints at
100k items per org, **search < 300 ms**.

## What guards the budget

- **Nightly k6 run against compose** — `perf/*.js`, driven by
  `.github/workflows/perf.yml` (cron, plus `workflow_dispatch`). It builds the branch,
  boots the deploy stack, seeds the perf organization, runs five load scripts whose
  thresholds *are* the budget, and uploads the summaries plus a `pg_stat_statements`
  top-10 dump as the `perf-results` artifact. A failed threshold fails the job. See
  `perf/README.md` for running it locally.
- **The perf dataset** — `dotnet run --project backend/src/Api -- aictiq-seed-perf`
  (or `docker compose run --rm api aictiq-seed-perf`) seeds an organization `perf`
  with 20 projects × 5,000 items (100k), 400k history rows, 50k comments, sprints,
  labels and one default team per project, in about 2½ minutes. Deterministic; skips
  if the organization already exists.
- **Query-count tests** — `backend/tests/IntegrationTests/Perf/QueryCountTests.cs`
  locks the number of SQL queries the hot endpoints issue (via a `DbCommandInterceptor`
  wired up by `ApiTestContext.CreateAsync(countQueries: true)` and asserted with
  `AssertAtMost`/`AssertSameAs` in `QueryCount.cs`). An N+1 that sneaks into a page
  assembly fails the suite long before the nightly does.
- **`pg_stat_statements`** — preloaded by the compose Postgres and created on first
  init of a fresh volume (`deploy/postgres/init-statements.sql`).

## Baseline (September 2026)

Measured locally against the compose stack seeded with the perf organization:

| Script | Load | p95 | Target |
| --- | --- | --- | --- |
| `list.js` | 20 req/s | 15 ms | < 200 ms |
| `board.js` | 5 req/s | 77 ms | < 200 ms |
| `search.js` | 10 req/s | 20 ms | < 300 ms |
| `detail.js` | 10 req/s | 6 ms | < 200 ms |
| `mcp.js` (`list_ready_work`) | 1 req/s | 50 ms | < 300 ms |

Board is the heaviest page by design. It used to return every team card grouped by column
(~3.5k cards for a seeded team's project), which put most of its p95 into serialization
rather than SQL (the board query itself plans at ~11 ms on 100k rows). Columns are now
paged: every matching card's placement is read as a narrow `(id, state_id, board_column_id)`
projection so counts and WIP stay exact, but full cards are built only for the first `take`
(default 50, the SPA asks for 30) of each column in rank order, and `expand=columnId:n`
grows one column as it is scrolled. Subtasks are fetched only for a card whose list is
opened — fetching them for every card on load was ~600 requests on a 1k-card board and
tripped the rate limiter. On that board: 41k → 7k DOM nodes, 270 → 138 MB JS heap.

## What the review changed

`EXPLAIN ANALYZE` on the top queries (100k items / 20 projects / 400k history) found
one missing index, added by the `PerformanceIndexes` migration:

- `work.items (organization_id, project_key, number)` — every lookup that addresses an
  item by its human key (item detail, and the MCP tools that filter on `project_key`)
  seq-scanned up to 100k rows because the unique `(organization_id, project_id, number)`
  index cannot serve a key-based predicate. Detail went 2.15 ms → 0.03 ms; the MCP tools
  ~25 ms → ~3 ms.

Candidates considered and **rejected**, with evidence:

- `items (team_id, type, rank)` for the board — the sort is ~3 ms; an ordered index
  scan would trade ~670 sequential heap blocks for thousands of random fetches.
- `items (organization_id, project_key, assignee_id)` for `list_ready_work` — the plan
  is already ~3 ms after the key index; the composite would double write amplification
  on every assign.
- History, comments, relations, labels and watchers indexes — the existing ones already
  serve those plans (per-row probes measured in the microsecond range).

## Findings that did not block the budget

Known shape-of-the-code costs, acceptable at this scale, worth revisiting if usage
grows:

- `OrganizationSearch` resolves project visibility one `GetProjectRoleAsync` per
  distinct project (serialized — a scoped `DbContext` tolerates no concurrent queries).
  Twenty fast lookups today; a batched contract method would remove them.
- The item-list `q` filter materializes **all** matching ids before paging; a very
  common term builds a multi-thousand-element array per request.
- Comment search ranks `ts_rank_cd` over the whole match set before `LIMIT`; adversarial
  low-selectivity terms cost tens of milliseconds. The GIN index wins at realistic
  selectivity.
- `History`/`ProjectActivity` page history in memory after loading an item's rows;
  fine at ~4 events per item.
- `WorkItemMcpTools.SearchItems` applies the filter grammar in memory *after*
  `Take(limit)`, so pre-filtered rows never reach the filter.
- Each MCP tool call writes an audit row — agent polling is, by design, a light write
  load.

## Fixed along the way

Two latent defects surfaced while measuring search, and both were fixed:

- `WikiSearchService` kept its raw-SQL reader open while the permission checks queried
  the same context — a deterministic 500 on every `types=pages` search — and fanned the
  per-project checks out with `Task.WhenAll` over one `DbContext`.
- `OrganizationSearch` issued its per-project visibility checks via `Task.WhenAll` on
  one scoped `TenancyDbContext`; the same concurrency violation waiting for a busy org.

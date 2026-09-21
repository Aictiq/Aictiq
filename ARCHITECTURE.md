# Aictiq architecture

Aictiq is a .NET 10 modular monolith for project management by software teams and their AI
agents. This is the maintained overview; [`docs/data-model.md`](docs/data-model.md) maps the
schemas, and [`docs/invariants.md`](docs/invariants.md) states the rules a change has to
respect.

## System shape

```text
browser / CLI / MCP client
           │
       API + SPA ─── SignalR
           │
    Postgres ◄── Workers ─── SMTP, GitHub, webhooks, Stripe
           │
  S3-compatible object storage
```

The `backend/src/Api` process owns HTTP, authentication, authorization, migrations, and the
production SPA. `backend/src/Workers` drains the transactional outbox and runs background
work. Aspire orchestrates the development stack; `deploy/` packages the same services for a
single-host Compose deployment.

## Repository layout

| Path | Responsibility |
| --- | --- |
| `backend/src/Api` | composition root; `/api/v1`, `/mcp`, `/hubs`, OpenAPI, SPA hosting |
| `backend/src/Workers` | outbox, email, retention, analytics, integrations and scheduled work |
| `backend/src/SharedKernel` | tenant primitives, module contracts, persistence, storage, events |
| `backend/src/Modules/*` | independently owned domain modules and database schemas |
| `frontend-vue` | Vue 3 single-page application |
| `cli` | `@aictiq/cli` REST client and stdio MCP bridge |
| `deploy` | Compose, Caddy, Garage setup, backup/restore helpers |
| `docs` | VitePress documentation site |

## Module boundaries and data

Each module owns a Postgres schema and DbContext: `identity`, `tenancy`, `work`, `wiki`,
`integrations`, `analytics`, `notify`, `billing`, and `automation`. Identity also owns the shared outbox
and audit tables. Modules do not reference each other's entity types or create cross-schema
foreign keys. Cross-module reads use contracts in `SharedKernel`; writes become integration
events persisted in the outbox in the same transaction as the originating change.

Every tenant-owned entity derives from `TenantEntity`. Its DbContext applies the current
organization filter automatically; no tenant means no rows. The request middleware resolves
the organization and membership before endpoint authorization. Postgres RLS is a second,
database-level guard for the runtime application role. Unscoped or inaccessible resources
normally answer 404, not 403.

Database constraints, triggers, and unique indexes carry invariants that must survive
concurrency: there is always an organization owner, audit and item history are append-only,
and state-changing writes use row versions/compare-and-swap. EF validation exists for useful
errors, not as the only guard.

## Identity and authorization

The browser uses short-lived access and rotating refresh tokens in httpOnly cookies. CLI,
scripts, and agents use personal access tokens (`aiq_…`) stored only as hashes, with scopes,
expiry, revocation, and optional organization binding. An agent is an Identity user with an
owner, but cannot log in interactively or create credentials. Both credential paths produce
the same principal shape, then organization and project roles narrow access further.

The SPA is same-origin with the API in production. Cookie state changes require the
`X-Aictiq-Request` header as CSRF protection; bearer-token clients do not use cookies. The
API has no permissive CORS mode.

## External boundaries

Attachments pass through the API, which re-encodes still images and writes the row before
the object. Avatars use short-lived S3 presigned URLs signed for the host the browser uses;
the API commits only validated metadata and refuses a key outside the caller's own prefix. Outbound webhooks and previews validate public destinations and defend
against private-network SSRF. SMTP, OAuth providers, GitHub, Stripe, and OpenTelemetry are
configured integrations; none owns core product state.

The outbox gives at-least-once delivery, so handlers must be idempotent with a database-backed
deduplication key. Workers never run migrations. The API migration runner takes a Postgres
advisory lock, ensuring one migrator in a multi-replica deployment.

## Operational model

Serilog writes structured logs and OpenTelemetry exports traces/metrics when configured.
Every process exposes `/health/live` and `/health/ready`; readiness intentionally exposes
only safe check descriptions. The Compose deployment puts Caddy at the boundary; Postgres
and object storage are private to its network. See [the self-hosting guide](docs/self-host.md)
and [operations guide](docs/operations.md) for configuration and runbooks.

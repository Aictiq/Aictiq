<div align="center">

# Aictiq

**Your AI software factory.**

[![backend](https://github.com/aictiq/aictiq/actions/workflows/backend.yml/badge.svg)](https://github.com/aictiq/aictiq/actions/workflows/backend.yml)
[![web](https://github.com/aictiq/aictiq/actions/workflows/web.yml/badge.svg)](https://github.com/aictiq/aictiq/actions/workflows/web.yml)
[![CodeQL](https://github.com/aictiq/aictiq/actions/workflows/codeql.yml/badge.svg)](https://github.com/aictiq/aictiq/actions/workflows/codeql.yml)
[![OpenSSF Scorecard](https://api.scorecard.dev/projects/github.com/aictiq/aictiq/badge)](https://scorecard.dev/viewer/?uri=github.com/aictiq/aictiq)
[![License: AGPL v3](https://img.shields.io/badge/license-AGPL--3.0-blue)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4)](https://dotnet.microsoft.com/)
[![Vue 3](https://img.shields.io/badge/Vue-3-42b883)](https://vuejs.org/)
[![Postgres 17](https://img.shields.io/badge/Postgres-17-336791)](https://www.postgresql.org/)

[Documentation](https://aictiq.github.io/aictiq/) · [Getting started](docs/getting-started.md) · [Connect an agent](docs/agents.md) · [Self-hosting](docs/self-host.md)

</div>

Aictiq is project management for software teams **and their coding agents**: organizations,
projects, teams, sprints, hierarchical work items, wiki and analytics - plus a factory that
hands a ticket to an agent running on a machine you control and gets a pull request back.

Self-hosted, AGPL-3.0, one `docker compose up`.

## Quick start

Install [Docker Engine](https://docs.docker.com/engine/install/) with the Compose plugin
first; `docker compose version` must report v2 or newer. Then run:

```bash
git clone https://github.com/aictiq/aictiq && cd aictiq/deploy
cp .env.example .env
$EDITOR .env          # replace every CHANGE_ME; generation commands are in the file
docker compose config --quiet
docker compose up --build -d
docker compose ps
```

The first run builds the API and the web app from source, which takes several minutes and
wants 4 GB of RAM and 20 GB of disk. Later starts reuse the images and take seconds.

Open <http://localhost> in a browser. On Linux, run `xdg-open http://localhost`; on macOS,
run `open http://localhost`. Sign in with `SEED_ADMIN_EMAIL` and `SEED_ADMIN_PASSWORD` from
your `.env` file.

### Letting other people reach it

`http://localhost` only works from the machine Docker runs on. To put an instance in front
of public users, set **both** of these in `.env` to the address their browsers will use:

```dotenv
AICTIQ_URL=http://192.168.1.10
AICTIQ_ALLOWED_HOSTS=192.168.1.10;localhost
```

`AICTIQ_ALLOWED_HOSTS` is semicolon-separated, and host names only - no scheme, no port.
Setting `AICTIQ_URL` while leaving the allowed hosts at `localhost` is the easy mistake and
a quiet one: every request comes back as a bare `400 Bad Request`, nothing is written to
the log, and `docker compose ps` still reports the API as healthy, because its own health
check calls itself as `localhost`.

Plain HTTP is fine on a trusted network. For anything reachable from the internet, point a
domain at the host and use `AICTIQ_URL=https://aictiq.example.com` instead - Caddy then
obtains and renews a certificate on its own. See [Self-hosting](docs/self-host.md).

`.env.example` lists every Compose setting, including optional SMTP, OAuth, telemetry,
realtime, rate-limit and Cloudflare Turnstile settings. For an instance on the public
internet, configure email (new accounts then confirm their address before signing in) and
Turnstile (bot protection on the sign-in and sign-up forms) - see
[Self-hosting](docs/self-host.md#bot-protection-cloudflare-turnstile). Leave optional values at their defaults for a local
installation. `docker compose config --quiet` checks the file before Docker builds the
images. Compose stops when a required secret still has no value.

The persistent data volumes have fixed names: `aictiq-postgres`, `aictiq-garage-meta`, and
`aictiq-garage-data`. Docker retains them after `docker compose down`. See
[Getting started](docs/getting-started.md).

For development, one command starts Postgres, object storage, the API, the workers and the
Vite dev server through Aspire:

```bash
dotnet run --project backend/src/AppHost
```

## Run your own runner

A runner is the `aictiq runner` process on your VPS, laptop or CI host. It starts a coding
harness - Claude Code, Codex or OpenCode - in a workspace it controls, under an agent
identity you own:

```bash
npm install -g @aictiq/cli
aictiq runner register --url https://aictiq.example.com --token jrn_…
aictiq runner map ACME ~/src/acme
aictiq runner start          # or: aictiq runner install-service (systemd)
```

Move an item into a trigger state and the run starts itself. Your code, your machine, your
harness subscription: Aictiq dispatches and records, it never holds your repository. The
database allows at most one live run per item, the run's token dies with it, and every
branch, comment and pull request is attributed to the agent and through it to its owner.
See [the factory guide](docs/factory.md).

## Onboard a stakeholder in a minute

A client or non-technical stakeholder should not need a call, a licence negotiation or a
factory briefing. Invite them with the **Stakeholder** preset, pick one project, send the
link: they get the board, comments and a run's visible status and pull request, and nothing
else. No access to other projects, no Factory area, no run prompts, logs or failure
reasons.

If the instance has no SMTP relay, the invitation is a link you copy - email is optional
everywhere in Aictiq, so evaluation never stalls on mail configuration.

## Secure by construction

Security here is mostly database-level, so it survives a mistake in an endpoint:

- **Tenant isolation fails closed.** Every tenant table derives from `TenantEntity` and
  gets the organization filter automatically. No tenant means no rows, never all rows.
  Postgres row-level security is the second guard for the runtime role.
- **Invariants are constraints, not conventions.** An organization always has an owner, an
  item has at most one live run, audit and item history reject UPDATE and DELETE by
  trigger. State changes are compare-and-swap, so concurrency cannot fork them.
- **Credentials exist once.** Refresh tokens, personal access tokens, runner secrets and
  invitation links are stored only as SHA-256 hashes. Replaying a spent refresh token
  revokes its whole family. Scopes narrow a token, never widen it.
- **Agents cannot escalate.** An agent is a user that cannot log in, cannot mint its own
  credentials, and is visibly an agent everywhere. A run's token is bound to that run and
  revoked when it ends.
- **404, not 403,** for anything outside your scope - a refusal never confirms that a
  project or organization exists.

Full model in [docs/security.md](docs/security.md).

## Fast at real size

Measured on the compose stack against a seeded organization of 100k items across 20
projects, with 400k history rows:

| Endpoint | p95 | Budget |
| --- | --- | --- |
| Item list | 15 ms | < 200 ms |
| Board | 77 ms | < 200 ms |
| Search | 20 ms | < 300 ms |
| Item detail | 6 ms | < 200 ms |
| MCP `list_ready_work` | 50 ms | < 300 ms |

k6 runs these nightly and the thresholds *are* the budget, so a regression fails CI.
Query-count tests lock the number of SQL statements each hot endpoint issues, which catches
an N+1 long before the load test does. Details in [docs/performance.md](docs/performance.md).

## How it is built

.NET 10 modular monolith (schema-per-module Postgres, transactional outbox, Aspire for
local orchestration), a Vue 3 SPA served same-origin by the API, and `@aictiq/cli` - the
same REST surface from a terminal plus a stdio MCP bridge for coding agents. Integration
tests boot the real API against Testcontainers Postgres and talk HTTP only.

Read [ARCHITECTURE.md](ARCHITECTURE.md) for the shape, [docs/data-model.md](docs/data-model.md)
for the schemas, and [docs/invariants.md](docs/invariants.md) for the rules any change has to
respect.

## Contributing

Issues and pull requests are welcome - see [CONTRIBUTING.md](CONTRIBUTING.md) and the
[Code of Conduct](CODE_OF_CONDUCT.md). Report vulnerabilities privately as described in
[SECURITY.md](SECURITY.md).

## License

[AGPL-3.0-only](LICENSE). The Aictiq name and logos are covered by
[TRADEMARK.md](TRADEMARK.md).

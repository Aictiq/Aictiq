# Getting started

The fastest way to run Aictiq is the compose bundle in the repository's `deploy/`
directory — five commands from a clean host to a running instance. This page gets you
there; [Self-hosting](./self-host.md) covers everything you may want to change
afterwards.

## Prerequisites

- A host with Docker and the compose plugin. The images build from source, so no .NET or
  Node tooling is needed on the host.
- The repository checkout — the bundle, its `.env.example` and the Garage configuration
  live in [`deploy/`](../deploy/README.md).
- For evaluation, nothing else. For a real deployment: ports 80 and 443 reachable from
  the internet and a DNS name pointed at the host.

## Start the stack

```bash
cd deploy
cp .env.example .env
$EDITOR .env          # fill in every CHANGE_ME — the file shows how to generate each secret
docker compose up --build -d
open http://localhost
```

Compose refuses to start rather than inventing a default for any secret, so a half-filled
`.env` fails loudly instead of shipping a guessable key.

The first start builds the API and web app images, initializes Postgres, gives Garage a
cluster layout and a bucket, and the API applies migrations and seeds the administrator.
Sign in at `http://localhost` with the `SEED_ADMIN_EMAIL` and `SEED_ADMIN_PASSWORD` you
set in `.env`.

What is running: Postgres, Garage (object storage), the API — which serves the web app
from the same origin — the background workers, and Caddy terminating TLS. Only Caddy
publishes ports; the rest are reachable only inside the compose network.

## First steps in the app

1. Change the seeded administrator's password — it stays in `.env` until you do.
2. Create a project. Its key (`ACME` in `ACME-123`) prefixes every item id your team will
   quote in commits, chat and agent configuration, and it is permanent.
3. Invite the team. With no SMTP relay configured, invitations are shared as links
   instead of mail — email is optional everywhere. See
   [Self-hosting → Email](./self-host.md#email).

## The in-app tour and Get started

Aictiq offers a short product tour on a new account's first working screen, and a
resumable **Get started** checklist behind the account menu, the command palette
(`Ctrl/Cmd+K`) and *Account settings → Profile*. Both stay available afterwards: **Get
started** reopens the checklist, **Replay product tour** starts the orientation again, and
neither undoes anything you have already set up.

The tour explains the navigation, organizations, projects and teams, the board, writing an
item an agent can run, project knowledge in the wiki, and — for whoever may operate the
factory — agents, runners, playbooks, the explicit handoff, and reviewing the result. It is
read-only: walking it dispatches nothing and changes no configuration.

The checklist is the same path as this page and [the factory guide](./factory.md), with a
live status per task computed from the API: **Ready**, **Needs setup**, **Needs an admin**,
or **Could not check**. It tells registration apart from an online runner, and an online
runner apart from a machine whose harness, repository checkout and push credentials
actually work — those last it can only tell you to verify on the machine itself.

Teams that only track work can ignore the AI tasks indefinitely; nothing prompts again
once the tour is skipped or finished.

## Connect an agent

Setting up AI work end to end — an agent account, a runner, a playbook and a repository —
is what **Get started** walks through, and [the factory guide](./factory.md) is the long
form. The steps below are the separate, optional path for pointing *your own* MCP client
or terminal at Aictiq with a personal access token. A personal token is not a runner
credential: a runner gets its own secret from *Factory → Runners*.

1. Create an agent and its token in *Organization settings → Agents*. A token for MCP
   needs the `mcp` scope and an organization binding; add `read` and `write` for an
   agent that will claim and update work. The full instructions are in
   [Connect an agent](./agents.md).

2. Point Claude Code at it over HTTP:

   ```json
   {
     "mcpServers": {
       "aictiq": {
         "type": "http",
         "url": "https://aictiq.example.com/mcp",
         "headers": { "Authorization": "Bearer aiq_your_token" }
       }
     }
   }
   ```

3. Or work from a terminal through the CLI:

   ```bash
   npm install -g @aictiq/cli
   aictiq auth login --url https://aictiq.example.com
   aictiq item list -p ACME --filter "state:todo"
   ```

The loop the agent should follow — claim before writing code, heartbeat while working,
report progress by editing one comment, link the pull request — is the second half of
[Connect an agent](./agents.md).

## Serving a real domain

Set `AICTIQ_URL=https://aictiq.example.com` in `.env` and Caddy obtains and renews the
certificate automatically; ports 80 and 443 must be reachable for the ACME challenge. A
non-default port must also appear in `AICTIQ_URL`. Published-image configuration,
realtime scale-out and troubleshooting live in the repository's
[`deploy/README.md`](../deploy/README.md).

## Where to next

- [Self-hosting](./self-host.md) — object storage (AWS S3, MinIO, R2), external
  Postgres, rate limits, backups and the restore drill.
- [Operations](./operations.md) — the observability profile, dashboards, and what pages
  an operator.
- [REST API](./api.md) — authentication, filtering, concurrency and error conventions.
- [Outgoing webhooks](./webhooks.md) — events, signatures and retries.
- [FAQ](./guide/faq.md) — the questions that come up after the first week.

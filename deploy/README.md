# Running Aictiq with docker compose

For evaluating Aictiq and for small self-hosted deployments. Everything runs on one host:
Postgres, Garage (object storage), the API (which also serves the web app), the background
workers, and Caddy in front terminating TLS.

## Quick start

```bash
cd deploy
cp .env.example .env
$EDITOR .env          # fill in every CHANGE_ME - the commands to generate them are in the file
docker compose up --build -d
open http://localhost # sign in with SEED_ADMIN_EMAIL / SEED_ADMIN_PASSWORD
```

Compose refuses to start rather than invent a default for any secret, so a half-filled
`.env` fails loudly instead of shipping a guessable key.

The first `up` builds the API and web app in Docker, then Postgres initialises, Garage is
given a cluster layout and bucket, and the API applies migrations and seeds the
administrator. Later starts reuse the cached images; pass `--build` again after changing
the checkout.

## Serving it on a real domain

Point the domain's DNS at the host, then set:

```
AICTIQ_URL=https://aictiq.example.com
```

Caddy obtains and renews a certificate automatically - nothing else to configure. Ports
80 and 443 must be reachable from the internet for the ACME challenge.

To use your own certificate instead, replace the `tls` behaviour in `Caddyfile`:

```
{$AICTIQ_SITE} {
	tls /etc/caddy/cert.pem /etc/caddy/key.pem
	...
}
```

and bind-mount the files into the `caddy` service.

## Using published images

The default is deliberately a source build, so a fresh clone works even where the
optional GHCR packages are not public. If your host has access to published images, set
these values in `.env` to skip the local build:

```dotenv
AICTIQ_IMAGE_PULL_POLICY=always
AICTIQ_IMAGE_REGISTRY=ghcr.io/green-code-dev
AICTIQ_IMAGE_TAG=<release-tag>
```

Then run `docker compose pull && docker compose up -d`. `build-local.sh` remains useful
for CI and developers who want to create image tags with the .NET SDK directly, but is
not required for self-hosting.

Published release bundles ship an `.env.example` whose image registry and tag are already
pinned to that release. Do not change just one of the API and Workers tags: they are a
matched release pair.

## Version and update checks

`GET /api/v1/meta` reports the running version. The app footer shows it too. A release
check is off by default; to enable it, set the following non-secret values in `.env`:

```dotenv
RELEASE_UPDATE_CHECK_ENABLED=true
RELEASE_UPDATE_CHECK_REPOSITORY=Green-Code-DEV/aictiq
```

When enabled, a signed-in browser queries GitHub's public releases endpoint at most once
per day and links to a newer stable release. No GitHub token is used. Leave it disabled
when that outbound browser request is not appropriate for your deployment.

## Realtime scale-out

Realtime project updates use SignalR and Postgres `LISTEN`/`NOTIFY` by default, so every
API replica receives the same `aictiq_rt` message. Workers publish on that channel too -
item changes and in-app notifications are handled there - so set the same
`Realtime__Backplane` value on both `api` and `workers`. Set `Realtime__Backplane=none` to
disable pushes. For higher-throughput deployments set `Realtime__Backplane=redis` and
`Realtime__RedisConnectionString=<redis connection string>` (the API needs the connection
string; Workers still publish over `NOTIFY`); the API enables SignalR's
`AddStackExchangeRedis` backplane in that mode, and exactly one API replica forwards what
Workers publish into it.

## What talks to what

```
                 :80/:443
                    │
                  caddy
                    ├── /s3/*  ──►  garage:3900     (presigned uploads/downloads)
                    └── /*     ──►  api:8080        (SPA + /api + /hubs + /health)
                                       │
                        ┌──────────────┼──────────────┐
                   postgres:5432   garage:3900     workers
```

Only Caddy publishes ports. Postgres and Garage are reachable only from inside the compose
network.

**The `/s3` route is subtle and deliberate.** Uploads never pass through the API: it hands
the browser a presigned URL and the browser talks to Garage directly. A presigned URL's
signature covers the request path and the `Host` header, so:

- Caddy strips `/s3` before forwarding (`handle_path`), and the API adds that prefix to the
  URL *after* signing it (`S3__PublicPathPrefix`). Both ends therefore sign the same path.
  Putting `/s3` in `S3__PublicEndpoint` instead would sign the prefixed path and fail.
- Caddy does **not** rewrite `Host`. Adding `header_up Host {upstream_hostport}` would
  invalidate every presigned URL.

Serving the store on the app's own origin this way also means uploads are same-origin, so
there is no CORS configuration to get wrong.

## Profiles

```bash
docker compose --profile mailpit up -d      # local SMTP sink at http://localhost:8025
```

`mailpit` captures outgoing mail instead of sending it - useful for watching invitations
and notifications without a real SMTP server. Point the app at it with

```dotenv
EMAIL_SMTP_HOST=mailpit
EMAIL_SMTP_PORT=1025
EMAIL_SMTP_STARTTLS=false
EMAIL_FROM_ADDRESS=aictiq@localhost
```

Email is optional everywhere. With `EMAIL_SMTP_HOST` empty the stack starts normally,
`/health/ready` reports `{"checks":{"email":"unconfigured"}}`, and every queued message is
parked as `skipped` in `notify.email_outbox` rather than retried forever - invitations are
shared as links instead.

## Upgrading

For the default source-build deployment, pull the new checkout and rebuild:

```bash
git pull
docker compose up --build -d
```

For the optional published-image configuration above, use `docker compose pull && docker
compose up -d` instead.

The API applies migrations on start, under a Postgres advisory lock, so a restart is safe
even with several instances. Workers wait for the API to be healthy and never migrate.

Read the release notes before a major upgrade, and take a backup first.

Images only ever migrate a database forward. Do not roll an older API image back against
a database started by a newer image; restore the backup taken before the upgrade instead.
Release notes call out any breaking configuration changes before the migration note.

## Backups

The state worth keeping lives in three volumes: `aictiq_pgdata` (everything relational),
`aictiq_garage-meta` and `aictiq_garage-data` (uploaded files). The scripts below stop the stack, archive both halves and restore them.

```bash
docker compose down
docker run --rm -v aictiq_pgdata:/v -v "$PWD":/out alpine tar czf /out/pgdata.tgz -C /v .
```

## Troubleshooting

**`docker compose up` fails with "required variable ... is missing a value"** - a
`CHANGE_ME` is still in `.env`, or `.env` is not in this directory.

**Garage rejects the access key on first run** - Garage requires the access key to be `GK`
followed by exactly 24 hex characters and the secret to be 64 hex characters. Regenerate
them with the commands in `.env.example`.

**Everyone gets rate-limited at once** - the API is not seeing real client addresses.
`CADDY_IP` in `.env` must match the address Caddy actually has on the compose network; it
is pinned by `AICTIQ_SUBNET` for exactly this reason.

**Uploads fail with `SignatureDoesNotMatch`** - something between the browser and Garage is
rewriting the `Host` header or the path. See the `/s3` note above and
[`../docs/self-host.md`](../docs/self-host.md).

**A non-default port does not work** - `HTTP_PORT` must also appear in `AICTIQ_URL`
(`AICTIQ_URL=http://localhost:8080` with `HTTP_PORT=8080`). Caddy listens on the port its
site address names.
# Backups

Run `./backup.sh` to create a Postgres custom-format dump and S3 object copy under
`backups/`. Run `./restore.sh backups/<timestamp>` for a deliberately destructive restore
drill. See [the self-hosting guide](../docs/self-host.md#backup-restore-and-upgrades) for
the maintenance-window and upgrade requirements.

# Self-hosting Aictiq

The fastest way to run Aictiq is the compose bundle in
[`../deploy/`](../deploy/README.md) - five commands from a clean host. This document
covers the parts you may want to change, including backup and restore.

## Object storage

Aictiq stores attachments, avatars and wiki uploads in an S3-compatible object store.
The application speaks the S3 API and nothing else - there is no Garage-specific code
anywhere in the backend - so any S3 implementation works by changing configuration.

### What ships by default

[Garage](https://garagehq.deuxfleurs.fr/) runs alongside Postgres in development
(Aspire) and in the compose bundle. It is small, has no cloud dependencies, and stores
its data in a single volume. The configuration lives in `deploy/garage/garage.toml`, and
`deploy/garage/init.sh` provisions a fresh node: a cluster layout (without one Garage
rejects every S3 call), a bucket, and a key allowed to read and write it. The script is
idempotent, so restarting the container is safe.

### Configuration

| Setting | Environment variable | Meaning |
| --- | --- | --- |
| `S3:Endpoint` | `S3__Endpoint` | Where the API and Workers reach the store. Usually an internal address. |
| `S3:PublicEndpoint` | `S3__PublicEndpoint` | Where the **browser** reaches it. Defaults to `S3:Endpoint`. |
| `S3:PublicPathPrefix` | `S3__PublicPathPrefix` | A path prefix the browser sends and a proxy strips (`/s3`). Empty unless the store is served under a path on the app's origin. |
| `S3:Bucket` | `S3__Bucket` | Bucket name. `aictiq` by default. |
| `S3:AccessKey` | `S3__AccessKey` | Access key id. |
| `S3:SecretKey` | `S3__SecretKey` | Secret access key. |
| `S3:Region` | `S3__Region` | Signing region. `garage` for Garage; the real region for AWS. |
| `S3:ForcePathStyle` | `S3__ForcePathStyle` | `true` for Garage and MinIO. `false` for AWS S3 and R2. |
| `S3:UploadUrlTtlMinutes` | `S3__UploadUrlTtlMinutes` | Lifetime of a presigned upload URL. Default 15. |
| `S3:DownloadUrlTtlMinutes` | `S3__DownloadUrlTtlMinutes` | Lifetime of a presigned download URL. Default 5. |

These are validated when the service starts, so a typo stops a deployment rather than
surfacing later as a failed upload. `/health/ready` includes a `storage` check that HEADs
the bucket - it turns **Unhealthy** if credentials, addressing style or the bucket itself
are wrong, which keeps traffic away from a node that cannot accept an upload.

### `PublicEndpoint`: the one setting people get wrong

Attachments on items and comments pass through the API - images are re-encoded to WebP on
upload - and are served from an authenticated API route, so they need nothing here. Avatars
still do not pass through the API: the API hands the browser a **presigned URL** and the
browser talks to the object store directly.

A presigned URL is signed with SigV4, and **the signature covers the `Host` header**. So
the URL must name the host the browser actually connects to. If the API reaches the store
at `http://garage:3900` on an internal network but the browser reaches it at
`https://files.example.com`, then:

```
S3__Endpoint=http://garage:3900
S3__PublicEndpoint=https://files.example.com
```

Get this wrong and uploads fail with `SignatureDoesNotMatch`. Note also that a reverse
proxy in front of the store must **not** rewrite the `Host` header (in nginx:
`proxy_set_header Host $host;` with the store served on its own name, or
`proxy_pass` without host rewriting) - rewriting it invalidates every signature.

The scheme, by contrast, is *not* signed, so terminating TLS at a proxy in front of a
plain-HTTP store is fine.

### Serving the store under a path on the app's origin

The compose bundle does this: Caddy routes `/s3/*` to Garage and strips the prefix. It
keeps everything on one origin, so uploads are same-origin and there is no CORS rule to
get wrong.

The signature covers the path, so the prefix cannot simply live in `S3:PublicEndpoint` -
the SDK would sign `/s3/aictiq/<key>` while the store, which receives the stripped
`/aictiq/<key>`, verifies the bare one. `S3:PublicPathPrefix` exists for exactly this:
the URL is signed **without** the prefix and the prefix is added afterwards, so both ends
agree. Set it to whatever the proxy strips:

```
S3__PublicEndpoint=https://aictiq.example.com
S3__PublicPathPrefix=/s3
```

and in Caddy:

```
handle_path /s3/* {
	reverse_proxy garage:3900
}
```

`handle_path` (not `handle`) is what strips the prefix. Leave `Host` alone.

### Swapping Garage out

Nothing but configuration changes. Remove the `garage` and `garage-init` containers from
your deployment and point the variables at the store you want.

**MinIO**

```
S3__Endpoint=http://minio:9000
S3__PublicEndpoint=https://files.example.com
S3__Bucket=aictiq
S3__AccessKey=...
S3__SecretKey=...
S3__Region=us-east-1
S3__ForcePathStyle=true
```

**AWS S3**

```
S3__Endpoint=https://s3.eu-west-1.amazonaws.com
S3__Bucket=your-bucket
S3__AccessKey=AKIA...
S3__SecretKey=...
S3__Region=eu-west-1
S3__ForcePathStyle=false
```

**Cloudflare R2**

```
S3__Endpoint=https://<account-id>.r2.cloudflarestorage.com
S3__PublicEndpoint=https://files.example.com
S3__Bucket=aictiq
S3__AccessKey=...
S3__SecretKey=...
S3__Region=auto
S3__ForcePathStyle=false
```

### Browser uploads and CORS

When the browser PUTs directly to a store on a **different origin** from the app, that
store needs a CORS rule allowing `PUT` and the `Content-Type` header from the app's
origin. Serving the store under the app's own domain through the reverse proxy (the
`/s3/` path the compose bundle uses) avoids CORS entirely and is the recommended setup.

### Object keys

```
org/{orgId}/project/{projectId}/attachments/{attachmentId}/{filename}
```

Tenant-first, so one organisation's objects can be listed, exported or deleted as a single
prefix. The authoritative record of what exists is the metadata in Postgres; a Workers job
removes blobs whose metadata is gone.

## First run

The API seeds an administrator and, optionally, the organization they own the first time
it starts against an empty database:

| Setting | Environment variable (compose) | Meaning |
| --- | --- | --- |
| `Seed:AdminEmail` | `SEED_ADMIN_EMAIL` | The first user. Created with the `Admin` role and a confirmed email. |
| `Seed:AdminPassword` | `SEED_ADMIN_PASSWORD` | Their initial password. Change it after signing in - it stays in `.env` otherwise. |
| `Seed:OrganizationName` | `SEED_ORG_NAME` | The organization to create, owned by that administrator. Blank means none. |
| `Seed:OrganizationSlug` | `SEED_ORG_SLUG` | Its address, the `{slug}` in `/orgs/{slug}`. Derived from the name when blank. |
| `Seed:OrganizationTimeZone` | `SEED_ORG_TIME_ZONE` | IANA identifier deciding when "today" ends for due dates and sprint boundaries. |
| `Org:AllowSelfServeCreation` | `ORG_ALLOW_SELF_SERVE_CREATION` | `true` by default. Set `false` to make the instance invitation-only; non-members are never told the seeded organization's identity. |

Organization seeding is **first-run only in the strict sense**: it does nothing once any
organization exists, not merely when one with that slug exists. Keying on the slug would
recreate a deliberately deleted organization on the next restart.

## External Postgres and tenant roles

Aictiq uses two database logins. `aictiq_app` is the API/Workers login and is subject to
row-level security; it must not own the database, be a superuser, or have `BYPASSRLS`.
`aictiq_admin` owns the database and is used by the API only while applying migrations.
The Compose bundle creates both on its first Postgres initialization. For an external
Postgres cluster, have a cluster administrator bootstrap them before first start:

```sql
CREATE ROLE aictiq_admin LOGIN PASSWORD 'use-a-secret' NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT;
CREATE ROLE aictiq_app LOGIN PASSWORD 'use-a-different-secret' NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT;
CREATE DATABASE aictiq OWNER aictiq_admin;
GRANT CONNECT ON DATABASE aictiq TO aictiq_app;
```

Then configure the API with both connections. Workers use the private admin connection
for bounded cross-organization maintenance (outbox, retention, billing, and webhook
sweeps); they expose no public application routes:

```dotenv
ConnectionStrings__appdb=Host=db.example;Database=aictiq;Username=aictiq_app;Password=...
ConnectionStrings__appdb-admin=Host=db.example;Database=aictiq;Username=aictiq_admin;Password=...
```

At startup the API uses `appdb-admin` under its advisory lock to run migrations. Those
migrations grant the app role only the required schema/table permissions and add `FORCE
ROW LEVEL SECURITY` policies to tenant data. Each API application query sets its organization
in `app.org_id`; an absent setting returns no tenant rows, including for raw SQL. Do not
put `appdb-admin` in a shell or an API request configuration. For a
managed service, ask its operator to run the bootstrap SQL if your database user cannot
create roles or databases.

The slug is permanent. Renaming an organization changes its display name and leaves the
address alone, because a slug is in every link a team has bookmarked, pasted into chat, or
written into an agent's configuration. Deleting an organization does not release its slug
either - a stale link should stay dead rather than start resolving to whoever claimed the
name next.

## Email

Email is **optional**. An instance with no SMTP relay is a supported deployment, not a
broken one - it starts normally, and the screens that would have sent a message offer a
link to copy instead.

| Setting | Environment variable (compose) | Meaning |
| --- | --- | --- |
| `Email:Smtp:Host` | `EMAIL_SMTP_HOST` | The relay. **Empty means no email at all**; everything else here is then ignored. |
| `Email:Smtp:Port` | `EMAIL_SMTP_PORT` | 587 for submission, 465 for implicit TLS, 25 for a relay on your own network. |
| `Email:Smtp:UserName` | `EMAIL_SMTP_USERNAME` | Blank for a relay that authenticates by network location. |
| `Email:Smtp:Password` | `EMAIL_SMTP_PASSWORD` | |
| `Email:Smtp:UseStartTls` | `EMAIL_SMTP_STARTTLS` | On by default. Turn it off only for a sink on your own machine. |
| `Email:FromAddress` | `EMAIL_FROM_ADDRESS` | The envelope sender. Required alongside the host - mail without one is rejected. |
| `Email:FromName` | `EMAIL_FROM_NAME` | Display name on the From header. |
| `Email:BaseUrl` | `AICTIQ_URL` | Where links in email point. Must be the address a **recipient's browser** can reach. It is required whenever SMTP is configured; without it link-bearing mail is parked rather than built from a request host. |

## Backup, restore, and upgrades

Back up both Postgres and Garage: the database only records attachment metadata, while
Garage contains the objects. From `deploy/`, run `./backup.sh` (optionally pass a target
directory). It briefly stops API and Workers to avoid writes during the dump and S3 sync.

To restore, start the compose dependencies, then run `./restore.sh backups/<timestamp>`.
The command requires typing `RESTORE`, replaces the database and bucket, and starts API
and Workers again. Practise this in a disposable environment; the backup and restore are
not atomic across Postgres and object storage, so keeping writers stopped is essential.

Personal access tokens issued before the `aiq_` prefix (they start with `jgl_`) no longer
authenticate: the prefix is how the API decides which handler sees the bearer. Issue new
tokens and update any CLI, MCP or CI configuration that carries one. Runner secrets
(`jrn_`) and browser sessions are unaffected.

API startup applies forward migrations. Downgrades are unsupported: restore a backup only
with a compatible application image. To move object stores, sync the bucket through the
S3 API rather than copying Garage volumes, then update the S3 settings and verify objects
before switching traffic.
| `Auth:Google:ClientId` / `ClientSecret` | `AUTH_GOOGLE_CLIENT_ID` / `_SECRET` | Optional. Set both to offer "Continue with Google". |
| `Auth:GitHub:ClientId` / `ClientSecret` | `AUTH_GITHUB_CLIENT_ID` / `_SECRET` | Optional. Set both to offer "Continue with GitHub". |

The redirect URI to register with each provider is
`${AICTIQ_URL}/api/v1/auth/external/<provider>/oauth` - `google` or `github`. Ask GitHub
for the `read:user` and `user:email` scopes: without the second one Aictiq is never told
which of the account's addresses has been verified, and it will refuse to sign anyone in
rather than trust an unverified one.

`/health/ready` reports which of the two states you are in:

```json
{ "status": "Healthy", "checks": { "email": "unconfigured" } }
```

**Unconfigured is Healthy, never Degraded.** A readiness probe exists to keep traffic away
from an instance that cannot serve; one that deliberately does not send email serves fine.

### How a message actually leaves

A module that has something to say raises `SendEmailRequested` through the transactional
outbox - never an SMTP call on the request path, so a relay that is slow or down cannot
fail the write that occasioned the message. Workers render it into `notify.email_outbox`
and a sweep hands each row to the relay, retrying with exponential backoff.

Rows settle into one of three terminal states, and the table is where you look when
someone says they never got an email:

- `sent` - the relay accepted it. Collected after `Email:Delivery:RetentionDays` (30).
- `skipped` - there is no relay configured on this instance. Never retried; no number of
  attempts conjures a relay. Collected on the same schedule, so the queue of an instance
  that will never send stays bounded.
- `failed` - a configured relay refused it `Email:Delivery:MaxAttempts` times (5). **Never
  collected**, exactly like a dead-lettered outbox message: nobody could deliver it, and
  that is an open incident rather than history. `last_error` says what the relay said.

### Watching mail without sending it

`docker compose --profile mailpit up -d` starts a local SMTP sink with a web inbox at
`http://localhost:8025`; point the app at it with `EMAIL_SMTP_HOST=mailpit`,
`EMAIL_SMTP_PORT=1025` and `EMAIL_SMTP_STARTTLS=false`. `dotnet run --project
backend/src/AppHost` wires the same thing up automatically in development.

## Rate limits

The API rate-limits by client address, except for requests carrying a personal access
token - those are partitioned by a hash of the token instead, so agents and CI sharing one
egress address do not throttle each other. The shipped defaults suit a team behind an
ordinary connection; raise them if your users reach the instance through a single NAT or
VPN address, and remember that a limit is a per-minute fixed window.

| Setting | Environment variable (compose) | Default | Meaning |
| --- | --- | --- | --- |
| `RateLimiting:GlobalPermitLimitPerMinute` | `RATE_LIMITING_GLOBAL_PER_MINUTE` | 300 | Every request, per address (or per token). |
| `RateLimiting:UploadPermitLimitPerMinute` | `RATE_LIMITING_UPLOADS_PER_MINUTE` | 20 | Presign and commit operations, per authenticated user and organization. |
| `RateLimiting:SearchPermitLimitPerMinute` | `RATE_LIMITING_SEARCH_PER_MINUTE` | 120 | Search operations, per authenticated user and organization. |
| `RateLimiting:McpRequestPermitLimitPerMinute` | `RATE_LIMITING_MCP_REQUESTS_PER_MINUTE` | 600 | HTTP requests to `/mcp`, which includes the handshake and tool listing. |
| `RateLimiting:McpPermitLimitPerMinute` | `RATE_LIMITING_MCP_TOOLS_PER_MINUTE` | 60 | Tool *calls* per token - the budget an agent polling in a loop spends. |

`RateLimiting:AuthPermitLimitPerMinute` (10 per address, on the sign-in and
password-recovery endpoints) is deliberately not a compose pass-through: it is what makes
credential stuffing expensive, and an instance that needs more than ten sign-in attempts a
minute from one address has a different problem.

The nightly performance job raises only the global guard, for exactly this reason - see
`docs/performance.md`.

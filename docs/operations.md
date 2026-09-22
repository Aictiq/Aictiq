# Operations

Two audiences share this page. Everything under **Running Aictiq** applies to any
deployment, self-hosted or hosted. **The managed service** describes what operating
hosted Aictiq commits its operator to, and is the source the sales copy must not exceed.

## Running Aictiq

99.9% monthly API availability and no silent loss of outbox messages are the internal
engineering objectives this system is built against. They are targets for the people
running it, not a service level offered to a customer; no Aictiq plan carries an SLA.

Run the observability profile with `docker compose --profile observability up -d`.
Grafana is available on `http://127.0.0.1:3000`; change `GRAFANA_ADMIN_PASSWORD` before
exposing it. The dashboard covers request latency, outbox delivery/dead letters, MCP calls
and email failures. A dead letter requires investigating its logged exception before a
retry; outbox rows are intentionally retained rather than discarded.

Page an operator for any dead letter, outbox lag over five minutes, more than ten stale
claims, or an automatically disabled webhook. First check Workers health and the affected
relay/webhook logs, then correct the dependency before redriving. Never repeatedly redrive
a message whose handler is deterministically failing.

Billing (SaaS only) is described in `billing.md`. Page on a dead-lettered
`OrganizationBillingChanged` or `OrganizationMemberAdded` message, or on a nightly
billing reconciliation failure. For a hosted subscription this is never a charge
problem - the flat organization bill does not depend on membership; the seat re-sync
that turns member changes into Stripe quantities now applies only to legacy
subscriptions. Stripe webhook deliveries failing with 400 mean the
`Stripe:WebhookSecret` is wrong.

## The managed service

Hosted Aictiq sells **operation of the shared service**, not extra product. The paid
commitment is the list below and nothing beyond it. Self-hosters run the same software
and are their own operator for all of it.

### Backups

- **What is backed up.** Both Postgres (`pg_dump`, custom format) and the object store,
  because the database holds only attachment metadata while the bucket holds the files.
  `deploy/backup.sh` is the same script self-hosters run; it briefly stops API and Workers
  so the dump and the bucket sync describe one moment.
- **Schedule and retention: deliberately unstated.** No cadence or retention window is
  published, and the website sells no backup commitment, because the restore drill below
  has not been performed. A schedule is a promise about recovery, and a promise about
  recovery that has never been exercised is not one to make. Fill both in here - and only
  then add the claim to the pricing copy - once a drill is recorded.
- **What a backup is not.** It is disaster recovery for the service, not per-organization
  undo. There is no self-service restore, no point-in-time restore of one organization's
  data, and no recovery of records a customer deleted themselves - deletion in Aictiq is
  a hard delete by design (see `security.md`). Nor does a backup recover run logs already
  pruned by the retention window.

### Recovery procedure

1. Stop API and Workers so nothing writes while data is replaced.
2. Start the compose dependencies, then run `deploy/restore.sh backups/<timestamp>`. It
   requires typing `RESTORE`, replaces the database and the bucket, and starts API and
   Workers again.
3. Restore into an application image compatible with the backup. API startup applies
   forward migrations; downgrades are unsupported, so a restore paired with an older
   image is not a supported recovery.
4. Verify before returning traffic: `/health/ready`, a sign-in, one attachment download
   and one work-item write.

Backup and restore are **not atomic across Postgres and object storage**, which is why
writers stay stopped for both halves. `self-host.md` has the same procedure in the
self-hoster's words.

> **Open operational item - blocks selling managed backups.** A restore drill has not
> been verified for the managed service. The scripts and the documentation exist, but no automated drill exists: no workflow in `.github/workflows/`
> performs backup → wipe → restore → smoke login, and no dated manual drill is recorded
> here. Until a drill has been run and its date and result recorded in this section, the
> managed-backup commitment above is a plan, not a verified capability, and must not be
> sold as one. `website/index.html` therefore advertises managed *operation and updates*
> and says nothing about backups; add the backup claim when, and only when, the drill is
> recorded here.

### Supported runner setup

Hosted Aictiq **does not provision runner machines**. The customer supplies the machine,
the repository access and the harness credentials; Aictiq dispatches to it and records
what came back.

- Supported: `aictiq runner` from `@aictiq/cli` (see `cli.md` and `factory.md`) on Linux
  or macOS, running a harness the CLI has an adapter for, reaching the API over HTTPS
  with a `jrn_` runner secret.
- A runner needs outbound HTTPS only. No inbound port, no VPN, no static address: it
  polls for work and heartbeats.
- Supported in the sense of "this is the setup support will help with". Custom harnesses,
  containerised runners and CI-hosted runners may work and are not covered.

### Support

- **Channel.** Email, one address, reachable by any Owner or Admin of a paying or
  evaluating organization. There is no phone number, no shared Slack channel and no
  on-call rotation for customers.
- **Hours.** European business hours, Monday to Friday, excluding Croatian public
  holidays. Mail sent outside them is read on the next working day.
- **No response-time guarantee.** Support is founder-led. No SLA, no first-response
  target and no uptime credit is offered, because the capacity to honour one does not
  exist yet. Do not let any page, plan card or contract imply otherwise.
- **What support covers.** Service availability and service incidents; bugs in Aictiq;
  account, billing and subscription questions; export of the customer's own data;
  guidance on setting up a supported runner.
- **What support explicitly excludes.** Writing or debugging the customer's playbooks and
  prompts; the behaviour, cost or output quality of a coding harness or model; the
  customer's repositories, CI, review process or deployments; operating or fixing the
  customer's runner machine; recovering data the customer deleted, or run logs past the
  retention window; custom development, migrations from other trackers beyond the
  documented CSV import, and self-hosted deployments (community support only).

### Hosted resource safeguards

These are independent of pricing: they protect the shared service and apply to every
hosted organization, evaluating or paid. The values below are the shipped defaults, read
from the code rather than aspirational.

| Area | Safeguard in place | Where |
| --- | --- | --- |
| Request rate | 300 requests/minute per partition. The partition is the hashed PAT or the hashed `jrn_` runner secret when one is present, otherwise the client address - so agents and CI sharing an egress IP do not throttle each other. | `RateLimiting:GlobalPermitLimitPerMinute` |
| Auth endpoints | 10 requests/minute per address. | `RateLimiting:AuthPermitLimitPerMinute` |
| Uploads, search | Post-authentication, per user **and** organization: 20 uploads/minute, 120 searches/minute. | `RateLimiting:UploadPermitLimitPerMinute`, `:SearchPermitLimitPerMinute` |
| MCP | 600 transport requests/minute per token, and a lower 60 tool calls/minute per token that answers with a protocol error carrying `Retry-After` rather than an opaque 429. | `RateLimiting:McpRequestPermitLimitPerMinute`, `:McpPermitLimitPerMinute` |
| Request bodies | MCP requests are capped at 1 MiB, enforced on Kestrel's streaming limit before parsing. The Stripe webhook body is capped at 512 KiB and answers 413 above it. | `Mcp:MaxRequestBodyBytes`; `BillingEndpoints` |
| Uploads | Attachments 25 MiB each; still images are re-encoded to WebP no wider than 1920px, with the pixel count checked from the header before decoding. Avatars 2 MiB, and the commit HEADs the object and deletes what it refuses. CSV import 20 MB. | `Attachments:MaxBytes`, `Attachments:MaxImageWidth`, `Avatar.MaxBytes` |
| Storage | 10 GiB of committed attachments per organization, refused at commit with `402 plan-limit` and `limit: "storage_bytes"`. `Billing:StorageAllowanceBytes` can narrow that for a deployment; it never widens it. | `BillingPlanLimits` |
| Log ingestion | 64 KiB per log batch, 8 MiB per run. Over the cap the API answers 413 and leaves a truncation marker at `RunLogChunk.TruncatedSeq`. Chunks insert `ON CONFLICT DO NOTHING`, so a retrying runner cannot inflate a log. | `Automation:MaxLogBatchBytes`, `Automation:MaxLogBytes` |
| Runner connections | A claim poll holds for 25 seconds; heartbeats every 60 seconds; a run whose runner has been silent for 5 minutes is swept and failed rather than left live. A disabled or deleted runner answers `401 token-revoked`. | `Automation:PollTimeoutSeconds`, `:HeartbeatIntervalSeconds`, `:RunnerLostAfterMinutes` |
| Run duration | A playbook's time limit is capped at 720 minutes, and the timeout sweep ends a run that passes its own deadline. | `Automation:MaxRunMinutes` |
| Concurrent work | At most one live run per work item, guaranteed by `ux_runs_item_live`; runners claim with `FOR UPDATE SKIP LOCKED`. A runner declares its own `maxParallel` at registration (validated 1–64) and honours it itself; the server does not schedule against it. | `Automation` migrations, `RunnerEndpoints` |
| Automation loops | `rule_firings` has a composite primary key over (rule, item, triggering event) inserted `ON CONFLICT DO NOTHING`, so a replayed transition fires nothing twice, and a rule whose own agent produced the last run on an item is skipped with `rule-loop`. | `RuleFiringHandler` |
| Outbound HTTP | Link previews and webhook deliveries go through `PublicNetworkGuard`: public addresses only, checked on the resolved address at connect time, redirects never followed. | `SharedKernel` |

Missing safeguards, stated as blockers rather than implied as capacity:

> **Launch blocker - no per-organization cap on queued or concurrently running runs.**
> `ux_runs_item_live` bounds runs *per item*, not per organization, and nothing bounds the
> depth of the queue. Throughput is bounded only by how many runners the customer
> connected, which is the customer's own machine capacity rather than a service
> safeguard. Until a cap exists, no page may describe hosted run capacity as unlimited or
> promise throughput, and pilot usage must be watched for an organization that queues far
> more than it can drain.

> **Launch blocker - no cap on runner registrations per organization.** `POST
> /orgs/{slug}/runners` is open to any organization Admin without a count limit. Each
> runner holds a long poll and a rate-limit partition of its own, so a large number of
> registered runners is a shared-service cost that nothing currently bounds.

> **Launch blocker - no rate limit on rule firings per organization.** The loop guard and
> the firing idempotency key prevent a rule re-triggering itself and prevent duplicate
> work from a replayed event. Neither bounds how often distinct legitimate transitions may
> start runs, so a busy import or bulk edit can fan out through rules.

Choose real numbers for the three above from pilot usage rather than guessing them now,
and record the chosen values in this table when they ship.

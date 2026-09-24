# Data model

One Postgres database, one schema per module, one migration history per schema. This page
is the map; the migrations in `backend/src/Modules/*/Migrations` are the truth.

## Conventions

- `snake_case` names, UUID v7 primary keys, `timestamptz` timestamps.
- Every tenant-owned table carries `organization_id uuid NOT NULL` and derives from
  `TenantEntity`, which is what applies the organization query filter and the row-level
  security policy. Composite uniqueness on such a table always includes `organization_id`.
- `xmin` is mapped as `Version` and round-trips through the API as the optimistic
  concurrency token; a stale write answers 409.
- `created_at/by` and `updated_at/by` are written by `AuditingInterceptor`, which also
  keeps sensitive columns (password and token hashes) out of the audit log.
- Modules never create cross-schema foreign keys. A reference that leaves its schema -
  an assignee, an agent, a trigger state - is validated on write instead.

## identity

ASP.NET Identity's tables plus what Aictiq needs on top, and the two shared
infrastructure tables every module uses.

| Table | Holds |
| --- | --- |
| `AspNetUsers` | people and agents - ASP.NET Identity's own table, extended with `is_agent`, `agent_owner_user_id` (an agent has an owner, a person does not), `avatar_key`, `time_zone`, `is_active` |
| `AspNetUserLogins` | Google and GitHub sign-in, unique per (provider, provider key) |
| `refresh_tokens` | SHA-256 hashes only; a rotation family is one session, and replaying a spent token revokes the family |
| `user_security_tokens` | password-reset, email-change and account-confirmation links, hashed; one live token per person per purpose |
| `personal_access_tokens` | hashed secret, 8-character prefix in the clear, scopes, expiry, revocation, optional organization binding |
| `user_onboarding` | product-tour and getting-started progress |
| `audit.audit_log` | append-only; UPDATE and DELETE are refused by trigger |
| `shared.outbox_messages` | integration events, written in the transaction that raised them |

## tenancy

The tenant boundary: who exists, where they belong and what they may do.

| Table | Holds |
| --- | --- |
| `organizations` | slug (permanent; a deleted one is retired in `retired_slugs`), name, plan, settings |
| `organization_members` | role (Owner, Admin, Member, Guest) and `can_operate_factory`; a trigger guarantees an organization always has an Owner |
| `invitations` | hashed token, role, optional project and project role, the factory flag; one live invitation per address per organization |
| `projects` | key (permanent, 2–10 uppercase), name unique per organization case-insensitively, visibility, `archived_at` |
| `project_members` | explicit project role; the implicit half comes from the organization role and the project's visibility |
| `teams` | sprint length, working days, estimation unit, time zone; exactly one default team per project |
| `team_members` | `is_lead`, capacity per day |

## work

| Table | Holds |
| --- | --- |
| `workflows`, `workflow_states`, `workflow_transitions` | per-project workflow; states carry a category (Proposed, Active, Resolved, Completed, Removed) |
| `items` | key, type (Epic, Feature, Story, Task, Bug), state, priority, assignee, team, sprint, parent, lexorank, estimates, claim fields, generated `tsvector` |
| `project_sequences` | the item number, incremented atomically in the insert's transaction |
| `labels`, `item_labels` | per-project labels |
| `comments`, `comment_revisions` | revisions are append-only |
| `item_history` | append-only field-level history; survives archival and retention |
| `attachments` | object key, size, checksum, owner (item, comment or wiki page); row first, object second |
| `item_relations`, `item_links` | related/blocks/duplicates, and commits, branches and pull requests |
| `item_templates`, `watchers`, `comment_reactions`, `saved_views` | per-project templates, per-item watchers, comment reactions and saved filters |
| `csv_import_jobs` | import runs and their per-row outcome |
| `sprints`, `sprint_scope_log`, `sprint_capacity` | one active sprint per team, no overlapping date ranges (a GiST exclusion constraint) |
| `boards` | column-to-state mapping, WIP limits, swimlanes |

Parent/child type compatibility is a trigger, not endpoint validation: an Epic takes no
parent, a Feature belongs to an Epic, a Task to a Story or Bug.

## wiki

| Table | Holds |
| --- | --- |
| `pages` | tree per project, slug unique per parent, current revision, search vector; `parent_id` cascades, so deleting a page deletes its subtree |
| `page_revisions` | append-only; DELETE only while the delete-subtree function holds its flag |
| `page_item_links` | page ↔ work item, at a revision |
| `page_permissions` | team, user or project-role grants; absence inherits from the parent, then the project |

## integrations

| Table | Holds |
| --- | --- |
| `github_installations`, `repo_bindings` | the App installation and repository-to-project bindings |
| `github_deliveries` | delivery id as primary key - webhook idempotency |
| `webhook_subscriptions`, `webhook_deliveries` | outgoing webhooks, hashed secret, per-attempt delivery record |

## analytics

| Table | Holds |
| --- | --- |
| `item_transitions` | one row per state change, keyed by the integration event id |
| `item_state_daily` | the daily snapshot burndown and flow charts read |
| `dashboards` | saved layouts |

## notify

| Table | Holds |
| --- | --- |
| `notifications` | in-app notifications per user |
| `preferences` | per-kind in-app and email choice |
| `digests`, `presence` | per-user digest scheduling, and last-seen state for read tracking |
| `email_outbox` | queued mail; claimed under `FOR UPDATE SKIP LOCKED`, swept by the delivery service, `failed` rows kept as incidents |

## billing

Hosted-service tables; a self-hosted instance carries them empty.

| Table | Holds |
| --- | --- |
| `plans` | price and a `limits` JSON document: seats, projects, storage, features, and service allowances such as run-log and analytics retention |
| `subscriptions` | Stripe ids, status, period, founding-price fields |
| `evaluations` | exactly one window per organization, born with it and never rewritten |
| `usage_snapshots`, `stripe_events` | metering, and event-id de-duplication |

## automation

The AI software factory.

| Table | Holds |
| --- | --- |
| `runners` | hashed `jrn_` secret, capabilities (harnesses, OS, arch, CLI version, parallelism, machine id), `last_seen_at`, disabled/deleted; soft-deleted because a runner id is on every run it ran |
| `playbooks` | harness, wiki page holding the instructions, success/failure states, time limit; one default per project |
| `project_settings` | repository source (GitHub binding or runner-local checkout), default branch, path hint, default agent |
| `runs` | the queue and the record: status, harness, prompt snapshot, agent token, timings, outcome, pull request, cost and tokens. A partial unique index on `(item_id) WHERE status < 3` is what guarantees **one live run per item** |
| `run_log_chunks` | `(run_id, seq)`, inserted `ON CONFLICT DO NOTHING`, capped per run and pruned after the retention window; run rows are kept |
| `rules`, `rule_firings` | state-entry triggers, and the `(rule_id, item_id, event_id)` key that makes a redelivered event fire once |

## Related

- [Architecture](../ARCHITECTURE.md) - module boundaries, contracts and the request path.
- [Engineering invariants](./invariants.md) - the rules these tables enforce.
- [Security model](./security.md) - what the constraints above are defending.
- [Performance](./performance.md) - the indexes that matter and why others were rejected.

# Engineering invariants

The rules a change to Aictiq has to respect. They are mostly architectural rather than
stylistic, and most of them are enforced by the database rather than by a code review.

## Modules

Each module owns a Postgres schema, a `DbContext` and its own migration history. Modules
never reference each other's entity types and never create cross-schema foreign keys.

- **Cross-module reads** go through a contract in `SharedKernel/Contracts`
  (`IOrganizationLookup`, `IProjectAccess`, `IUserDirectory`, `IRealtimePublisher`),
  implemented by the module that owns the data.
- **Cross-module writes** are integration events through the outbox. The one exception is
  `IWorkItemClaims`, a synchronous write, because the run dispatcher waits on the decision.
- A query that wants to filter or sort by another module's columns passes its candidate ids
  into that module's contract rather than joining. `IUserDirectory.SearchAsync` is the
  pattern: the ids go to Identity, the matching and ordering happen where the text is, and
  one page comes back.
- A contract's default implementation fails closed when it decides access, and is
  permissive when it answers "does anything reference this" — a missing module means
  nothing references a team, not that teams are undeletable.

To add a module, copy the structure of `backend/src/Modules/WorkItems`, register it in
`Api/Program.cs` (and `Workers/Program.cs` if it handles events), add it to `Aictiq.slnx`
and `MigrationRunner`, and mirror the integration tests under `tests/IntegrationTests`.

## Tenancy

- **Every tenant table derives from `TenantEntity`.** That is the whole opt-in: module
  contexts find them by base type and apply the `organization_id` filter automatically, so
  a new table cannot ship without isolation because someone forgot a `HasQueryFilter` call.
  Composite uniqueness on such a table always includes `OrganizationId`.
- **No tenant means no rows, never all rows.** `ICurrentTenant.OrganizationId` is null until
  the API's tenant middleware sets it (or background work opens an
  `AmbientCurrentTenant.Use(orgId)` scope), and the filter then matches nothing. Code that
  forgets to establish a tenant fails closed.
- `SaveChangesAsync` stamps the tenant on new rows and **throws** on a write that names a
  different organization than the one in scope.
- `IgnoreQueryFilters()` is the only way past it, and it is rare, deliberate and greppable.
  The sanctioned uses are the ones that *decide* the tenant or span every tenant: the
  membership check the tenant middleware runs before a tenant exists, `GET /orgs`, the
  invitation-token preview, and the runner secret lookup. Each carries a user-id or
  unique-token predicate — that, not the filter, is what keeps it safe.
- Postgres row-level security is the second guard, for the runtime application role.

## Authorization

- **404 for what you cannot see, 403 only for what you can.** `RequireOrgRole` /
  `RequireProjectRole` answer **404** when the caller is not a member (a 403 would confirm
  the organization or project exists) and **403** only once membership is established but
  the role or token scope is too low. The same rule applies to individual records.
- **Two role ladders meet in `ProjectAccessRules.Effective`, and nowhere else.** An
  organization Owner/Admin is implicitly a project Admin; an organization-visible project
  implicitly admits every member; an explicit project membership is the *better* of the two,
  never the lesser; and an organization Guest is never more than a project Guest.
- **Roles are managed strictly downwards.** An Admin may act on people below Admin and
  assign roles below Admin, so only an Owner grants Owner. Owner is not an invitable role.
- **Operating the AI factory is a flag, not a rank.** `organization_members.can_operate_factory`
  says who may start, cancel and watch runs. `MembershipRules.CanOperateFactory` is the only
  reader; ask `IProjectAccess.CanOperateFactoryAsync`, which fails closed, or put
  `RequireFactoryOperator()` beside the role filter. A non-member still gets 404; a member
  who may not operate gets **403 `factory-not-permitted`**.
- **Archived is read-only, not hidden.** `RequireProjectWritable` sits beside the role check
  on every write endpoint and answers **409 `project-archived`** — a conflict rather than a
  403, because the caller's permissions are fine. Item-scoped routes use
  `RequireItemProjectWritable`, which resolves the project from the key's prefix.
- **Ids in a request body are requests, not grants.** An `assigneeId` must be a project
  member, a `teamId` a team of the item's project, a `sprintId` a sprint of that team, a
  state id one of that workflow's own. The tenant filter only keeps a foreign id inside the
  organization; these checks keep it inside the project.
- **MCP tools obey the REST rules, not weaker ones.** A project is resolved through
  `IProjectAccess`, never by finding an item that happens to carry the key, and
  `McpRequestGate` runs for `resources/read` exactly as for tool calls.

## Credentials

- **One `Authorization` header, two kinds of credential.** The policy scheme authenticates
  nothing itself: it forwards an `aiq_` bearer to the PAT handler and everything else to JWT
  (which falls back to the cookie). Both build the *same principal shape*, so no endpoint,
  filter or audit row knows which one it was. Claim names live in
  `SharedKernel/Authorization/PrincipalClaims.cs`.
- **Scopes narrow, never grant.** An empty scope set is a browser session and means
  unscoped; a non-empty one must contain the required scope (or `admin`). Creating a token
  needs `admin`, so a leaked read-only token is not a leaked everything token. A token may
  also be bound to one organization, and is then refused against any other.
- **A token's secret exists once.** Only its SHA-256 is stored, with an 8-character prefix
  in the clear so a person can tell two tokens apart. `TokenHash` is on the auditing
  interceptor's sensitive list. `last_used_at` is throttled in the `WHERE` clause, not in an
  `if`. A revoked or expired token answers **401 `token-revoked`** — distinct, because an
  agent looping on one needs to be told to stop.
- **An agent never mints its own credentials.** `/me/tokens` refuses an agent principal; its
  tokens come from `/orgs/{slug}/agents/{id}/tokens`, always organization-bound and always
  listed where its owner can revoke them. Setting a password or changing an email needs the
  `admin` scope for the same reason: it mints a credential.
- **A runner is a machine, not an actor.** Its `jrn_` secret is hashed like a PAT and
  authenticates **only under `/api/v1/runner`**. The principal has no `sub`, so a membership
  check sees a non-member, and its single scope is `runner`.
- **A mailed link is a hash and a compare-and-swap.** Password reset, email change and
  invitations store only the SHA-256 of the link and spend it with one conditional
  `UPDATE ... WHERE used_at IS NULL AND expires_at > now()`, so two clicks produce one change
  and one refusal. Resending mints a new token and retires the old one, because there is no
  old one to send.
- **`/auth/forgot` answers the same either way,** and a test asserts exactly that.
- **An unverified provider email proves nothing.** OAuth sign-in never uses an address the
  provider will not vouch for, and never links a verified address to a local account that
  never confirmed its own.
- **The SPA's tokens are invisible to JS.** They live in httpOnly cookies the API sets.
  Cookie-authenticated writes carry `X-Aictiq-Request: 1`; bearer writes must not. There is
  no CORS configuration and none should be added.

## Integrity and concurrency

- **Integrity lives in the database.** A new invariant is a check constraint or a filtered
  unique index, not only endpoint validation. The endpoint validates for good error
  messages; the constraint is the guarantee. Unique violations, FK violations and
  `DbUpdateConcurrencyException` become 409s in `GlobalExceptionHandler`, and signal names
  live in `SharedKernel/Persistence/DatabaseSignals.cs` so a migration and its handler
  cannot drift apart.
- **State transitions are compare-and-swap, never read-check-write.** A read followed by a
  conditional write is a race the database cannot see. Spending a refresh token, claiming a
  queued email, claiming a run and heartbeating are all single conditional statements.
- **`uint Version` (xmin) round-trips** through the API on every update; clients echo it back
  or the write 409s. A membership role and a runner heartbeat are deliberate exceptions.
- **An organization always has an Owner,** guaranteed by `tenancy.ensure_org_has_owner()`,
  which takes `FOR UPDATE` on the organization row — two Owners demoting each other at the
  same instant each read a database in which the other is still an Owner, so no application
  check would catch it.
- **One item has at most one live run.** `ux_runs_item_live` is the guarantee; dispatch
  claims the item through `IWorkItemClaims` first and 409s `item-claimed` without a row.
  After that claim, `RunFinished` is the only write path back to the item.

## Events

- Entities `Raise(...)` events. `SaveChangesAsync` writes `IIntegrationEvent`s to the outbox
  in the same transaction and dispatches plain domain events in-process after commit.
  Synchronous `SaveChanges` throws.
- **Outbox delivery is at-least-once,** so every integration-event handler is idempotent —
  and the idempotency is a database constraint, not a check in the handler, which is just a
  narrower race. `DomainEvent.EventId` is the key to write against: it travels in the
  payload and *is* the outbox row's primary key.
- A message that fails ten times is dead-lettered: never retried, logged at Critical,
  counted, and reported Degraded by `/health/ready`. Alert on that.
- **Realtime from an integration-event handler runs in Workers,** which publish over
  `NOTIFY aictiq_rt`; the API's backplane listener forwards to the hub. Only plain domain
  events belong in `Api/Realtime`.
- **Webhook fan-out is scoped by the event's `OrganizationId`,** and refuses to fan out an
  event without one.

## Deletion and retention

- **Items, projects and organizations are hard-deleted, and nothing is left behind.** The
  owning module deletes its rows in the request's transaction and commits one event from
  `SharedKernel/Contracts/DeletionEvents.cs` with them; every other module removes what it
  keeps in a handler. Stored objects go by prefix, objects before rows, so a failed handler
  retries against rows that still exist. `HardDeleteTests` scans every schema for leftovers,
  so a new table that holds a project's data must be added to its module's purge.
- What survives is deliberate and content-free: one `(deleted)` audit row per deleted
  record, and a deleted organization's slug in `tenancy.retired_slugs`.
- **Append-only tables stay append-only.** `audit.audit_log`, `work.item_history`,
  `work.comment_revisions` and `wiki.page_revisions` reject UPDATE always and DELETE except
  while the purge function that owns them holds its transaction-local flag.
- **Unbounded tables get a retention policy.** `RetentionCleanupService` prunes spent refresh
  tokens, aged audit rows and processed outbox rows; a module that owns its own unbounded
  table prunes it itself. Dead-lettered outbox rows and failed emails are never collected —
  they are open incidents, not history.

## HTTP surface

- Minimal APIs, RFC 9457 `problem+json` for every error, `/api/v1` for everything.
- **Enums cross the wire as camelCase names, never numbers.** The same API is the CLI's and
  the MCP server's surface, where `"role": 0` is not something an agent — or a person reading
  a log — can act on.
- **A project's key and an organization's slug are permanent while they exist.** They are in
  every item id a team quotes and every link it has bookmarked. `PATCH` has no `key` field,
  and a deleted organization's slug is retired rather than released.
- **Attachments pass through the API; avatars do not.** Attachments are re-encoded and
  written row-first, object-second. Avatars use presigned URLs signed for the host the
  *browser* uses, and the commit — not the presign — is the guard: it HEADs the object,
  refuses anything oversized or outside the caller's own prefix, and deletes what it refused.
- **Outbound HTTP the server makes for a user goes through `PublicNetworkGuard`**: public
  addresses only, checked at connect time, and redirects are never followed.
- **Server-rendered Markdown is `DisableHtml()`**, and CSV cells go through
  `CsvCell.Escape`, which also makes a leading `=`, `+`, `-` or `@` inert.
- **Email is optional and never on a request path.** A module raises `SendEmailRequested`
  through the outbox; an instance with no SMTP host is a supported deployment. Never
  `[Required]` those options.
- **`/health/ready` publishes only what opted in.** A check's description reaches the body
  only if its registration is tagged `public`.
- **Only the API migrates,** under a Postgres advisory lock, Identity first because it owns
  the shared infrastructure tables. Raw SQL uses snake_case names.

## Frontend

`frontend-vue/` is a plain Vite + Vue 3 SPA — no SSR, no BFF — same-origin with the API.

- **All data goes through `src/utils/api.ts`.** `apiFetch` prefixes `/api/v1`, sends the
  credentials and the CSRF header, and normalises problem+json into `ApiError` /
  `ValidationError` / `ConflictError`. Do not call `fetch` directly. The one sanctioned
  exception is the presigned avatar PUT, which must receive no cookie and no custom header.
- **The token refresh is single-flight and must stay that way.** The API revokes a whole
  refresh-token family when a spent token is replayed, so parallel refreshes would sign the
  user out rather than keep them in.
- Session state lives only in `stores/session.ts`, and `load()` is single-flight.
- **Design tokens, never literal colours.** A hex value in a component is a bug — it will be
  wrong in one of the two themes. Fonts are self-hosted; the CSP blocks a Google Fonts link.
- `src/components/ui/**` is vendored by the shadcn-vue CLI and is regenerated, not
  hand-edited. `src/components/common/**` is the shared vocabulary.
- **`v-html` has exactly one sanctioned use**: `components/common/Markdown.vue`, whose input
  goes through markdown-it with `html: false` and then DOMPurify. DOMPurify silently no-ops
  under happy-dom, so sanitisation tests run under jsdom.
- Shortcuts never fire while someone is typing unless the binding opts in.
- A 409 means someone else saved first: refetch and show the server's state, never retry the
  write with the stale version.

## Tests

Integration tests boot the real API with `WebApplicationFactory` and a Testcontainers
Postgres, one database per test class, and talk HTTP only. Data-integrity tests bypass the
app with raw SQL on purpose — they prove the database enforces the invariants. Add a test
for any changed authorization, persistence or external-boundary behaviour.

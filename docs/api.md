# Aictiq REST API

The versioned public API lives under `/api/v1`. Its machine-readable contract is
`/openapi/v1.json`; the interactive [Scalar](https://scalar.com/) reference is `/docs`.
The reference is the source of truth for endpoint-specific fields and examples. This guide
explains the conventions that apply across the API.

## Authentication

Browser clients use the `aictiq.at` httpOnly session cookie. A state-changing cookie request
must also send `X-Aictiq-Request: 1`; the API rejects requests without it to protect against
cross-site request forgery. Do not try to read, copy, or place the cookie in JavaScript.

Automation should create a personal access token in **Settings → Access tokens**, then send it
on every request:

```sh
curl https://aictiq.example.com/api/v1/orgs/acme/projects \
  -H "Authorization: Bearer $AICTIQ_TOKEN"
```

PAT scopes (`read`, `write`, `admin`, and `mcp`) and an optional organization binding are
enforced in addition to membership and project roles. Use the narrowest scope practical. A
token for one organization cannot be used to discover another organization.

## Filtering, search, and pagination

List endpoints document their available query parameters. Work-item `filter` is space-separated
and all terms must match. Supported terms include `state:active,resolved`, `type:task`,
`priority:>=high`, `assignee:@me`, `assignee:none`, `label:bug,frontend` (any),
`label:bug+frontend` (all), `-label:wontfix`, `sprint:current`, `blocked:true`,
`estimate:>4`, `remaining:none`, and `claimed:any|none|@me|@agent|<userId>`. Use `q` for
free-text search and `sort` only from the values documented by that endpoint.

Paged endpoints use one-based `page` and `pageSize` query parameters and return a
`PagedResult` with the current page, page size, total count, and items. Page size is clamped to
the endpoint's documented maximum (normally 100). Do not infer more pages from item count: use
the returned total and request the next page only while it remains in range.

## Concurrency

Mutable resources return a numeric `version`. Echo the most recently read version in every
`PATCH`, `PUT`, transition, and claim request. A `409 Conflict` means another writer won first:
read the resource again, reconcile intentionally, then retry. Never retry a stale write blindly.

## Rate limits

The default budget is 300 requests per minute per IP address, or per PAT when a bearer token is
present. Authentication routes are limited to 10 requests per minute per IP. Installations may
configure different limits. A rejected request returns `429`; wait for the next one-minute window
before retrying and use bounded exponential backoff for transient failures.

## Errors

All errors use `application/problem+json` (RFC 9457). The response contains `type`, `title`,
`status`, `detail` when safe to disclose, and `traceId`; validation errors also contain an
`errors` object keyed by field name. Preserve the trace ID when asking for support.

| Status | Meaning | Client action |
| --- | --- | --- |
| 400 / 422 | Invalid request or validation failed | Correct the supplied fields. |
| 401 | Missing, expired, or invalid credentials | Re-authenticate or replace the token. |
| 403 | Missing scope, role, or organization access | Request the necessary access; do not retry. |
| 404 | Resource absent or intentionally hidden | Treat it as unavailable. |
| 409 | Stale `version` or domain conflict | Re-read and reconcile before retrying. |
| 429 | Rate budget exhausted | Back off until the next window. |
| 500 | Unexpected server error | Retry only if the operation is safe; include `traceId` in a report. |

## Versioning and deprecation

`v1` is additive: we may add endpoints, optional request fields, response fields, enum values,
and new documented capabilities without a URL-version change. We do not rename or remove fields,
change a field's meaning or type, make an optional input required, or alter existing success/error
semantics within v1.

Before a future breaking change, Aictiq will publish a new major API version and leave the old one
available for a documented migration window. Deprecated endpoints and fields are marked in the
OpenAPI document and responses carry `Deprecation: true` plus a `Sunset` date and `Link` header
to migration guidance where applicable. Clients should tolerate unknown response fields and enum
values, and should surface deprecation notices during development rather than waiting for sunset.

## Notification destinations

`GET /api/v1/me/notifications` includes `organizationId` on each entry. Resolve this ID
using the caller’s organizations from `GET /api/v1/orgs`, and combine its slug with the
entry’s `itemKey` to link to `/o/{slug}/p/{projectKey}/items/{itemKey}`. Derive the project
key from the item key’s prefix before the final hyphen. Use `/inbox` for entries without
item context or when the organization is no longer accessible.

Run-completion updates also include an optional `runId`. When the notification’s
organization grants `canOperateFactory`, link to `/o/{slug}/factory/runs/{runId}`;
otherwise open the related item. Older notifications have no run ID.

## Notification preferences and chat channels

`GET`/`PUT /api/v1/me/notification-preferences` exchange one entry per kind:

```json
{ "kind": "runFailed", "inApp": true, "email": "off",
  "telegram": "immediate", "slack": "digest", "discord": null }
```

Kinds: `assigned`, `mentioned`, `commented`, `transitioned`, `claimed`, `sprintStarted`,
`sprintCompleted`, `wikiMentioned`, `inviteAccepted`, `replied`, `reacted`,
`runSucceeded`, `runFailed`, `runNeedsInput`. Modes are `off`, `immediate` and `digest`.
A `null` chat mode follows the organization default and then `email`. `PUT` writes every
field of each entry it sends, so a client that omits the chat fields clears them back to
`null`. With `inApp: false`, the kind is not sent on any channel.

Personal channels, at most one per platform:

| Method | Path | |
|---|---|---|
| `GET` | `/api/v1/me/notification-channels` | `{ telegramAvailable, telegramBotUsername, channels }` |
| `POST` | `/api/v1/me/notification-channels` | `{ "type": "slack" \| "discord", "webhookUrl": "…" }` or `{ "type": "telegram" }`. Replaces an existing channel of that type. |
| `POST` | `/api/v1/me/notification-channels/{id}/test` | Sends a test message now: `200 { ok: true }` or `422 { error }`. A success clears `broken`. |
| `DELETE` | `/api/v1/me/notification-channels/{id}` | `204`. |

A channel is `{ id, type, status, target, lastError, connectedAt, createdAt }`. `status` is
`pending` (a Telegram channel waiting for its code), `active` or `broken`. `target` is
masked, for example `hooks.slack.com/…a1b2`. The webhook URL or chat id is never
returned. `POST` responds with `{ channel, connectCode, connectUrl, expiresAt }`; the
code and its `t.me` link are only returned for Telegram, and only in that response.
Slack URLs must start with `https://hooks.slack.com/services/` and Discord URLs with
`https://discord.com/api/webhooks/`. Other URLs get `400` with `errors.webhookUrl`.
Telegram gets `400` when `telegramAvailable` is false.

Organization channels and defaults (org admins; `read` scope for `GET`, `admin` for writes):

| Method | Path | |
|---|---|---|
| `GET` | `/api/v1/orgs/{slug}/notification-channels` | `{ telegramAvailable, telegramBotUsername, kinds, channels }`. `kinds` lists the org-wide kinds. |
| `POST` | `/api/v1/orgs/{slug}/notification-channels` | `{ type, name, webhookUrl? }`. The Telegram link is a `startgroup` link. |
| `PATCH` | `/api/v1/orgs/{slug}/notification-channels/{id}` | `{ name?, modes?: { "<kind>": "<mode>" } }`. Only org-wide kinds are accepted; a missing kind is `off`. |
| `POST` | `/api/v1/orgs/{slug}/notification-channels/{id}/test` | As above. |
| `DELETE` | `/api/v1/orgs/{slug}/notification-channels/{id}` | `204`. |
| `GET`/`PUT` | `/api/v1/orgs/{slug}/notification-defaults` | `[{ kind, email, telegram, slack, discord }]` with nullable modes. `PUT { "defaults": [...] }` replaces the set. Kinds left out, or with every mode `null`, have no default. |

The org-wide kinds are `transitioned`, `sprintStarted`, `sprintCompleted`, `runSucceeded`,
`runFailed` and `runNeedsInput`.

## Comment reactions

Comments and replies support the fixed set 👍 👎 ❤️ 🎉 👀 ✅. Use
`PUT /api/v1/orgs/{slug}/items/{key}/comments/{commentId}/reactions` with
`{"emoji":"👍"}` to add a reaction, and
`DELETE /api/v1/orgs/{slug}/items/{key}/comments/{commentId}/reactions/{emoji}`
with a URL-encoded emoji to remove it. Both operations are idempotent and require the
same item visibility and `write` scope as commenting; archived projects are read-only.
These operations do not require a version. Guest roles can comment and react.

The comments response includes `reactions`, with `emoji`, `count`, `reactedByMe`, and
`users` (reactor IDs, display names, avatar keys, and agent flags). Removed reactions
are excluded. Each comment also has `canReact`, false for read-only credentials,
archived projects, and deleted comments. A user can add multiple different emojis,
each once per comment.
Deleting a comment clears its reactions, including its internal toggle history.

The first addition of each emoji by another user sends the comment author a `reacted`
Inbox notification and email. Removing or re-adding that same reaction sends nothing;
self-reactions never notify. Notification preferences accept `reacted`: disabling Inbox
suppresses both channels, and Email Off suppresses email independently. Immediate and
daily digest email modes use the existing delivery settings. No reaction commands are
added to the CLI or MCP tools.

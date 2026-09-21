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

# Outgoing webhooks

Organization administrators can create outgoing webhook subscriptions at
`/api/v1/orgs/{orgSlug}/webhooks`. A subscription may apply to the whole organization or
one project and selects one or more of these events:

`item.created`, `item.updated`, `item.transitioned`, `item.commented`,
`sprint.started`, `sprint.completed`, `wiki.page.updated`, and `agent.claimed`.

The creation response contains a randomly generated secret exactly once. Store it at the
receiver. Aictiq retains an encrypted delivery copy and a SHA-256 audit hash, never returns
the plaintext again.

Every delivery is an HTTPS `POST` with JSON and these headers:

| Header | Value |
| --- | --- |
| `X-Aictiq-Signature` | `sha256=` followed by the lowercase HMAC-SHA-256 of the exact request body, keyed by the subscription secret |
| `X-Aictiq-Event` | selected event name |
| `X-Aictiq-Delivery` | stable UUID for this delivery, suitable for receiver-side deduplication |

The body has this envelope. `data` is the event record and includes the item key, project
identifier, actor, changed fields, and item version whenever those apply to that event.

```json
{
  "id": "b738bd9a-dcae-4b30-9881-e8860f457d1e",
  "event": "item.updated",
  "occurredAt": "2026-09-06T10:00:00Z",
  "data": {
    "projectId": "d7c1d22b-cf27-49e1-b155-6259dd136c3e",
    "key": "ENG-42",
    "actorId": "user_123",
    "changedFields": ["title"]
  }
}
```

Receivers should calculate the HMAC over the raw bytes before parsing JSON and compare it in
constant time. Aictiq uses a 10-second request timeout, retries failures with exponential
backoff for up to eight attempts, and disables a subscription after 50 consecutive failures.
Private and local destination networks are rejected unless the self-hosted administrator sets
`Webhooks:AllowPrivateNetworks=true`. Administrators can inspect excerpts, send a test, and
redeliver from the delivery log endpoints.

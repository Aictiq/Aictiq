# Security model

This is Aictiq's threat model for operators and contributors. It describes the
boundaries the application enforces, the assumptions it makes, and the places
where a deployment must supply its own controls. It complements
[`SECURITY.md`](../SECURITY.md), which records the implemented controls and the
process for reporting a vulnerability.

## Assets and trust boundaries

The primary assets are organization data, user sessions and personal access
tokens (PATs), factory runner and per-run credentials, webhook and OAuth
credentials, and objects stored in S3 compatible storage. The browser SPA,
REST clients, MCP clients, factory runners and harnesses, configured identity
providers, webhook receivers, object storage, and SMTP relay are all separate
trust boundaries. Content created by a member, including descriptions,
comments, wiki pages, playbooks, CSV imports, link previews, and MCP resources,
is untrusted input.

Aictiq is a multi-tenant application. A request is authorized by its identity,
organization membership, project role, PAT scopes, and, where relevant, the
organization to which a PAT is bound. A route must not use an identifier alone
as authorization.

## Threats and controls

| Threat | Controls in Aictiq | Residual risk / operator action |
| --- | --- | --- |
| Cross-tenant reads or writes | Tenant-scoped entities fail closed without an organization; membership and project-role filters are applied before record lookup; forbidden resources normally return 404. Database RLS is an additional deployment defence once enabled. | Review every new `IgnoreQueryFilters()` use and add a cross-tenant regression test for every identifier-based endpoint. Give the application a least-privileged database role. |
| Stolen PAT or session | PAT secrets are shown once and stored hashed; tokens have scopes, optional organization binding, expiry, revocation, and token-partitioned rate limits. Refresh tokens rotate and reuse revokes the token family. Browser tokens are httpOnly cookies with CSRF protection. | Use short expirations and narrow scopes, revoke suspect tokens immediately, and keep TLS enabled. A stolen valid bearer token remains usable until expiry or revocation. |
| Agent prompt injection | MCP labels returned item, comment, wiki, and linked content as user-controlled data. Agents have scoped, organization-bound PATs and cannot create credentials for themselves. | The host running an agent must treat all project content as data too. Do not grant broad write/admin scopes merely to let an agent summarize content; require a human review for destructive actions. |
| Compromised factory runner or prompt | A runner credential authenticates only under `/api/v1/runner/*`; a separate agent token is minted only after a run is assigned and is revoked on every terminal path. Playbooks are permissioned wiki pages, and item content fetched over MCP is marked as user-controlled data. | A runner is not a sandbox. Dedicate its machine to one trusted organization, minimize the operating-system user's credentials, keep secrets out of playbooks, and review agent pull requests before merge or deployment. |
| Webhook or link-preview SSRF | URLs must be public HTTP(S); private, loopback, link-local and multicast addresses are rejected. The destination is checked again when a socket opens, redirects are not followed, and webhook delivery has a short timeout. | Keep `Webhooks:AllowPrivateNetworks=false` unless there is a deliberately isolated private receiver. DNS and outbound-firewall policy remain operator controls. |
| Upload abuse or active content | Attachments are posted to the API, which enforces type and size, decodes every image (rejecting files that are not one, and headers claiming over `Attachments:MaxImagePixels` before decoding) and stores a re-encoded WebP without metadata; they are served from an authenticated route with `nosniff`, inline only for images. Avatars use short-lived presigned URLs and the API verifies the committed object's key, type, and size. Allowlists exclude SVG. Uncommitted objects are cleaned up. | Configure storage with private buckets and a separate public/download origin. Consider malware scanning in the storage event path for deployments that permit untrusted office documents or archives. |
| Stored XSS and unsafe URLs | Work-item Markdown disables raw HTML and strips active link attributes while restricting rendered images to same-origin attachment URLs; wiki Markdown disables raw HTML. The SPA has a restrictive CSP, `nosniff`, frame denial, referrer policy, and permissions policy. | CSP does not sanitize data served directly from object storage. Keep user uploads on a non-cookie origin and do not add SVG to upload allowlists without dedicated sanitization. |
| Automated sign-ups, credential stuffing, mail bombing | Password endpoints are rate-limited per address and lock an account after repeated failures. New password accounts must confirm their address by mail before they can sign in (invitations accepted from the invited address are already confirmed). With Cloudflare Turnstile configured, sign-in, sign-up, password reset and resend-confirmation require a token the API verifies with Cloudflare before doing any work, failing closed if Cloudflare is unreachable. Resending a confirmation is capped at one mail a minute per account. | Configure Turnstile (`TURNSTILE_SITE_KEY` / `TURNSTILE_SECRET_KEY`) on any instance reachable from the internet, and email so that confirmation is enforced. Unconfirmed accounts are never deleted automatically; review them if sign-up abuse is suspected. |
| OAuth account takeover/linking | External identities are keyed by provider subject, state is protected by the framework, and an existing local account requires an authenticated, explicit linking flow rather than matching an email silently. | Register exact redirect URIs at Google/GitHub and protect provider client secrets in the platform secret store. Review provider-side audit logs after a suspected takeover. |
| Secret disclosure | Production credentials are configuration/secret-store values, not repository defaults; required storage and JWT configuration validate on startup. The gitleaks pre-commit hook scans staged changes; CI dependency and CodeQL gates catch known package and static-analysis findings. | Rotate a secret immediately if it ever reaches a log, issue, build artifact, or git history. Removing it from the current commit does not invalidate it. |

## Factory runners and prompts

One runner is one trust domain. Aictiq intentionally starts Claude Code, Codex,
or OpenCode as the runner's operating-system user without an additional sandbox
or approval boundary. The harness can reach that user's repositories, network,
harness sign-in, git credentials, and other readable files. Run it on a machine
or VM dedicated to one organization, under an unprivileged account, and do not
mix mutually untrusted organizations on the same runner.

The two factory credentials have deliberately different reach:

- The `jrn_` runner secret creates a principal with `typ=runner`, its organization
  and runner id, the single `runner` scope, and no user `sub`. It is accepted only
  under `/api/v1/runner/*`; it cannot call user, organization, project, other REST,
  or MCP surfaces. Disabling, deleting, or rotating the runner invalidates it.
- After a runner claims a queued run, Aictiq mints an organization-bound PAT for
  that run's agent with `read`, `write`, and `mcp` scopes. It is passed through the
  harness process environment rather than written to the workspace, revoked on
  success, failure, cancellation, timeout, or runner loss, and expires on its own
  at the playbook limit plus ten minutes if revocation cannot complete.

A playbook is prompt text, not trusted code. Its Markdown comes from a versioned
wiki page, and the page's wiki permissions are its access control for a manually
started run. The fixed run prompt identifies the item and directs the harness to
read it with `get_item`; descriptions, comments, linked text, and wiki content
remain user-controlled input. MCP encloses that material in explicit
`AICTIQ_USER_CONTROLLED_CONTENT` boundaries, and the composed prompt tells the
agent to treat it as data rather than authority. Content inside those boundaries
cannot authorize a new secret, tool call, deployment, or change of task.

Run logs are factory-operator data because commands and model output may disclose
repository details or other credentials the harness could read. The runner
redacts the runner secret, run token, and GitHub installation token it knows, but
that is not a general secret scanner. Keep the runner account least-privileged
and never put credentials in an item or playbook.

## Rate-limit policy and observability

The global HTTP limiter is per source IP, or per hashed PAT for bearer requests.
Authentication routes use a tighter per-IP limit; MCP has a transport limit per
PAT plus a lower per-tool-call limit; attachment upload/commit and search have a
second post-authentication budget per organization and user. ASP.NET authentication lockout stops repeated password
guesses after five failures for fifteen minutes. The application exports MCP,
mail, outbox, and `identity.login.lockouts` counters through OpenTelemetry;
operators should alert on a rise in HTTP 429 responses, lockouts, dead-lettered
outbox messages, and webhook delivery failures.

Rate limits are abuse controls, not authorization. Reverse-proxy deployments
must set `ReverseProxy:KnownProxies` or all clients will appear to be the proxy,
which makes the per-IP policy both inaccurate and easy to exhaust.

## Security review expectations

Before merging a change that crosses a trust boundary, reviewers should verify:

- a tenant, user, project, or ownership predicate exists at the data access point;
- untrusted content is encoded/sanitized for its output context;
- new outbound URLs, redirects, file types, or credentials have an explicit policy;
- sensitive values cannot reach API responses, audit records, logs, fixtures, or docs;
- an authorization or abuse-regression test covers the new path; and
- dependency, CodeQL, and secret-scanning checks pass.

This model intentionally does not claim to make a compromised host, database
superuser, object-store administrator, or endpoint device safe. Those are
operational trust anchors and require platform hardening, backups, monitoring,
and incident response outside the application.

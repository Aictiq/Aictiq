# Security

What Aictiq actually enforces, and where. Everything below has an integration
test unless marked otherwise.

## Authentication

- **Access tokens**: 15-minute JWTs, HS256, short claim names (`sub`, `email`, `name`,
  `role`) with `MapInboundClaims = false` — the same names on the wire, in the API and
  in the clients. Startup refuses keys shorter than 32 characters.
- **Refresh tokens**: 48 random bytes, stored **only as SHA-256 hashes**
  (`identity.refresh_tokens.token_hash`, unique). Rotation on every use; reusing a
  consumed token revokes the **entire family** (theft containment). 30-day lifetime.
  Rotation is a single conditional `UPDATE ... WHERE used_at IS NULL` — the database
  picks the winner, so two concurrent refreshes can never fork a family and silently
  disable reuse detection.
- **Password policy**: minimum 12 characters, no composition rules (NIST 800-63B).
- **Lockout**: 5 failed attempts → 15 minutes (423 response).
- **No user enumeration**: unknown email and wrong password return byte-identical
  401 problems; registration maps Identity errors onto fields without confirming
  account existence beyond what registration inherently reveals.
- **Deactivation**: `POST /admin/users/{id}/deactivate` revokes every refresh token
  and blocks login.

## Web

The SPA (`frontend-vue/`) is **same-origin with the API**: Vite proxies `/api` in
development, and the API serves the built `dist/` from `wwwroot` in production. There is
no BFF process and no cross-origin request.

- Tokens live in **httpOnly cookies** issued by the API. Browser JavaScript can never
  read them; there is nothing in localStorage.
  - `aictiq.at` — the access JWT. `HttpOnly`, `SameSite=Lax`, path `/`, expires with the
    JWT (15 min). Lax so a top-level link into the app arrives authenticated.
  - `aictiq.rt` — the refresh token. `HttpOnly`, `SameSite=Strict`, path
    `/api/v1/auth/refresh` only, 30 days. Scoping the path means no other request the
    app makes can leak it.
  - `Secure` is set whenever the request is HTTPS or arrived with
    `X-Forwarded-Proto: https`, so a TLS-terminating proxy works without extra config.
- **Mode selection**: the API answers in cookie mode when the request carries
  `X-Aictiq-Request` (only the SPA does), and in bearer mode otherwise, so the CLI and
  MCP clients still get tokens in the body. `?mode=cookie|body` overrides the sniff.
- CSRF: cookie-authenticated non-GET/HEAD/OPTIONS requests must carry
  `X-Aictiq-Request: 1` or the API answers **403** (`csrf-header-missing`). A cross-site
  form post cannot set a custom header, and anything that can is already gated by the
  same-origin policy. Bearer requests are exempt — nothing attaches an `Authorization`
  header automatically.
- **No CORS at all.** The SPA is same-origin; an allowlist would be the only way a
  third-party page could ever reach the API with credentials, so there is none.
- Every call goes through `apiFetch` in `src/utils/api.ts`, which sends
  `credentials: 'include'` and surfaces the API's `problem+json` unchanged as `ApiError`.
- The API sets the CSP on the HTML it serves (`SecurityHeadersMiddleware`); API
  responses get `Cache-Control: no-store` and no CSP. Development relaxations
  (`unsafe-eval`, `ws:` for Vite HMR) live in a separate constant.
- Behind a reverse proxy, configure `ReverseProxy:KnownProxies` so per-IP rate limiting
  sees real clients rather than the proxy's single address.

## API hardening

- Rate limiting: 300 req/min per IP globally, 10/min on `/auth/*` (fixed windows).
- Security headers on every response: `X-Content-Type-Options: nosniff`,
  `X-Frame-Options: DENY`, `Referrer-Policy`, `Cache-Control: no-store` on API routes.
- HSTS + HTTPS redirect in production. No CORS allowlist exists — see "Web" above.
- Errors are RFC 9457 `problem+json` with a `traceId`; unhandled exceptions log
  server-side and return no internals.

## Data integrity (enforced by Postgres, not just the app)

- Check constraints on domain invariants (blank work-item titles, non-negative hours,
  and parent integrity).
- Filtered unique indexes for project defaults and initial workflow states.
- **Append-only audit log**: `audit.audit_log` has a BEFORE UPDATE/DELETE trigger that
  raises — even a compromised app connection cannot rewrite history. Field-level diffs
  are written in the same transaction as the change, with sensitive Identity columns
  (password hash, security stamps, lockout counters) excluded.
- Optimistic concurrency via the `xmin` system column → HTTP 409 on stale writes.
- Ownership scoping: non-admin queries are filtered to the owner; out-of-scope reads
  return **404, not 403**, so the API never confirms a record exists.

## Secrets

- No `.env` files; `.gitignore` blocks them. Aspire secret parameters carry dev
  secrets; production uses the platform's secret store.
- Values that were committed to git history before this overhaul must be treated as
  burned — rotate them; removing files from HEAD does not un-leak history
  (`git filter-repo` if you want history rewritten).

See [`docs/security.md`](docs/security.md) for the threat model, abuse controls,
and reviewer checklist.

## Reporting

Please do not open a public issue for an unpatched vulnerability. Use the
repository's **Security → Report a vulnerability** form (a private GitHub
vulnerability report). If that form is unavailable, use the maintainer contact
published with the release and include a concise reproduction, affected version
or commit, impact, and any suggested mitigation.

We will acknowledge a report within 7 days, provide a status update at least
every 14 days while it is being investigated, and coordinate a fix and public
disclosure with the reporter. Please give maintainers reasonable time to ship a
fix before disclosing details publicly. Do not access, modify, or exfiltrate
other users' data while demonstrating a flaw.

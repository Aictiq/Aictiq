// Shared configuration and helpers for the Aictiq k6 perf scripts.
//
// Every script imports from here: base URL, org slug, admin credentials, the seeded
// project keys, the thresholds factory and the JSON summary writer. Plain JS, k6 v0.5x+,
// no imports beyond k6's own modules.
//
// Environment (all optional unless noted):
//   BASE_URL          default http://localhost:8080 - the compose stack via Caddy
//   ORG_SLUG          default perf - the organization aictiq-seed-perf creates
//   ADMIN_EMAIL       default admin@example.com - the seeded admin (Seed:AdminEmail)
//   ADMIN_PASSWORD    required - Seed:AdminPassword; scripts abort in setup() without it
//   RESULTS_DIR       default perf/results - where <name>-summary.json lands (must exist)
//   <NAME>_RATE       per-script arrival rate override (e.g. LIST_RATE=10)

import http from 'k6/http';
import { check } from 'k6';

export const BASE_URL = (__ENV.BASE_URL || 'http://localhost:8080').replace(/\/+$/, '');
export const ORG_SLUG = __ENV.ORG_SLUG || 'perf';
export const ADMIN_EMAIL = __ENV.ADMIN_EMAIL || 'admin@example.com';
export const ADMIN_PASSWORD = __ENV.ADMIN_PASSWORD || '';

export const API = `${BASE_URL}/api/v1`;
export const MCP_URL = `${BASE_URL}/mcp`;

// aictiq-seed-perf: 20 projects keyed P01..P20, 5000 items each, 3 sprints and one
// default team per project.
export const PROJECT_KEYS = Array.from({ length: 20 }, (_, i) => `P${String(i + 1).padStart(2, '0')}`);
export const ITEMS_PER_PROJECT = 5000;

export function requirePassword() {
  if (!ADMIN_PASSWORD) {
    throw new Error('ADMIN_PASSWORD is required (the Seed:AdminPassword of the compose stack).');
  }
}

// The product budgets: p95 < 200 ms for list/board,
// search < 300 ms. Thresholds are duration-based, but a 429 storm would make latency
// look great while serving nothing, so error rate and checks are guarded too.
export function thresholds({ p95, p99 = null }) {
  const duration = [`p(95)<${p95}`];
  if (p99 !== null) duration.push(`p(99)<${p99}`);
  return {
    http_req_duration: duration,
    http_req_failed: ['rate<0.01'],
    checks: ['rate>0.99'],
  };
}

// Constant-arrival: every iteration is issued at a fixed rate regardless of how fast the
// server answers, which is what makes duration thresholds meaningful. Sized for small CI
// runners - modest rates, VUs preallocated so the ramp is not part of the measurement.
export function arrivalScenario(rate, duration = '60s') {
  const r = typeof rate === 'number' ? rate : Number(rate);
  const vus = Math.max(4, Math.ceil(r * 2));
  return {
    executor: 'constant-arrival-rate',
    rate: r,
    timeUnit: '1s',
    duration,
    preAllocatedVUs: vus,
    maxVUs: vus * 2,
  };
}

// One login per script run (k6 setup() runs once). Returns the session cookies as a
// header string - k6 cookie jars are VU-local, so requests pass this explicitly instead.
// Cookie mode is what a same-origin client gets: the API decides by the X-Aictiq-Request
// header (AuthCookies.UseCookies), which the SPA's apiFetch always sends.
export function login() {
  requirePassword();
  const res = http.post(
    `${API}/auth/login`,
    JSON.stringify({ email: ADMIN_EMAIL, password: ADMIN_PASSWORD }),
    { headers: { 'Content-Type': 'application/json', 'X-Aictiq-Request': '1' } },
  );
  const ok = check(res, {
    'login 200': (r) => r.status === 200,
    'session cookies issued': (r) => Object.keys(r.cookies || {}).length > 0,
  });
  if (!ok) {
    throw new Error(`Login failed with ${res.status}: ${String(res.body || '').slice(0, 200)}`);
  }
  return Object.entries(res.cookies)
    .map(([name, values]) => `${name}=${values[0].value}`)
    .join('; ');
}

export function cookieParams(cookieHeader, extra = {}) {
  return {
    headers: Object.assign({ Cookie: cookieHeader }, extra),
  };
}

// Mints a personal access token for the MCP script. Creating one needs the admin scope,
// so it authenticates with the admin's browser session (which is unscoped) and the CSRF
// header cookie writes require. The token is bound to the perf organization - the /mcp
// endpoint has no {orgSlug} route, so the tenant comes from the token's own binding.
// Returns the plaintext secret (shown exactly once by the API).
export function mintPat(cookieHeader, name = 'k6 perf') {
  const params = cookieParams(cookieHeader, { 'X-Aictiq-Request': '1' });

  const orgsRes = http.get(`${API}/orgs`, params);
  check(orgsRes, { 'orgs 200': (r) => r.status === 200 });
  const orgs = orgsRes.status === 200 ? orgsRes.json() : [];
  const org = (orgs || []).find((o) => o.slug === ORG_SLUG);
  if (!org) {
    const known = (orgs || []).map((o) => o.slug).join(', ') || 'none';
    throw new Error(`Organization '${ORG_SLUG}' not found for the admin (member of: ${known}). Did aictiq-seed-perf run?`);
  }

  const created = http.post(
    `${API}/me/tokens`,
    JSON.stringify({ name, scopes: ['mcp', 'read'], organizationId: org.id }),
    cookieParams(cookieHeader, { 'X-Aictiq-Request': '1', 'Content-Type': 'application/json' }),
  );
  check(created, { 'PAT created': (r) => r.status === 201 });
  const secret = created.status === 201 ? created.json().secret : null;
  if (!secret) {
    throw new Error(`PAT creation failed with ${created.status}: ${String(created.body || '').slice(0, 200)}`);
  }
  return secret;
}

// Deterministic LCG so a run's shape is reproducible (same mix every night) without
// corralling all VUs onto the same project at the same instant.
export function rng(seed) {
  let state = seed % 2147483647;
  if (state <= 0) state += 2147483646;
  return () => {
    state = (state * 16807) % 2147483647;
    return (state - 1) / 2147483646;
  };
}

// handleSummary helper: writes the full end-of-test JSON to <RESULTS_DIR>/<name>-summary
// .json and prints a compact table to stdout. RESULTS_DIR must exist (k6 cannot mkdir);
// the workflow and README create it.
export function writeSummary(name, data) {
  const dir = __ENV.RESULTS_DIR || 'perf/results';
  const m = data.metrics || {};
  const v = (key, sub) => (m[key] && m[key].values && m[key].values[sub]);
  const pct = (rate) => (typeof rate === 'number' ? `${(rate * 100).toFixed(2)}%` : 'n/a');
  const ms = (x) => (typeof x === 'number' ? `${x.toFixed(1)}ms` : 'n/a');
  const stdout = [
    '',
    `${name} - perf results`,
    `  http_reqs:         ${m.http_reqs ? m.http_reqs.values.count : 'n/a'} at ${v('http_reqs', 'rate') ? v('http_reqs', 'rate').toFixed(2) : 'n/a'}/s`,
    `  http_req_duration: avg=${ms(v('http_req_duration', 'avg'))} med=${ms(v('http_req_duration', 'med'))} p95=${ms(v('http_req_duration', 'p(95)'))} p99=${ms(v('http_req_duration', 'p(99)'))}`,
    `  http_req_failed:   ${pct(v('http_req_failed', 'rate'))}`,
    `  checks:            ${pct(v('checks', 'rate'))}`,
    '',
  ].join('\n');
  return {
    stdout,
    [`${dir}/${name}-summary.json`]: JSON.stringify(data, null, 2),
  };
}

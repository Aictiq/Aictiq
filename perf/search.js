// Organization search: the org-wide full-text search the top bar issues.
// Multi-word queries over title+description, mostly against items, sometimes narrowed to
// types or to one project (the per-project variant). The org-level route checks project
// visibility for every project before ranking, so it is the more expensive of the two.
//
// Target: p95 < 300 ms. Run: k6 run perf/search.js

import http from 'k6/http';
import { check } from 'k6';
import exec from 'k6/execution';

import {
  API, ORG_SLUG, PROJECT_KEYS, thresholds, arrivalScenario, login, cookieParams, rng, writeSummary,
} from './lib.js';

export const options = {
  scenarios: {
    main: arrivalScenario(Number(__ENV.SEARCH_RATE || 10)),
  },
  thresholds: thresholds({ p95: 300 }),
  discardResponseBodies: false,
};

// Realistic two-word queries. Most use the seeded vocabulary ("Perf story N", "Perf bug
// N" — the titles aictiq-seed-perf writes) so the full-text ranking and headline path is
// what gets measured; the rest are deliberately miss-matched so the trigram fallback
// keeps some of the budget too.
const QUERIES = [
  'perf story',
  'perf bug 100',
  'perf task',
  'perf feature',
  'perf epic 1',
  'flaky test',
  'login fails',
  'timeout retry',
  'memory leak',
  'rate limit',
];

export function setup() {
  const cookie = login();

  const probe = http.get(
    `${API}/orgs/${ORG_SLUG}/search?q=${encodeURIComponent(QUERIES[0])}`,
    cookieParams(cookie),
  );
  check(probe, {
    'setup: search 200': (r) => r.status === 200,
    'setup: search response shape': (r) => r.status === 200 && r.json() !== null && Array.isArray(r.json().items),
  });
  if (probe.status !== 200) {
    throw new Error(`Search is not answering (${probe.status}).`);
  }

  return { cookie, queries: QUERIES };
}

export default function (data) {
  const rand = rng((exec.vu.idInTest + 1) * 15485863 + exec.scenario.iterationInTest);
  const query = data.queries[Math.floor(rand() * data.queries.length) % data.queries.length];

  let url;
  let tag;
  const roll = rand();
  if (roll < 0.6) {
    url = `${API}/orgs/${ORG_SLUG}/search?q=${encodeURIComponent(query)}`;
    tag = 'org';
  } else if (roll < 0.8) {
    url = `${API}/orgs/${ORG_SLUG}/search?q=${encodeURIComponent(query)}&types=items`;
    tag = 'org-items-only';
  } else {
    const project = PROJECT_KEYS[Math.floor(rand() * PROJECT_KEYS.length) % PROJECT_KEYS.length];
    url = `${API}/orgs/${ORG_SLUG}/projects/${project}/search?q=${encodeURIComponent(query)}`;
    tag = 'project';
  }

  const res = http.get(
    url,
    Object.assign(cookieParams(data.cookie), { tags: { kind: tag } }),
  );
  check(res, {
    'status 200': (r) => r.status === 200,
    'search response shape': (r) => r.status === 200 && Array.isArray(r.json().items) && Array.isArray(r.json().comments),
  });
}

export function handleSummary(data) {
  return writeSummary('search', data);
}

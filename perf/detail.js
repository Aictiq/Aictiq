// Item detail: the single-item view the SPA opens on every card click and the
// one an agent's `get_item` mirrors. Keys are assembled from the seeded space - P01..P20
// × 1..5000 - and rotate pseudo-randomly, so every request is a different row and the
// buffer cache cannot carry the run.
//
// Target: p95 < 200 ms. Run: k6 run perf/detail.js

import http from 'k6/http';
import { check } from 'k6';
import exec from 'k6/execution';

import {
  API, ORG_SLUG, PROJECT_KEYS, ITEMS_PER_PROJECT, thresholds, arrivalScenario, login, cookieParams, rng, writeSummary,
} from './lib.js';

export const options = {
  scenarios: {
    main: arrivalScenario(Number(__ENV.DETAIL_RATE || 10)),
  },
  thresholds: thresholds({ p95: 200 }),
  discardResponseBodies: false,
};

export function setup() {
  const cookie = login();

  const probe = http.get(`${API}/orgs/${ORG_SLUG}/items/P01-1`, cookieParams(cookie));
  check(probe, {
    'setup: detail 200': (r) => r.status === 200,
    'setup: detail returns the item': (r) => r.status === 200 && r.json().key === 'P01-1',
  });
  if (probe.status !== 200) {
    throw new Error(`Item detail is not answering (${probe.status}). Did aictiq-seed-perf run?`);
  }

  return { cookie };
}

export default function (data) {
  const rand = rng((exec.vu.idInTest + 1) * 32452843 + exec.scenario.iterationInTest);
  const project = PROJECT_KEYS[Math.floor(rand() * PROJECT_KEYS.length) % PROJECT_KEYS.length];
  const number = 1 + (Math.floor(rand() * ITEMS_PER_PROJECT) % ITEMS_PER_PROJECT);

  const res = http.get(
    `${API}/orgs/${ORG_SLUG}/items/${project}-${number}`,
    Object.assign(cookieParams(data.cookie), { tags: { kind: 'detail', project } }),
  );
  check(res, {
    'status 200': (r) => r.status === 200,
    'item echoed': (r) => r.status === 200 && r.json().key === `${project}-${number}`,
  });
}

export function handleSummary(data) {
  return writeSummary('detail', data);
}

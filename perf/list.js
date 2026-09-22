// Item list: the SPA's backlog/list view at pageSize=100, mixing the unfiltered
// page with the filter grammar the UI actually issues - state categories, the current
// sprint, a label, and a type+priority+sort combination. Projects are picked
// pseudo-randomly across the seeded P01..P20.
//
// Target: p95 < 200 ms. Run: k6 run perf/list.js

import http from 'k6/http';
import { check } from 'k6';
import exec from 'k6/execution';

import {
  API, ORG_SLUG, PROJECT_KEYS, thresholds, arrivalScenario, login, cookieParams, rng, writeSummary,
} from './lib.js';

export const options = {
  scenarios: {
    main: arrivalScenario(Number(__ENV.LIST_RATE || 20)),
  },
  thresholds: thresholds({ p95: 200, p99: 500 }),
  discardResponseBodies: false,
};

// Probe a couple of plausible seeded label names once; an unknown label is a 400, and the
// mix should not spend its budget on requests the seeder made impossible.
function probeLabel(cookie) {
  const candidates = ['perf', 'bug', 'backend', 'infra'];
  for (const name of candidates) {
    const res = http.get(
      `${API}/orgs/${ORG_SLUG}/projects/P01/items/?filter=label:${encodeURIComponent(name)}&pageSize=1`,
      cookieParams(cookie),
    );
    if (res.status === 200) return name;
  }
  return null;
}

export function setup() {
  const cookie = login();

  const probe = http.get(`${API}/orgs/${ORG_SLUG}/projects/P01/items/?pageSize=100`, cookieParams(cookie));
  check(probe, {
    'setup: list 200': (r) => r.status === 200,
    'setup: list returns items': (r) => r.status === 200 && Array.isArray(r.json().items) && r.json().items.length > 0,
  });
  if (probe.status !== 200) {
    throw new Error(`Seeded list is not answering (${probe.status}). Did aictiq-seed-perf run?`);
  }

  return { cookie, label: probeLabel(cookie) };
}

export default function (data) {
  const rand = rng((exec.vu.idInTest + 1) * 7919 + exec.scenario.iterationInTest);
  const project = PROJECT_KEYS[Math.floor(rand() * PROJECT_KEYS.length) % PROJECT_KEYS.length];

  const roll = rand();
  let path;
  let tag;
  if (roll < 0.4) {
    path = `?pageSize=100`;
    tag = 'unfiltered';
  } else if (roll < 0.65) {
    path = `?filter=state:proposed,active&pageSize=100`;
    tag = 'state';
  } else if (roll < 0.8) {
    path = `?filter=sprint:current&pageSize=100`;
    tag = 'sprint-current';
  } else if (roll < 0.9 && data.label) {
    path = `?filter=label:${encodeURIComponent(data.label)}&pageSize=100`;
    tag = 'label';
  } else {
    path = `?filter=type:bug%20priority:high&pageSize=100&sort=updated`;
    tag = 'type-priority';
  }

  const res = http.get(
    `${API}/orgs/${ORG_SLUG}/projects/${project}/items/${path}`,
    Object.assign(cookieParams(data.cookie), { tags: { kind: tag, project } }),
  );
  check(res, {
    'status 200': (r) => r.status === 200,
    'page of items': (r) => r.status === 200 && Array.isArray(r.json().items),
  });
}

export function handleSummary(data) {
  return writeSummary('list', data);
}

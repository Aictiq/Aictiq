// Team board: the kanban board GET, the SPA's heaviest single page — every
// card of the team comes back grouped by column, so this is the endpoint the 500-card
// budget and the 200 ms product target were written for. Teams rotate across
// the seeded projects.
//
// Target: p95 < 200 ms. Run: k6 run perf/board.js

import http from 'k6/http';
import { check } from 'k6';
import exec from 'k6/execution';

import {
  API, ORG_SLUG, PROJECT_KEYS, thresholds, arrivalScenario, login, cookieParams, rng, writeSummary,
} from './lib.js';

export const options = {
  scenarios: {
    main: arrivalScenario(Number(__ENV.BOARD_RATE || 5)),
  },
  thresholds: thresholds({ p95: 200 }),
  discardResponseBodies: false,
};

function teamsOf(cookie, projectKey) {
  const res = http.get(`${API}/orgs/${ORG_SLUG}/projects/${projectKey}/teams/`, cookieParams(cookie));
  check(res, { [`setup: teams of ${projectKey} 200`]: (r) => r.status === 200 });
  return res.status === 200 ? res.json().map((t) => t.id) : [];
}

export function setup() {
  const cookie = login();

  const teamIds = PROJECT_KEYS.flatMap((key) => teamsOf(cookie, key));
  if (teamIds.length === 0) {
    throw new Error('No teams found in any seeded project. Did aictiq-seed-perf run?');
  }

  const probe = http.get(`${API}/orgs/${ORG_SLUG}/teams/${teamIds[0]}/board`, cookieParams(cookie));
  check(probe, {
    'setup: board 200': (r) => r.status === 200,
    'setup: board has columns': (r) => r.status === 200 && Array.isArray(r.json().columns) && r.json().columns.length > 0,
  });
  if (probe.status !== 200) {
    throw new Error(`Board is not answering (${probe.status}).`);
  }

  return { cookie, teamIds };
}

export default function (data) {
  const rand = rng((exec.vu.idInTest + 1) * 104729 + exec.scenario.iterationInTest);
  const teamId = data.teamIds[Math.floor(rand() * data.teamIds.length) % data.teamIds.length];

  const res = http.get(
    `${API}/orgs/${ORG_SLUG}/teams/${teamId}/board`,
    Object.assign(cookieParams(data.cookie), { tags: { kind: 'board' } }),
  );
  // Body length only: a seeded board payload is megabytes, and parsing it on a small
  // runner would make the load test measure the runner. One board was shape-checked in
  // setup.
  check(res, {
    'status 200': (r) => r.status === 200,
    'board payload arrived': (r) => String(r.body || '').length > 1000,
  });
}

export function handleSummary(data) {
  return writeSummary('board', data);
}

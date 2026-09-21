// MCP `list_ready_work`: the tool an agent loops on to find pick-up work, over
// streamable HTTP with a personal access token. Setup performs the full MCP handshake
// once (initialize, initialized notification, and a validating tools/call probe); the VU
// loop then calls `list_ready_work` with project keys rotating across P01..P20.
//
// Target: p95 < 300 ms.
//
// The shipped stack rate-limits MCP tool calls to 60/min per token
// (RateLimiting:McpPermitLimitPerMinute), so the default arrival rate is 1/s over 50s —
// 50 calls, under the guard, so the script measures the tool, not the limiter. Raise
// MCP_RATE (and the server-side limit) together if you want more than that.
//
// Run: k6 run perf/mcp.js

import http from 'k6/http';
import { check } from 'k6';
import exec from 'k6/execution';

import {
  MCP_URL, PROJECT_KEYS, thresholds, arrivalScenario, login, mintPat, rng, writeSummary,
} from './lib.js';

export const options = {
  scenarios: {
    main: arrivalScenario(Number(__ENV.MCP_RATE || 1), __ENV.MCP_DURATION || '50s'),
  },
  thresholds: thresholds({ p95: 300 }),
  discardResponseBodies: false,
};

const CLIENT = { name: 'aictiq-k6-perf', version: '1.0.0' };
// The C# SDK (2.2.0) speaks this revision; the initialize response names what it agreed to.
const REQUESTED_PROTOCOL_VERSION = '2025-03-26';

function postRpc(token, protocolVersion, body) {
  return http.post(MCP_URL, JSON.stringify(body), {
    headers: {
      'Content-Type': 'application/json',
      'Accept': 'application/json, text/event-stream',
      'Authorization': `Bearer ${token}`,
      'MCP-Protocol-Version': protocolVersion,
    },
    tags: { kind: body.method || 'notification' },
  });
}

// A stateless streamable-HTTP reply is either one JSON document or an SSE stream of
// `data:` lines; take the last JSON-RPC message found.
function parseRpc(res) {
  try {
    return JSON.parse(res.body);
  } catch {
    /* fall through to SSE */
  }
  let message = null;
  for (const line of String(res.body || '').split('\n')) {
    if (!line.startsWith('data:')) continue;
    try {
      message = JSON.parse(line.slice(5).trim());
    } catch {
      /* keep the last good one */
    }
  }
  return message;
}

function listReadyWork(token, protocolVersion, id, project) {
  const res = postRpc(token, protocolVersion, {
    jsonrpc: '2.0',
    id,
    method: 'tools/call',
    params: { name: 'list_ready_work', arguments: { project } },
  });
  const message = parseRpc(res);
  check(res, {
    'status 200': (r) => r.status === 200,
    'json-rpc reply': () => message !== null && typeof message === 'object',
    'no protocol error': () => message !== null && message.error === undefined,
    'tool result': () => message !== null && message.result !== undefined && message.result.isError !== true,
  });
  return message;
}

export function setup() {
  const cookie = login();
  const token = mintPat(cookie, 'k6 perf MCP');

  const initRes = postRpc(token, REQUESTED_PROTOCOL_VERSION, {
    jsonrpc: '2.0',
    id: 0,
    method: 'initialize',
    params: { protocolVersion: REQUESTED_PROTOCOL_VERSION, capabilities: {}, clientInfo: CLIENT },
  });
  const initMessage = parseRpc(initRes);
  check(initRes, {
    'initialize 200': (r) => r.status === 200,
    'initialize result': () => initMessage !== null && initMessage.result !== undefined && initMessage.result.serverInfo !== undefined,
  });
  if (initMessage === null || initMessage.result === undefined) {
    throw new Error(`MCP initialize failed with ${initRes.status}: ${String(initRes.body || '').slice(0, 200)}`);
  }
  const protocolVersion = initMessage.result.protocolVersion || REQUESTED_PROTOCOL_VERSION;

  const notified = postRpc(token, protocolVersion, { jsonrpc: '2.0', method: 'notifications/initialized' });
  check(notified, { 'initialized accepted': (r) => r.status === 202 || r.status === 200 });

  // One real tool call: proves the token's organization binding, scopes and membership
  // all hold before the measured loop begins. Counts toward the per-token tool budget,
  // which is why the loop below stays at 50 calls against the 60/min default.
  const probe = listReadyWork(token, protocolVersion, 1, PROJECT_KEYS[0]);
  if (probe === null || probe.error !== undefined || probe.result === undefined) {
    throw new Error('MCP list_ready_work probe failed — check the PAT scopes and organization binding.');
  }

  return { token, protocolVersion };
}

export default function (data) {
  const rand = rng((exec.vu.idInTest + 1) * 65537 + exec.scenario.iterationInTest);
  const project = PROJECT_KEYS[Math.floor(rand() * PROJECT_KEYS.length) % PROJECT_KEYS.length];
  // The transport is stateless, so the id only has to pair with its own reply.
  const id = exec.scenario.iterationInTest + 1;

  listReadyWork(data.token, data.protocolVersion, id, project);
}

export function handleSummary(data) {
  return writeSummary('mcp', data);
}

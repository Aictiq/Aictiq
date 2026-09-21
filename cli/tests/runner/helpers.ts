import { mkdtempSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import type { ClaimedRun, HarnessAdapter, RunnerHello } from '../../src/runner/types.js'

export const agentToken = 'aiq_agent_secret_0123456789'
export const runnerToken = 'jrn_runner_secret_0123456789'

export const hello: RunnerHello = {
  runnerId: 'r1',
  name: 'vps-1',
  organizationSlug: 'acme',
  heartbeatIntervalSeconds: 60,
  pollTimeoutSeconds: 25,
  maxLogBytes: 8 * 1024 * 1024,
  maxLogBatchBytes: 64 * 1024,
  maxRunMinutes: 720,
}

export function claimedRun(overrides: Partial<ClaimedRun> = {}): ClaimedRun {
  return {
    runId: '0b7e3c1e-6a0f-4d8e-9d55-1f3a2b4c5d6e',
    itemId: 'i1',
    itemKey: 'ACME-42',
    projectId: 'p1',
    projectKey: 'ACME',
    organizationSlug: 'acme',
    harness: 'fake',
    prompt: 'Implement ACME-42',
    playbookRevisionId: null,
    repo: { source: 'local', repoFullName: null, cloneToken: null, localPathHint: null },
    defaultBranch: 'main',
    branchName: 'aictiq/acme-42',
    maxMinutes: 30,
    aictiqUrl: null,
    agentToken,
    agentTokenDisplay: null,
    heartbeatIntervalSeconds: 60,
    ...overrides,
  }
}

/** A harness that is a node script: what it prints and how it exits is the test's to say. */
export function scriptAdapter(script: string, available = true): HarnessAdapter {
  const dir = mkdtempSync(join(tmpdir(), 'aictiq-harness-'))
  const file = join(dir, 'harness.mjs')
  writeFileSync(file, script)
  return {
    name: 'claude',
    available: async () => (available ? { name: 'fake', version: '1.0.0' } : null),
    invocation: (context) => ({ command: process.execPath, args: [file], stdin: context.prompt }),
    parse: (line) =>
      line.startsWith('RESULT ')
        ? { log: null, result: line.slice(7), costUsd: 0.5, inputTokens: 10, outputTokens: 3 }
        : line === 'STEP'
          ? { log: null, accumulate: true, costUsd: 0.25, inputTokens: 5, outputTokens: 2 }
          : { log: line },
    outcome: (exitCode, lastLines, lastResult) =>
      exitCode === 0
        ? { outcome: 'succeeded', summary: lastResult, failureReason: null }
        : {
            outcome: 'failed',
            summary: lastLines.join('\n'),
            failureReason: `harness-exit-${exitCode}`,
          },
  }
}

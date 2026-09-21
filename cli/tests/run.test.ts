import { mkdtempSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { RunLogEntry, RunView } from '../src/api/views.js'
import { CliError, ExitCode } from '../src/errors.js'
import { createProgram } from '../src/program.js'
import { FakeInstance } from './runner/fake-instance.js'

const runId = '7d3b6b2e-1f0a-4c7e-9a3d-5e2f1c0b8a11'
const playbookId = 'a1b2c3d4-0000-4000-8000-000000000001'
const otherPlaybookId = 'a1b2c3d4-0000-4000-8000-000000000002'
const agentId = 'b2c3d4e5-0000-4000-8000-000000000003'

const run: RunView = {
  id: runId,
  projectId: 'c0000000-0000-4000-8000-000000000000',
  itemId: 'd0000000-0000-4000-8000-000000000000',
  itemKey: 'ACME-123',
  playbookId,
  playbookName: 'Implement',
  agentId,
  agentName: 'Worker',
  requestedBy: 'e0000000-0000-4000-8000-000000000000',
  ruleId: null,
  ruleName: null,
  runnerId: null,
  runnerName: null,
  status: 'queued',
  harness: 'claude-code',
  playbookRevisionId: null,
  maxMinutes: 60,
  queuedAt: '2026-09-18T10:00:00Z',
  assignedAt: null,
  startedAt: null,
  finishedAt: null,
  lastHeartbeatAt: null,
  cancelRequested: false,
  outcomeSummary: null,
  pullRequestUrl: null,
  exitCode: null,
  costUsd: null,
  inputTokens: null,
  outputTokens: null,
  failureReason: null,
  promptSnapshot: null,
  version: 1,
}

const playbooks = [
  { id: playbookId, name: 'Implement', isDefault: true },
  { id: otherPlaybookId, name: 'Review', isDefault: false },
]

const agents = [
  { userId: agentId, displayName: 'Worker', isActive: true },
  { userId: 'b2c3d4e5-0000-4000-8000-000000000004', displayName: 'Reviewer', isActive: true },
]

describe('aictiq run', () => {
  let instance: FakeInstance
  let out: string[]

  const invoke = (args: string[]) =>
    createProgram().parseAsync(
      ['--url', instance.url, '--token', 'aiq_secret', '--org', 'acme', ...args],
      { from: 'user' },
    )

  beforeEach(async () => {
    instance = await new FakeInstance().start()
    vi.stubEnv('AICTIQ_CONFIG_HOME', mkdtempSync(join(tmpdir(), 'aictiq-run-')))
    vi.stubEnv('AICTIQ_RUN_POLL_MS', '1')
    out = []
    vi.spyOn(process.stdout, 'write').mockImplementation((chunk) => {
      out.push(String(chunk))
      return true
    })
  })

  afterEach(async () => {
    vi.unstubAllEnvs()
    vi.restoreAllMocks()
    await instance.stop()
  })

  it('start resolves --playbook and --agent by name and posts their ids', async () => {
    instance
      .on((r) =>
        r.path === '/api/v1/orgs/acme/projects/ACME/playbooks'
          ? { status: 200, body: playbooks }
          : undefined,
      )
      .on((r) =>
        r.path === '/api/v1/orgs/acme/agents' ? { status: 200, body: agents } : undefined,
      )
      .on((r) =>
        r.method === 'POST' && r.path === '/api/v1/orgs/acme/items/ACME-123/runs'
          ? { status: 201, body: run }
          : undefined,
      )

    await invoke(['run', 'start', 'acme-123', '--playbook', 'implement', '--agent', 'worker'])

    const posted = instance.to('/items/ACME-123/runs')
    expect(posted).toHaveLength(1)
    expect(posted[0]?.body).toEqual({ playbookId, agentId })
    const text = out.join('')
    expect(text).toContain(runId)
    expect(text).toContain('queued')
    expect(text).toContain('Worker')
    expect(text).toContain('Implement')
  })

  it('start refuses an ambiguous agent name and lists the candidates', async () => {
    instance.on((r) =>
      r.path === '/api/v1/orgs/acme/agents'
        ? {
            status: 200,
            body: [
              ...agents,
              { userId: 'ffffffff-0000-4000-8000-000000000009', displayName: 'worker' },
            ],
          }
        : undefined,
    )

    const failure = await invoke(['run', 'start', 'ACME-123', '--agent', 'Worker']).catch(
      (error: unknown) => error,
    )
    expect(failure).toBeInstanceOf(CliError)
    expect((failure as CliError).exitCode).toBe(ExitCode.Validation)
    expect((failure as CliError).message).toContain(agentId)
    expect((failure as CliError).message).toContain('ffffffff-0000-4000-8000-000000000009')
    expect(instance.to('/runs')).toHaveLength(0)
  })

  it('start on a 409 rejects with exit code 3', async () => {
    instance.on((r) =>
      r.method === 'POST' && r.path === '/api/v1/orgs/acme/items/ACME-123/runs'
        ? {
            status: 409,
            body: {
              type: 'https://aictiq.com/problems/item-claimed',
              title: 'Item already claimed',
              status: 409,
            },
          }
        : undefined,
    )

    const failure = await invoke(['run', 'start', 'ACME-123']).catch((error: unknown) => error)
    expect(failure).toBeInstanceOf(CliError)
    expect((failure as CliError).exitCode).toBe(ExitCode.Conflict)
    expect((failure as CliError).message).toContain('Item already claimed')
    expect(instance.to('/items/ACME-123/runs')[0]?.body).toEqual({})
  })

  it('list renders a table row and forwards the filters', async () => {
    instance.on((r) =>
      r.path === '/api/v1/orgs/acme/runs'
        ? {
            status: 200,
            body: {
              items: [
                {
                  ...run,
                  status: 'succeeded',
                  finishedAt: '2026-09-18T10:30:00Z',
                  pullRequestUrl: 'https://github.com/o/r/pull/7',
                },
              ],
              page: 1,
              pageSize: 25,
              totalCount: 1,
              totalPages: 1,
            },
          }
        : undefined,
    )

    await invoke(['run', 'list', '-p', 'acme', '--status', 'succeeded', '--item', 'acme-123'])

    expect(instance.to('/runs')[0]?.query).toMatchObject({
      project: 'ACME',
      status: 'succeeded',
      item: 'ACME-123',
      page: '1',
      pageSize: '25',
    })
    const lines = out.join('').split('\n')
    expect(lines[0]).toMatch(/^ID\s+ITEM\s+STATUS\s+AGENT\s+PLAYBOOK\s+QUEUED\s+FINISHED\s+PR$/)
    expect(lines[1]).toBe(
      `${runId}  ACME-123  succeeded  Worker  Implement  2026-09-18 10:00:00Z  2026-09-18 10:30:00Z  https://github.com/o/r/pull/7`,
    )
    expect(out.join('')).toContain('Page 1 of 1 · 1 runs')
  })

  it('list refuses an unknown status before asking the API', async () => {
    const failure = await invoke(['run', 'list', '--status', 'done']).catch(
      (error: unknown) => error,
    )
    expect(failure).toBeInstanceOf(CliError)
    expect((failure as CliError).exitCode).toBe(ExitCode.Validation)
    expect(instance.requests).toHaveLength(0)
  })

  it('view prints the fields that are present', async () => {
    instance.on((r) =>
      r.path === `/api/v1/orgs/acme/runs/${runId}`
        ? {
            status: 200,
            body: {
              ...run,
              status: 'failed',
              runnerName: 'build-1',
              startedAt: '2026-09-18T10:01:00Z',
              finishedAt: '2026-09-18T10:20:00Z',
              exitCode: 1,
              costUsd: 0.5,
              inputTokens: 1200,
              outputTokens: 300,
              failureReason: 'tests failed',
            },
          }
        : undefined,
    )

    await invoke(['run', 'view', runId])

    const text = out.join('')
    // Labels pad to the widest one present ("Requested by"), two spaces before the value.
    expect(text).toContain('Status        failed')
    expect(text).toContain('Runner        build-1')
    expect(text).toContain('Exit code     1')
    expect(text).toContain('Cost          $0.5000 · 1200 in / 300 out tokens')
    expect(text).toContain('Failure       tests failed')
    expect(text).not.toContain('Cancel requested')
    expect(text).not.toMatch(/^PR\b/m)
    expect(text).not.toMatch(/^Assigned\b/m)
  })

  it('view names the rule when a run has no requester', async () => {
    instance.on((r) =>
      r.path === `/api/v1/orgs/acme/runs/${runId}`
        ? {
            status: 200,
            body: {
              ...run,
              requestedBy: null,
              ruleId: 'f0000000-0000-4000-8000-000000000000',
              ruleName: 'Start implementation',
            },
          }
        : undefined,
    )

    await invoke(['run', 'view', runId])

    expect(out.join('')).toContain('Requested by  rule: Start implementation')
  })

  it('view says the rule is gone when its name did not come back', async () => {
    instance.on((r) =>
      r.path === `/api/v1/orgs/acme/runs/${runId}`
        ? {
            status: 200,
            body: {
              ...run,
              requestedBy: null,
              ruleId: 'f0000000-0000-4000-8000-000000000000',
              ruleName: null,
            },
          }
        : undefined,
    )

    await invoke(['run', 'view', runId])

    expect(out.join('')).toContain('Requested by  rule: (deleted)')
  })

  it('logs prints each chunk once, prefixing stderr and event lines', async () => {
    const entries: RunLogEntry[] = [
      { seq: 0, at: '2026-09-18T10:01:00Z', stream: 'stdout', text: 'hello\n' },
      { seq: 1, at: '2026-09-18T10:01:01Z', stream: 'stderr', text: 'warn a\nwarn b' },
      { seq: 2, at: '2026-09-18T10:01:02Z', stream: 'event', text: 'tool: bash' },
    ]
    instance.on((r) =>
      r.path === `/api/v1/orgs/acme/runs/${runId}/log`
        ? { status: 200, body: { items: entries, truncated: true } }
        : undefined,
    )

    await invoke(['run', 'logs', runId])

    expect(out.join('')).toBe(
      'hello\n[stderr] warn a\n[stderr] warn b\n[event] tool: bash\n(log truncated)\n',
    )
    // Without --follow the status is not consulted: one request, from the beginning.
    expect(instance.requests.map((r) => r.path)).toEqual([`/api/v1/orgs/acme/runs/${runId}/log`])
    expect(instance.requests[0]?.query.after).toBe('-1')
  })

  it('logs --follow polls with an increasing after and stops once the run is terminal', async () => {
    let statusCalls = 0
    const log: RunLogEntry[] = [
      { seq: 0, at: '2026-09-18T10:01:00Z', stream: 'stdout', text: 'one' },
      { seq: 1, at: '2026-09-18T10:01:01Z', stream: 'stdout', text: 'two' },
      { seq: 2, at: '2026-09-18T10:01:02Z', stream: 'stdout', text: 'three' },
    ]
    instance
      .on((r) => {
        if (r.path !== `/api/v1/orgs/acme/runs/${runId}`) return undefined
        statusCalls += 1
        // Running for two polls, then finished; the last line lands after the finish.
        return { status: 200, body: { ...run, status: statusCalls < 3 ? 'running' : 'succeeded' } }
      })
      .on((r) => {
        if (r.path !== `/api/v1/orgs/acme/runs/${runId}/log`) return undefined
        const after = Number(r.query.after)
        const visible = statusCalls < 3 ? log.slice(0, 2) : log
        return {
          status: 200,
          body: { items: visible.filter((e) => e.seq > after), truncated: false },
        }
      })

    await invoke(['run', 'logs', runId, '--follow'])

    expect(out.join('')).toBe('one\ntwo\nthree\n')
    const afters = instance.to('/log').map((r) => r.query.after)
    expect(afters).toEqual(['-1', '1', '1'])
    expect(statusCalls).toBe(3)
  })

  it('cancel posts to the cancel route and confirms', async () => {
    await invoke(['run', 'cancel', runId])

    const posted = instance.to(`/runs/${runId}/cancel`)
    expect(posted).toHaveLength(1)
    expect(posted[0]?.method).toBe('POST')
    expect(out.join('')).toBe(`Cancel requested for run ${runId}.\n`)
  })

  it('cancel on a finished run rejects with exit code 3', async () => {
    instance.on((r) =>
      r.path === `/api/v1/orgs/acme/runs/${runId}/cancel`
        ? { status: 409, body: { title: 'Run already finished', status: 409 } }
        : undefined,
    )

    const failure = await invoke(['run', 'cancel', runId]).catch((error: unknown) => error)
    expect(failure).toBeInstanceOf(CliError)
    expect((failure as CliError).exitCode).toBe(ExitCode.Conflict)
  })
})

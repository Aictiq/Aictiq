import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { RunnerClient } from '../../src/runner/client.js'
import type { RunnerConfig, RunnerProfile } from '../../src/runner/config.js'
import { Floor } from '../../src/runner/floor.js'
import { RunnerLoop, RunnerRevokedError } from '../../src/runner/loop.js'
import { RunnerSupervisor } from '../../src/runner/supervisor.js'
import type { RunnerCapabilities } from '../../src/runner/types.js'
import { claimedRun, hello } from './helpers.js'
import { FakeInstance } from './fake-instance.js'
import type { Recorded } from './fake-instance.js'

const capabilities: RunnerCapabilities = {
  v: 1,
  harnesses: [{ name: 'fake', version: '1.0.0' }],
  os: 'linux',
  arch: 'x64',
  cliVersion: '0.1.0',
  maxParallel: 1,
  machineId: '4d1c7f2e-9b1a-4c3e-8f5d-2a6b7c8d9e0f',
}

const organizations: Record<string, string> = { 'Bearer jrn_acme': 'acme', 'Bearer jrn_globex': 'globex' }
const revoked = { status: 401, body: { type: 'https://aictiq.com/problems/token-revoked', title: 'Revoked' } }

function profile(org: string): RunnerProfile {
  return { url: '', token: `jrn_${org}`, organization: org, workspaces: {}, repoRoots: [] }
}

/** One queued run per organization, claimable again after it is released. */
class Queues {
  readonly runs = new Map<string, { runId: string; assigned: boolean; done: boolean }>()
  readonly released: string[] = []

  constructor(orgs: string[]) {
    for (const org of orgs) this.runs.set(org, { runId: `run-${org}`, assigned: false, done: false })
  }

  handle(r: Recorded): { status: number; body?: unknown } | undefined {
    const org = organizations[r.authorization ?? '']
    if (!org) return revoked
    const run = this.runs.get(org)
    if (r.path.endsWith('/runner/hello')) return { status: 200, body: { ...hello, organizationSlug: org } }
    if (r.path.endsWith('/runs/claim')) {
      if (!run || run.assigned || run.done) return { status: 204 }
      run.assigned = true
      return { status: 200, body: claimedRun({ runId: run.runId, organizationSlug: org, itemKey: `${org.toUpperCase()}-1` }) }
    }
    if (r.path.endsWith('/release') && run) {
      run.assigned = false
      this.released.push(run.runId)
      return { status: 204 }
    }
    return undefined
  }
}

describe('RunnerSupervisor', () => {
  let instance: FakeInstance

  beforeEach(async () => {
    instance = await new FakeInstance().start()
  })
  afterEach(async () => {
    await instance.stop()
  })

  function supervisor(
    config: () => RunnerConfig,
    executed: Array<{ org: string; start: number; end: number }>,
    notes: string[] = [],
  ) {
    return new RunnerSupervisor({
      readConfig: config,
      local: (message) => notes.push(message),
      reloadMs: 20,
      floor: new Floor(10),
      createLoop: (p, floor, onHello) =>
        new RunnerLoop({
          client: new RunnerClient({ baseUrl: instance.url, token: p.token }),
          parallel: 1,
          probe: async () => capabilities,
          local: (message) => notes.push(`[${p.organization}] ${message}`),
          floor,
          floorKey: p.token,
          onHello,
          execute: async (run) => {
            const start = Date.now()
            await new Promise((resolve) => setTimeout(resolve, 60))
            executed.push({ org: run.organizationSlug, start, end: Date.now() })
          },
        }),
    })
  }

  const config = (...profiles: RunnerProfile[]): RunnerConfig => ({
    machineId: capabilities.machineId!,
    profiles: profiles.map((p) => ({ ...p, url: instance.url })),
    attachments: { maxCount: 25, maxBytes: 1024 },
  })

  async function until(condition: () => boolean) {
    for (let i = 0; i < 400 && !condition(); i++) await new Promise((resolve) => setTimeout(resolve, 10))
    expect(condition()).toBe(true)
  }

  it('runs two organizations on one machine, never at the same time, giving back what it cannot take', async () => {
    const queues = new Queues(['acme', 'globex'])
    instance.on((r) => queues.handle(r))
    const executed: Array<{ org: string; start: number; end: number }> = []
    const notes: string[] = []
    const runner = supervisor(() => config(profile('acme'), profile('globex')), executed, notes)

    const running = runner.run()
    await until(() => executed.length === 2)
    runner.stop()
    await running

    expect(executed.map((e) => e.org).sort()).toEqual(['acme', 'globex'])
    const [first, second] = [...executed].sort((a, b) => a.start - b.start)
    expect(second!.start).toBeGreaterThanOrEqual(first!.end)
    // Both queues answered the idle machine's first polls at once; the loser gave its run back.
    expect(queues.released).toHaveLength(1)
    // The floor is keyed by secret here; nothing the runner prints may carry one.
    expect(notes.some((n) => n.includes('Gave'))).toBe(true)
    expect(notes.filter((n) => n.includes('jrn_'))).toEqual([])
    expect(queues.released[0]).toBe(`run-${second!.org}`)
    // Every profile said hello with the same machine id.
    expect(new Set(instance.to('/runner/hello').map((r) => (r.body as { capabilities: RunnerCapabilities }).capabilities.machineId)))
      .toEqual(new Set([capabilities.machineId]))
  })

  it('picks up a registration added while it runs, and one revoked organization does not stop the others', async () => {
    const queues = new Queues(['globex'])
    instance.on((r) => queues.handle(r))
    let profiles = [profile('acme'), profile('initech')]
    const notes: string[] = []
    const executed: Array<{ org: string; start: number; end: number }> = []
    const runner = supervisor(() => config(...profiles), executed, notes)

    const running = runner.run()
    await until(() => notes.some((n) => n.includes('[initech]') && n.includes('disabled, deleted or rotated')))
    expect(instance.requests.filter((r) => r.authorization === 'Bearer jrn_acme' && r.path.endsWith('/runs/claim')).length)
      .toBeGreaterThan(0)

    profiles = [profile('acme'), profile('initech'), profile('globex')]
    await until(() => executed.some((e) => e.org === 'globex'))

    // Removed from runner.json, a profile stops polling.
    profiles = [profile('globex')]
    await until(() => notes.some((n) => n.includes('[acme] Removed from runner.json')))
    runner.stop()
    await running
  })

  it('ends with the revoked error once no organization in runner.json still works', async () => {
    instance.on(() => revoked)
    const runner = supervisor(() => config(profile('acme'), profile('globex')), [])
    await expect(runner.run()).rejects.toBeInstanceOf(RunnerRevokedError)
  })
})

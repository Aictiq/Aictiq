import { mount } from '@vue/test-utils'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { createMemoryHistory, createRouter } from 'vue-router'

import RunnerLoad from '@/components/factory/RunnerLoad.vue'
import {
  deleteRunner,
  getRunnerLoad,
  listRunnerMachinesElsewhere,
  listRunners,
  registerRunner,
  registerRunnerOnMachine,
  rotateRunner,
  updateRunner,
  type Runner,
  type RunnerLoadEntry,
  type RunnerUsageLimits,
} from '@/api/runners'
import {
  claudeUsagePollHint,
  loadBarClass,
  loadFor,
  loadLabel,
  loadLevel,
  loadPercent,
  runnerPlatformGuess,
  runnerRegisterCommand,
  runnerServiceSteps,
  runnerStatus,
  usageLimitsForRun,
  usageSummary,
  usageWindowStale,
  waitingSummary,
} from '@/lib/runners'
import { factoryLinks, factoryPath, factoryRunPath, factorySetupPath } from '@/router/paths'

/**
 * Runners. The roster is an organization's, so every route nests under its slug;
 * a runner is only as alive as its last heartbeat; and the command a person pastes on the
 * machine has to be exactly what the CLI accepts.
 */

function stubFetch(body: unknown = {}) {
  const fetchMock = vi.fn(
    async (_input: RequestInfo | URL, _init?: RequestInit) =>
      new Response(JSON.stringify(body), {
        status: 200,
        headers: { 'content-type': 'application/json' },
      }),
  )
  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}

afterEach(() => {
  vi.unstubAllGlobals()
})

const runner = (overrides: Partial<Runner> = {}): Runner => ({
  id: 'r1',
  name: 'vps-1',
  tokenDisplay: 'jrn_abcdefgh…',
  registeredBy: 'u1',
  registeredByName: 'Alice',
  capabilities: null,
  lastSeenAt: null,
  isOnline: false,
  isDisabled: false,
  createdAt: '2026-09-17T10:00:00Z',
  ...overrides,
})

describe('the runner endpoints', () => {
  it('nests every route under the organization', async () => {
    const fetchMock = stubFetch([])

    await listRunners('acme')
    await rotateRunner('acme', 'r1')
    await deleteRunner('acme', 'r1')

    expect(String(fetchMock.mock.calls[0]![0])).toBe('/api/v1/orgs/acme/runners')
    expect(String(fetchMock.mock.calls[1]![0])).toBe('/api/v1/orgs/acme/runners/r1/rotate')
    expect(fetchMock.mock.calls[1]![1]!.method).toBe('POST')
    expect(fetchMock.mock.calls[2]![1]!.method).toBe('DELETE')
  })

  it('registers with a name and the CSRF header', async () => {
    const fetchMock = stubFetch({ runner: runner(), secret: 'jrn_x' })

    await registerRunner('acme', 'vps-1')

    const [url, init] = fetchMock.mock.calls[0]!
    expect(String(url)).toBe('/api/v1/orgs/acme/runners')
    expect(init!.method).toBe('POST')
    expect(JSON.parse(String(init!.body))).toEqual({ name: 'vps-1' })
    expect(new Headers(init!.headers).get('X-Aictiq-Request')).toBe('1')
  })

  it('lists the caller’s machines elsewhere and registers one of them here', async () => {
    const fetchMock = stubFetch({ runner: runner(), secret: 'jrn_x' })

    await listRunnerMachinesElsewhere('acme')
    await registerRunnerOnMachine('acme', 'r9')
    await registerRunnerOnMachine('acme', 'r9', 'laptop-acme')

    expect(String(fetchMock.mock.calls[0]![0])).toBe('/api/v1/orgs/acme/runners/elsewhere')
    expect(JSON.parse(String(fetchMock.mock.calls[1]![1]!.body))).toEqual({ sameMachineAs: 'r9' })
    expect(JSON.parse(String(fetchMock.mock.calls[2]![1]!.body))).toEqual({
      name: 'laptop-acme',
      sameMachineAs: 'r9',
    })
  })

  it('patches without a version: the runner rewrites its own row every heartbeat', async () => {
    const fetchMock = stubFetch(runner())

    await updateRunner('acme', 'r1', { disabled: true })

    const [url, init] = fetchMock.mock.calls[0]!
    expect(String(url)).toBe('/api/v1/orgs/acme/runners/r1')
    expect(init!.method).toBe('PATCH')
    expect(JSON.parse(String(init!.body))).toEqual({ disabled: true })
  })
})

describe('runnerStatus', () => {
  it('tells a machine that went quiet from one that never spoke', () => {
    expect(runnerStatus(runner())).toBe('never')
    expect(runnerStatus(runner({ lastSeenAt: '2026-09-17T10:00:00Z' }))).toBe('offline')
    expect(runnerStatus(runner({ lastSeenAt: '2026-09-17T10:00:00Z', isOnline: true }))).toBe(
      'online',
    )
  })

  it('says disabled before anything else', () => {
    expect(runnerStatus(runner({ isDisabled: true, isOnline: true }))).toBe('disabled')
  })
})

describe('claudeUsagePollHint', () => {
  const claude = [{ name: 'claude', version: '2.1.294' }]

  it('shows for a runner with Claude whose poll is off', () => {
    expect(claudeUsagePollHint({ harnesses: claude, claudeUsagePoll: false })).toBe(true)
    // A runner before 0.9.1 reports nothing, and its Claude usage only moves after runs too.
    expect(claudeUsagePollHint({ harnesses: claude })).toBe(true)
  })

  it('stays away once the poll is on, or without Claude', () => {
    expect(claudeUsagePollHint({ harnesses: claude, claudeUsagePoll: true })).toBe(false)
    expect(claudeUsagePollHint({ harnesses: [{ name: 'codex', version: '0.160.0' }] })).toBe(false)
    expect(claudeUsagePollHint(null)).toBe(false)
  })
})

describe('runnerRegisterCommand', () => {
  it('names the page origin and the secret', () => {
    expect(runnerRegisterCommand('https://aictiq.example.com/', 'jrn_abc')).toBe(
      'aictiq runner register --url https://aictiq.example.com --token jrn_abc',
    )
  })

  it('quotes a name the shell would split', () => {
    expect(runnerRegisterCommand('http://localhost:5173', 'jrn_abc', "Ana's box")).toBe(
      `aictiq runner register --url http://localhost:5173 --token jrn_abc --name 'Ana'\\''s box'`,
    )
    expect(runnerRegisterCommand('http://h', 'jrn_abc', 'vps-1')).toContain('--name vps-1')
  })
})

describe('runnerServiceSteps', () => {
  it('writes and enables the definition install-service only prints', () => {
    for (const steps of Object.values(runnerServiceSteps)) {
      expect(steps.commands).toContain('aictiq runner install-service')
    }

    expect(runnerServiceSteps.linux.commands).toContain(
      'systemctl --user enable --now aictiq-runner',
    )
    expect(runnerServiceSteps.linux.commands).toContain('loginctl enable-linger')
    expect(runnerServiceSteps.macos.commands).toContain('launchctl bootstrap gui/$(id -u)')
    expect(runnerServiceSteps.windows.commands).toContain('-File install-runner.ps1')
  })
})

describe('runnerPlatformGuess', () => {
  it('opens on the browser’s own platform, and falls back to Linux', () => {
    expect(runnerPlatformGuess('Mozilla/5.0 (Windows NT 10.0; Win64; x64)')).toBe('windows')
    expect(runnerPlatformGuess('Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7)')).toBe('macos')
    expect(runnerPlatformGuess('Mozilla/5.0 (X11; Linux x86_64)')).toBe('linux')
    expect(runnerPlatformGuess('')).toBe('linux')
  })
})

describe('the Factory area', () => {
  it('lands on runners and links every factory tab', () => {
    expect(factoryPath('acme')).toBe('/o/acme/factory/runners')
    expect(factoryLinks('acme').map((link) => link.label)).toEqual([
      'Setup',
      'Runs',
      'Rules',
      'Runners',
      'Playbooks',
    ])
    expect(factoryRunPath('acme', 'r-1')).toBe('/o/acme/factory/runs/r-1')
    expect(factorySetupPath('acme')).toBe('/o/acme/factory/setup')
    expect(factorySetupPath('acme', { runner: 'r 1', project: 'WEB' })).toBe(
      '/o/acme/factory/setup?runner=r+1&project=WEB',
    )
  })
})

describe('harness usage limits', () => {
  const now = new Date('2026-10-08T12:00:00Z')
  const limits = (
    observedAt: string,
    over: Partial<RunnerUsageLimits> = {},
  ): RunnerUsageLimits => ({
    harness: 'claude',
    observedAt,
    fiveHour: { usedPercent: 40.4, resetsAt: '2026-10-08T14:00:00Z' },
    weekly: { usedPercent: 71.6, resetsAt: '2026-10-12T09:00:00Z' },
    ...over,
  })

  it('summarises both windows and the weekly reset in one short line', () => {
    const summary = usageSummary(limits('2026-10-08T11:50:00Z'), now)
    expect(summary).toMatch(/^5h 40% · wk 72% · resets \S+$/)
  })

  it('marks a window stale once it is older than it lasts or its reset has passed', () => {
    const old = limits('2026-10-08T06:00:00Z')
    expect(usageWindowStale(old, 'fiveHour', now)).toBe(true)
    expect(usageWindowStale(old, 'weekly', now)).toBe(false)
    expect(usageSummary(old, now)).toMatch(/^5h stale · wk 72%/)

    const reset = limits('2026-10-08T11:00:00Z', {
      fiveHour: { usedPercent: 90, resetsAt: '2026-10-08T11:30:00Z' },
    })
    expect(usageWindowStale(reset, 'fiveHour', now)).toBe(true)
    expect(usageWindowStale(limits('2026-09-30T12:00:00Z'), 'weekly', now)).toBe(true)
  })

  it('leaves out a window the harness did not report', () => {
    expect(
      usageSummary(
        limits('2026-10-08T11:50:00Z', {
          fiveHour: null,
          weekly: { usedPercent: 3, resetsAt: null },
        }),
        now,
      ),
    ).toBe('wk 3%')
  })

  it('takes the most recently read runner for the harness, and nothing without one', () => {
    const runners = [
      { name: 'vps-1', usageLimits: [limits('2026-10-08T10:00:00Z')] },
      { name: 'laptop', usageLimits: [limits('2026-10-08T11:00:00Z')] },
      { name: 'old', usageLimits: null },
    ]
    expect(usageLimitsForRun(runners, 'claude')?.runner).toBe('laptop')
    expect(usageLimitsForRun(runners, 'codex')).toBeNull()
    expect(usageLimitsForRun(runners, null)).toBeNull()
  })
})

describe('runner load', () => {
  const now = new Date('2026-10-08T12:00:00Z')

  const entry = (overrides: Partial<RunnerLoadEntry> = {}): RunnerLoadEntry => ({
    runnerId: 'r1',
    running: 0,
    queued: 0,
    scheduled: 0,
    nextScheduledFor: null,
    active: [],
    ...overrides,
  })

  it('reads the load from the roster', async () => {
    const fetchMock = stubFetch({ runners: [], unassignedQueued: 0, unassignedScheduled: 0 })
    await getRunnerLoad('acme')
    expect(String(fetchMock.mock.calls[0]![0])).toBe('/api/v1/orgs/acme/runners/load')
  })

  it('fills the bar by slots: idle, busy, full', () => {
    expect([loadPercent(2, 4), loadLabel(2, 4), loadLevel(2, 4)]).toEqual([
      50,
      '2 / 4 running',
      'busy',
    ])
    expect([loadPercent(0, 4), loadLabel(0, 4), loadLevel(0, 4)]).toEqual([0, 'idle', 'idle'])
    expect([loadPercent(4, 4), loadLevel(4, 4)]).toEqual([100, 'full'])
    // More runs than slots after --parallel shrank is still a full bar, not an overflow.
    expect([loadPercent(5, 4), loadLevel(5, 4)]).toEqual([100, 'full'])
    expect(loadBarClass('full', false)).toBe('bg-amber-500')
    expect(loadBarClass('busy', false)).toBe('bg-success')
    expect(loadBarClass('full', true)).toBe('bg-muted-foreground/40')
  })

  it('says what waits, and when the next scheduled run starts', () => {
    expect(waitingSummary(0, 0, null, now)).toBe('')
    expect(waitingSummary(1, 0, null, now)).toBe('1 queued')
    expect(waitingSummary(2, 3, '2026-10-08T14:00:00Z', now)).toBe(
      '2 queued · 3 scheduled · next in 2h',
    )
    expect(waitingSummary(0, 1, '2026-10-08T12:30:00Z', now)).toBe('1 scheduled · next in 30m')
    expect(waitingSummary(0, 1, '2026-10-11T12:00:00Z', now)).toBe('1 scheduled · next in 3d')
  })

  it('gives a runner with no live runs an empty load', () => {
    const load = {
      runners: [entry({ runnerId: 'r1', running: 2 })],
      unassignedQueued: 0,
      unassignedScheduled: 0,
      nextUnassignedScheduledFor: null,
    }
    expect(loadFor(load, 'r1').running).toBe(2)
    expect(loadFor(load, 'r2')).toEqual(entry({ runnerId: 'r2' }))
    expect(loadFor(null, 'r1')).toEqual(entry())
  })

  async function mountLoad(props: { entry: RunnerLoadEntry; slots: number; muted?: boolean }) {
    const router = createRouter({
      history: createMemoryHistory(),
      routes: [{ path: '/:pathMatch(.*)*', component: { template: '<div />' } }],
    })
    await router.push('/')
    return mount(RunnerLoad, {
      props: { slug: 'acme', runnerName: 'vps-1', muted: false, ...props },
      global: { plugins: [router] },
    })
  }

  it('draws an accessible meter and links each active run', async () => {
    const wrapper = await mountLoad({
      slots: 4,
      entry: entry({
        running: 2,
        queued: 1,
        active: [
          {
            id: 'a1',
            itemKey: 'WEB-1',
            playbookName: 'Implement',
            status: 'running',
            startedAt: null,
          },
          { id: 'a2', itemKey: 'WEB-2', playbookName: null, status: 'assigned', startedAt: null },
        ],
      }),
    })
    const meter = wrapper.get('[role="meter"]')
    expect(meter.attributes('aria-label')).toBe('Slots in use on vps-1')
    expect(meter.attributes('aria-valuenow')).toBe('2')
    expect(meter.attributes('aria-valuemax')).toBe('4')
    expect(meter.get('span').attributes('style')).toContain('width: 50%')
    expect(wrapper.text()).toContain('2 / 4 running')
    expect(wrapper.get('[data-testid="runner-waiting"]').text()).toBe('1 queued')
    const link = wrapper.get('[data-testid="runner-active-run-a1"]')
    expect(link.attributes('href')).toBe('/o/acme/factory/runs/a1')
    expect(link.attributes('title')).toBe('WEB-1 · Implement')
  })

  it('links the rest to Runs filtered by the runner once there are too many', async () => {
    const active = ['a', 'b', 'c', 'd', 'e'].map((id) => ({
      id,
      itemKey: `WEB-${id}`,
      playbookName: null,
      status: 'running' as const,
      startedAt: null,
    }))
    const wrapper = await mountLoad({ slots: 5, entry: entry({ running: 5, active }) })
    expect(wrapper.get('[data-testid="runner-load"]').attributes('data-level')).toBe('full')
    expect(wrapper.findAll('[data-testid^="runner-active-run-"]')).toHaveLength(3)
    const more = wrapper.findAll('a').at(-1)!
    expect(more.text()).toBe('+2 more')
    expect(more.attributes('href')).toBe('/o/acme/factory/runs?runner=r1')
  })

  it('mutes an idle or offline runner but still says what waits for it', async () => {
    const wrapper = await mountLoad({
      slots: 2,
      muted: true,
      entry: entry({
        scheduled: 1,
        nextScheduledFor: new Date(Date.now() + 2 * 3_600_000).toISOString(),
      }),
    })
    expect(wrapper.text()).toContain('idle')
    expect(wrapper.get('[role="meter"] span').classes()).toContain('bg-muted-foreground/40')
    expect(wrapper.get('[data-testid="runner-waiting"]').text()).toBe('1 scheduled · next in 2h')
  })
})

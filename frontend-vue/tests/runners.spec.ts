import { afterEach, describe, expect, it, vi } from 'vitest'

import {
  deleteRunner,
  listRunnerMachinesElsewhere,
  listRunners,
  registerRunner,
  registerRunnerOnMachine,
  rotateRunner,
  updateRunner,
  type Runner,
  type RunnerUsageLimits,
} from '@/api/runners'
import {
  runnerMissingHarnessLabel,
  runnerPlatformGuess,
  runnerRegisterCommand,
  runnerServiceSteps,
  runnerStatus,
  usageLimitsForRun,
  usageSummary,
  usageWindowStale,
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

describe('runnerMissingHarnessLabel', () => {
  it('says whether to fix PATH or the harness itself', () => {
    expect(
      runnerMissingHarnessLabel({ name: 'claude', reason: 'not-on-path', command: 'claude' }),
    ).toBe("claude is not on the runner's PATH")
    expect(
      runnerMissingHarnessLabel({ name: 'codex', reason: 'version-failed', command: 'codex' }),
    ).toBe('codex is on PATH, but `codex --version` failed')
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

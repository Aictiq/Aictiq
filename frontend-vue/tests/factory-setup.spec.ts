import { describe, expect, it } from 'vitest'

import type { Agent } from '@/api/agents'
import type { FactorySettings, Playbook } from '@/api/playbooks'
import type { ProjectMember } from '@/api/projects'
import type { Runner, RunnerCapabilities } from '@/api/runners'
import {
  hintUnderRoot,
  machineSteps,
  projectSteps,
  runnerPlatformOf,
  type ProjectFacts,
  type SetupStep,
} from '@/lib/factorySetup'
import { runnerMapCommand, runnerRootCommand } from '@/lib/runners'

const capabilities = (extra: Partial<RunnerCapabilities> = {}): RunnerCapabilities => ({
  v: 1,
  harnesses: [{ name: 'claude', version: '2.1.0' }],
  os: 'linux',
  arch: 'x64',
  cliVersion: '0.9.0',
  maxParallel: 1,
  ...extra,
})

const runner = (extra: Partial<Runner> = {}): Runner => ({
  id: 'r-1',
  name: 'vps-1',
  tokenDisplay: 'jrn_abcd…',
  registeredBy: 'u-1',
  registeredByName: 'Ana',
  capabilities: capabilities(),
  lastSeenAt: '2026-09-30T08:00:00Z',
  isOnline: true,
  isDisabled: false,
  createdAt: '2026-09-30T07:00:00Z',
  ...extra,
})

const states = (steps: SetupStep[]) => Object.fromEntries(steps.map((s) => [s.id, s.state]))

describe('the machine steps', () => {
  it('are all to do before a runner exists', () => {
    expect(states(machineSteps(null, new Set()))).toEqual({
      install: 'todo',
      harness: 'todo',
      register: 'todo',
      start: 'todo',
      service: 'todo',
    })
  })

  it('wait for the first hello after a registration', () => {
    const steps = machineSteps(
      runner({ capabilities: null, lastSeenAt: null, isOnline: false }),
      new Set(),
    )
    expect(states(steps)).toMatchObject({ install: 'todo', register: 'todo', start: 'todo' })
    expect(steps.find((s) => s.id === 'register')?.detail).toMatch(/has not said hello/)
  })

  it('are confirmed by what the runner reports, including a service start', () => {
    const steps = machineSteps(runner({ capabilities: capabilities({ service: true }) }), new Set())
    expect(Object.values(states(steps))).toEqual(Array(5).fill('confirmed'))
  })

  it('say a terminal runner is not a service, and offer a tick only to an older runner', () => {
    const terminal = machineSteps(runner({ capabilities: capabilities({ service: false }) }), new Set())
    expect(terminal.find((s) => s.id === 'service')).toMatchObject({ state: 'todo', tickable: false })

    const older = machineSteps(runner(), new Set())
    expect(older.find((s) => s.id === 'service')).toMatchObject({ state: 'unknown', tickable: true })
    expect(machineSteps(runner(), new Set(['service'])).find((s) => s.id === 'service')?.state).toBe(
      'ticked',
    )
  })

  it('open the service tab for the platform the runner reported', () => {
    expect(runnerPlatformOf(runner({ capabilities: capabilities({ os: 'darwin' }) }))).toBe('macos')
    expect(runnerPlatformOf(runner({ capabilities: capabilities({ os: 'win32' }) }))).toBe('windows')
    expect(runnerPlatformOf(null)).toBeNull()
  })
})

describe('a path hint under a runner root', () => {
  it('matches absolute paths on whole segments only', () => {
    expect(hintUnderRoot('/srv/repos/web', ['/srv/repos'])).toBe(true)
    expect(hintUnderRoot('/srv/repos/web/', ['/srv/repos/'])).toBe(true)
    expect(hintUnderRoot('/srv/repositories/web', ['/srv/repos'])).toBe(false)
    expect(hintUnderRoot('/srv/repos/web', [])).toBe(false)
  })

  it('matches a home-relative hint against a root under a home directory', () => {
    expect(hintUnderRoot('~/src/web', ['/home/ana/src'])).toBe(true)
    expect(hintUnderRoot('~/src/web', ['/Users/ana/code'])).toBe(false)
    // Which home `~` is, only the runner knows.
    expect(hintUnderRoot('~/src/web', ['/srv/repos'])).toBeNull()
    expect(hintUnderRoot('repos/web', ['/srv'])).toBeNull()
  })
})

describe('the project steps', () => {
  const settings = (extra: Partial<FactorySettings> = {}): FactorySettings => ({
    projectId: 'p-1',
    repoSource: 1,
    repoFullName: null,
    defaultBranch: 'main',
    localPathHint: '/srv/repos/web',
    defaultAgentId: null,
    updatedAt: '2026-09-30T08:00:00Z',
    version: 3,
    ...extra,
  })
  const agent = { userId: 'a-1', displayName: 'worker', isActive: true } as Agent
  const member = { userId: 'a-1', isAgent: true } as ProjectMember
  const playbook = { name: 'Implement', harness: 'claude', isDefault: true } as Playbook

  const facts = (extra: Partial<ProjectFacts> = {}): ProjectFacts => ({
    projectKey: 'WEB',
    settings: settings(),
    agents: [agent],
    members: [member],
    playbooks: [playbook],
    succeededRuns: 0,
    runner: runner({ capabilities: capabilities({ workspaces: [], repoRoots: ['/srv/repos'] }) }),
    offeredHarnesses: ['claude'],
    ...extra,
  })

  it('confirms a runner-local project the runner can reach and leaves the push to a tick', () => {
    const steps = projectSteps(facts(), new Set())
    expect(states(steps)).toEqual({
      agent: 'confirmed',
      repository: 'confirmed',
      checkout: 'confirmed',
      push: 'unknown',
      connection: 'automatic',
      playbook: 'confirmed',
      'first-run': 'todo',
    })
    expect(steps.find((s) => s.id === 'push')?.tickable).toBe(true)
    expect(states(projectSteps(facts(), new Set(['push']))).push).toBe('ticked')
  })

  it('needs nothing on the machine for a GitHub binding', () => {
    const bound = facts({ settings: settings({ repoSource: 0, repoFullName: 'acme/web' }) })
    expect(states(projectSteps(bound, new Set()))).toMatchObject({
      checkout: 'automatic',
      push: 'automatic',
    })
  })

  it('names what is missing: an agent off the roster, an unmapped project, a harness nobody has', () => {
    const steps = projectSteps(
      facts({
        members: [],
        runner: runner({ capabilities: capabilities({ workspaces: ['API'], repoRoots: ['/home/x'] }) }),
        playbooks: [{ ...playbook, harness: 'codex' }],
      }),
      new Set(),
    )
    expect(states(steps)).toMatchObject({ agent: 'todo', checkout: 'todo', playbook: 'todo' })
    expect(steps.find((s) => s.id === 'agent')?.detail).toMatch(/none is a member/)
    expect(steps.find((s) => s.id === 'playbook')?.detail).toMatch(/codex/)
  })

  it('treats a mapping as reachable and a first success as proof of checkout and push', () => {
    const mapped = facts({
      settings: settings({ localPathHint: null }),
      runner: runner({ capabilities: capabilities({ workspaces: ['WEB'], repoRoots: [] }) }),
    })
    expect(states(projectSteps(mapped, new Set())).checkout).toBe('confirmed')

    const delivered = facts({ runner: null, succeededRuns: 2 })
    expect(states(projectSteps(delivered, new Set()))).toMatchObject({
      checkout: 'confirmed',
      push: 'confirmed',
      'first-run': 'confirmed',
    })
  })

  it('lets someone who cannot see the roster tick the checkout', () => {
    const blind = projectSteps(facts({ runner: null }), new Set())
    expect(blind.find((s) => s.id === 'checkout')).toMatchObject({ state: 'unknown', tickable: true })
    const none = projectSteps(facts({ runner: null, offeredHarnesses: [] }), new Set())
    expect(none.find((s) => s.id === 'checkout')?.state).toBe('todo')
  })

  it('does not count an unsaved default configuration', () => {
    const steps = projectSteps(facts({ settings: settings({ updatedAt: null }) }), new Set())
    expect(states(steps)).toMatchObject({ repository: 'todo', checkout: 'todo', push: 'todo' })
  })
})

describe('the runner repository commands', () => {
  it('root the hint’s parent and map the hint itself, quoted for a shell', () => {
    expect(runnerRootCommand('/srv/repos/web/', 'acme')).toBe('aictiq runner root /srv/repos --org acme')
    expect(runnerMapCommand('WEB', "~/my repos/it's", 'acme')).toBe(
      `aictiq runner map WEB ~/'my repos/it'\\''s' --org acme`,
    )
    expect(runnerRootCommand('', 'acme')).toBe('aictiq runner root <directory> --org acme')
  })
})

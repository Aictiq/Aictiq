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
} from '@/api/runners'
import { runnerRegisterCommand, runnerStatus } from '@/lib/runners'
import { factoryLinks, factoryPath, factoryRunPath } from '@/router/paths'

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

describe('the Factory area', () => {
  it('lands on runners and links every factory tab', () => {
    expect(factoryPath('acme')).toBe('/o/acme/factory/runners')
    expect(factoryLinks('acme').map((link) => link.label)).toEqual([
      'Runs',
      'Rules',
      'Runners',
      'Playbooks',
    ])
    expect(factoryRunPath('acme', 'r-1')).toBe('/o/acme/factory/runs/r-1')
  })
})

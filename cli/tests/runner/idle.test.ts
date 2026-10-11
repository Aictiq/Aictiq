import { describe, expect, it } from 'vitest'
import { RunnerHttpError, RunnerNetworkError } from '../../src/runner/client.js'
import type { RunnerConfig, RunnerProfile } from '../../src/runner/config.js'
import { IdleClock, idleExitMinutes, retireEphemeral } from '../../src/runner/idle.js'

const machineId = '4d1c7f2e-9b1a-4c3e-8f5d-2a6b7c8d9e0f'

describe('IdleClock', () => {
  it('expires once nothing has run for the idle time, counting from its start', () => {
    let now = 1000
    const clock = new IdleClock(60_000, () => now)

    now += 59_999
    expect(clock.expired(0)).toBe(false)
    now += 1
    expect(clock.expired(0)).toBe(true)
  })

  it('never expires while a run is in flight, and counts again from the end of the last one', () => {
    let now = 0
    const clock = new IdleClock(60_000, () => now)

    now = 30_000
    clock.touch() // claimed
    now = 10 * 60_000
    expect(clock.expired(2)).toBe(false)
    now += 30_000
    clock.touch() // ended
    expect(clock.expired(0)).toBe(false)
    now += 59_999
    expect(clock.expired(0)).toBe(false)
    now += 1
    expect(clock.expired(0)).toBe(true)
  })

  it('a run that started and ended between two checks still restarts the count', () => {
    let now = 0
    const clock = new IdleClock(60_000, () => now)
    now = 50_000
    clock.touch()
    now = 55_000
    clock.touch()
    now = 61_000
    expect(clock.expired(0)).toBe(false)
    now = 115_000
    expect(clock.expired(0)).toBe(true)
  })
})

describe('--idle-exit', () => {
  it('is whole minutes from 1 to 1440, and absent when not given', () => {
    expect(idleExitMinutes(undefined)).toBeNull()
    expect(idleExitMinutes('1')).toBe(1)
    expect(idleExitMinutes('1440')).toBe(1440)
    for (const bad of ['0', '1441', '1.5', '-3', 'ten', '']) {
      expect(() => idleExitMinutes(bad)).toThrow(/--idle-exit is a whole number of minutes between 1 and 1440/)
    }
  })
})

describe('retireEphemeral', () => {
  const profile = (org: string, ephemeral: boolean): RunnerProfile => ({
    url: 'https://j.example',
    token: `jrn_${org}`,
    organization: org,
    workspaces: {},
    repoRoots: [],
    ...(ephemeral ? { ephemeral: true as const } : {}),
  })

  function setup(profiles: RunnerProfile[], failures: Record<string, Error> = {}) {
    let stored: RunnerConfig = { machineId, profiles, attachments: { maxCount: 25, maxBytes: 1024 } }
    const deregistered: string[] = []
    const notes: string[] = []
    const run = () =>
      retireEphemeral({
        readConfig: () => structuredClone(stored),
        writeConfig: (config) => (stored = config),
        client: (p) => ({
          deregister: async () => {
            deregistered.push(p.token)
            const failure = failures[p.token]
            if (failure) throw failure
          },
        }),
        local: (message) => notes.push(message),
      })
    return { run, deregistered, notes, stored: () => stored }
  }

  it('deregisters every ephemeral profile and keeps the ones registered by hand', async () => {
    const { run, deregistered, stored } = setup([profile('acme', true), profile('globex', false), profile('initech', true)])

    const retired = await run()

    expect(retired.map((p) => p.organization)).toEqual(['acme', 'initech'])
    expect(deregistered).toEqual(['jrn_acme', 'jrn_initech'])
    expect(stored().profiles.map((p) => p.organization)).toEqual(['globex'])
  })

  it('logs a refusal or an unreachable instance and drops the profile anyway', async () => {
    const { run, notes, stored } = setup([profile('acme', true), profile('globex', true), profile('initech', true)], {
      jrn_acme: new RunnerHttpError(404, undefined, '/runner/deregister: 404 Not Found'),
      jrn_globex: new RunnerHttpError(409, undefined, '/runner/deregister: 409 Conflict'),
      jrn_initech: new RunnerNetworkError('Cannot reach https://j.example: ECONNREFUSED'),
    })

    await run()

    expect(stored().profiles).toEqual([])
    expect(notes).toEqual([
      '[acme] The instance did not deregister this runner (/runner/deregister: 404 Not Found); dropping it here anyway',
      '[globex] The instance did not deregister this runner (/runner/deregister: 409 Conflict); dropping it here anyway',
      '[initech] Could not deregister this runner (Cannot reach https://j.example: ECONNREFUSED); dropping it here, and the instance will see it go offline',
    ])
    expect(notes.filter((n) => n.includes('jrn_'))).toEqual([])
  })

  it('leaves runner.json alone when nothing is ephemeral', async () => {
    const { run, deregistered, stored } = setup([profile('acme', false)])
    const before = stored()
    expect(await run()).toEqual([])
    expect(deregistered).toEqual([])
    expect(stored()).toBe(before)
  })
})

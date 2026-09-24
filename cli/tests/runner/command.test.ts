import { mkdirSync, mkdtempSync, readFileSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createProgram } from '../../src/program.js'
import { hello } from './helpers.js'
import { FakeInstance } from './fake-instance.js'

interface Written {
  machineId: string
  profiles: Array<{ organization?: string; url: string; token: string; workspaces: Record<string, string>; repoRoots: string[] }>
}

// The fake instance tells secrets apart by their suffix: jrn_acme_… is acme, jrn_globex_… globex.
const acme = 'jrn_acme_0123456789'
const acmeRotated = 'jrn_acme_rotated_01'
const globex = 'jrn_globex_01234567'

describe('aictiq runner register', () => {
  let instance: FakeInstance
  let configHome: string
  const revokedTokens = new Set<string>()

  beforeEach(async () => {
    revokedTokens.clear()
    instance = await new FakeInstance()
      .on((r) => {
        if (!r.path.endsWith('/runner/hello')) return undefined
        const token = r.authorization?.replace('Bearer ', '') ?? ''
        if (revokedTokens.has(token)) {
          return { status: 401, body: { type: 'https://aictiq.com/problems/token-revoked', title: 'Revoked' } }
        }
        return { status: 200, body: { ...hello, organizationSlug: token.split('_')[1] } }
      })
      .start()
    configHome = mkdtempSync(join(tmpdir(), 'aictiq-cmd-'))
    vi.stubEnv('AICTIQ_CONFIG_HOME', configHome)
    vi.spyOn(process.stdout, 'write').mockImplementation(() => true)
  })

  afterEach(async () => {
    vi.unstubAllEnvs()
    vi.restoreAllMocks()
    await instance.stop()
  })

  const path = () => join(configHome, 'aictiq', 'runner.json')
  const read = () => JSON.parse(readFileSync(path(), 'utf8')) as Written
  const run = (...args: string[]) => createProgram().parseAsync(args, { from: 'user' })
  const register = (token: string) => run('runner', 'register', '--url', instance.url, '--token', token)

  // Parsed through the real program: the root command also defines --url and --token, and
  // commander hands them to the root rather than to the subcommand.
  it('reads --url and --token after the subcommand, verifies them and writes a profile', async () => {
    await register(acme)

    expect(instance.to('/runner/hello')[0]?.authorization).toBe(`Bearer ${acme}`)
    const written = read()
    expect(written.machineId).toMatch(/^[0-9a-f-]{36}$/)
    expect(written.profiles).toEqual([
      { organization: 'acme', url: instance.url, token: acme, workspaces: {}, repoRoots: [] },
    ])
    expect((instance.to('/runner/hello')[0]?.body as { capabilities: { machineId: string } }).capabilities.machineId)
      .toBe(written.machineId)
  })

  it('adds another organization as its own profile and keeps each one’s roots apart', async () => {
    await register(acme)
    await run('runner', 'root', configHome)
    await register(globex)

    expect(read().profiles.map((p) => [p.organization, p.token, p.repoRoots])).toEqual([
      ['acme', acme, [configHome]],
      ['globex', globex, []],
    ])

    // With two, a root or mapping must say whose it is.
    await expect(run('runner', 'root', configHome)).rejects.toThrow(/--org/)
    await run('runner', 'root', configHome, '--org', 'globex')
    await run('runner', 'map', 'ACME', configHome, '--org', 'globex')
    expect(read().profiles[1]).toMatchObject({ repoRoots: [configHome], workspaces: { ACME: configHome } })
    expect(read().profiles[0]!.workspaces).toEqual({})

    // A rotated secret replaces its own organization's profile, keeping what was built up.
    await register(acmeRotated)
    expect(read().profiles.map((p) => [p.organization, p.token, p.repoRoots])).toEqual([
      ['acme', acmeRotated, [configHome]],
      ['globex', globex, [configHome]],
    ])

    await run('runner', 'root', configHome, '--remove', '--org', 'acme')
    expect(read().profiles[0]!.repoRoots).toEqual([])

    await run('runner', 'remove', 'globex')
    expect(read().profiles.map((p) => p.organization)).toEqual(['acme'])
    await expect(run('runner', 'remove', 'globex')).rejects.toThrow(/does not run for globex/)
  })

  it('upgrades a file from before profiles: a rotated secret replaces it and keeps its roots', async () => {
    mkdirSync(join(configHome, 'aictiq'), { recursive: true })
    writeFileSync(path(), JSON.stringify({ url: instance.url, token: acme, workspaces: { ACME: '/src/acme' }, repoRoots: ['/src'] }))
    revokedTokens.add(acme)

    await register(acmeRotated)
    expect(read().profiles).toEqual([
      { organization: 'acme', url: instance.url, token: acmeRotated, workspaces: { ACME: '/src/acme' }, repoRoots: ['/src'] },
    ])
  })

  it('upgrades a file from before profiles: another organization is added beside it', async () => {
    mkdirSync(join(configHome, 'aictiq'), { recursive: true })
    writeFileSync(path(), JSON.stringify({ url: instance.url, token: acme, workspaces: {}, repoRoots: ['/src'] }))

    await register(globex)
    expect(read().profiles.map((p) => [p.organization, p.repoRoots])).toEqual([
      ['acme', ['/src']],
      ['globex', []],
    ])
  })
})

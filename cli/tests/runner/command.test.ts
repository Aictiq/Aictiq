import { mkdirSync, mkdtempSync, readFileSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { CliError, ExitCode } from '../../src/errors.js'
import { createProgram } from '../../src/program.js'
import { hello } from './helpers.js'
import { FakeInstance } from './fake-instance.js'

interface Written {
  machineId: string
  profiles: Array<{ organization?: string; url: string; token: string; workspaces: Record<string, string>; repoRoots: string[]; ephemeral?: true }>
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

  it('reports each organization only its own mappings and roots, for the setup guide', async () => {
    await register(acme)
    await register(globex)
    await run('runner', 'map', 'ACME', configHome, '--org', 'acme')
    await run('runner', 'root', configHome, '--org', 'globex')
    const before = instance.to('/runner/hello').length

    await run('runner', 'status')

    const sent = instance
      .to('/runner/hello')
      .slice(before)
      .map((r) => ({
        token: r.authorization?.replace('Bearer ', ''),
        capabilities: (r.body as { capabilities: { workspaces?: string[]; repoRoots?: string[] } }).capabilities,
      }))
      .sort((a, b) => String(a.token).localeCompare(String(b.token)))
    expect(sent.map((s) => [s.token, s.capabilities.workspaces, s.capabilities.repoRoots])).toEqual([
      [acme, ['ACME'], []],
      [globex, [], [configHome]],
    ])
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

  describe('enroll', () => {
    const enrollmentToken = 'jfe_fleet_0123456789'
    const enroll = (...args: string[]) => run('runner', 'enroll', '--url', instance.url, '--token', enrollmentToken, ...args)

    function answer(status: number, body?: unknown) {
      instance.on((r) => (r.path.endsWith('/runner/enroll') ? { status, body } : undefined))
    }

    it('trades the enrollment token for a secret and stores it as an ephemeral profile', async () => {
      await register(globex)
      answer(200, { runnerId: 'r9', name: 'fleet-1', secret: acme })

      await enroll('--name', 'fleet-1', '--tag', 'GPU', '--tag', 'docker,gpu')

      const [request] = instance.to('/runner/enroll')
      expect(request?.authorization).toBe(`Bearer ${enrollmentToken}`)
      expect(request?.body).toEqual({ name: 'fleet-1', tags: ['docker', 'gpu'] })
      // Then it is a registration like any other: hello with the new secret, reporting the tags.
      const hello = instance.to('/runner/hello').at(-1)
      expect(hello?.authorization).toBe(`Bearer ${acme}`)
      expect((hello?.body as { capabilities: { tags?: string[] } }).capabilities.tags).toEqual(['docker', 'gpu'])
      expect(read().profiles).toEqual([
        { organization: 'globex', url: instance.url, token: globex, workspaces: {}, repoRoots: [] },
        { organization: 'acme', url: instance.url, token: acme, workspaces: {}, repoRoots: [], ephemeral: true },
      ])
    })

    it('sends a null name and no tags when none are given, and reports no tags on hello', async () => {
      answer(200, { runnerId: 'r9', name: 'picked-by-server', secret: acme })
      await enroll()
      expect(instance.to('/runner/enroll')[0]?.body).toEqual({ name: null, tags: [] })
      expect((instance.to('/runner/hello')[0]?.body as { capabilities: object }).capabilities).not.toHaveProperty('tags')
    })

    it.each([
      [401, { title: 'Unauthorized' }, ExitCode.Auth, 'The enrollment token is not valid, or fleet mode is off for that organization.'],
      [409, { title: 'Conflict' }, ExitCode.Conflict, 'A runner named "fleet-1" already exists in that organization. Pass another --name.'],
      [
        400,
        { title: 'The request is invalid.', errors: { name: ['Use 100 characters or fewer.'] } },
        ExitCode.Validation,
        'The request is invalid.\n  name: Use 100 characters or fewer.',
      ],
    ])('explains a %i and writes nothing', async (status, body, exitCode, message) => {
      answer(status, body)
      const error = await enroll('--name', 'fleet-1').catch((e: unknown) => e)
      expect(error).toBeInstanceOf(CliError)
      expect((error as CliError).exitCode).toBe(exitCode)
      expect((error as CliError).message).toBe(message)
      expect(instance.to('/runner/hello')).toEqual([])
      expect(() => read()).toThrow()
    })

    it('refuses a token that is not an enrollment token, and a bad tag, before calling the instance', async () => {
      await expect(run('runner', 'enroll', '--url', instance.url, '--token', acme)).rejects.toThrow(/starts with jfe_/)
      await expect(enroll('--tag', 'not ok')).rejects.toThrow(/Tags are lower case letters/)
      expect(instance.to('/runner/enroll')).toEqual([])
    })
  })
})

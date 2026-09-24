import { mkdtempSync, readFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createProgram } from '../../src/program.js'
import { hello } from './helpers.js'
import { FakeInstance } from './fake-instance.js'

describe('aictiq runner register', () => {
  let instance: FakeInstance
  let configHome: string

  beforeEach(async () => {
    instance = await new FakeInstance()
      .on((r) => (r.path.endsWith('/runner/hello') ? { status: 200, body: hello } : undefined))
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

  // Parsed through the real program: the root command also defines --url and --token, and
  // commander hands them to the root rather than to the subcommand.
  it('reads --url and --token after the subcommand, verifies them and writes runner.json', async () => {
    await createProgram().parseAsync(
      ['runner', 'register', '--url', instance.url, '--token', 'jrn_secret_0123456789'],
      { from: 'user' },
    )

    expect(instance.to('/runner/hello')[0]?.authorization).toBe('Bearer jrn_secret_0123456789')
    const written = JSON.parse(
      readFileSync(join(configHome, 'aictiq', 'runner.json'), 'utf8'),
    ) as Record<string, unknown>
    expect(written).toMatchObject({
      url: instance.url,
      token: 'jrn_secret_0123456789',
      workspaces: {},
      repoRoots: [],
    })
  })

  it('adds and removes a repository root, keeping the existing registration', async () => {
    await createProgram().parseAsync(
      ['runner', 'register', '--url', instance.url, '--token', 'jrn_secret_0123456789'],
      { from: 'user' },
    )
    const read = () =>
      JSON.parse(readFileSync(join(configHome, 'aictiq', 'runner.json'), 'utf8')) as {
        token: string
        repoRoots: string[]
      }

    await createProgram().parseAsync(['runner', 'root', configHome], { from: 'user' })
    await createProgram().parseAsync(['runner', 'root', configHome], { from: 'user' })
    expect(read().repoRoots).toEqual([configHome])
    expect(read().token).toBe('jrn_secret_0123456789')

    await createProgram().parseAsync(['runner', 'root', configHome, '--remove'], { from: 'user' })
    expect(read().repoRoots).toEqual([])
  })
})

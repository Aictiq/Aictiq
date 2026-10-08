import { chmodSync, mkdirSync, mkdtempSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { describe, expect, it } from 'vitest'
import { describeMissing, probeAllHarnesses } from '../../src/runner/harness/index.js'
import { findOnPath, harnessDirs, pathKey, withHarnessDirs } from '../../src/runner/path.js'

const unix = process.platform !== 'win32'

function script(dir: string, name: string, body: string): void {
  mkdirSync(dir, { recursive: true })
  const file = join(dir, name)
  writeFileSync(file, `#!/bin/sh\n${body}\n`)
  chmodSync(file, 0o755)
}

describe('harness directories', () => {
  it('lists the per-user directories harness installers use', () => {
    const dirs = harnessDirs(
      { NVM_BIN: '/home/mik/.nvm/versions/node/v22.0.0/bin', PNPM_HOME: '/home/mik/pnpm' },
      'linux',
      '/home/mik',
      '/usr/bin/node',
    )
    expect(dirs).toEqual([
      '/home/mik/.local/bin',
      '/home/mik/.npm-global/bin',
      '/home/mik/.bun/bin',
      '/home/mik/.opencode/bin',
      '/home/mik/.nvm/versions/node/v22.0.0/bin',
      '/home/mik/pnpm',
      '/usr/bin',
    ])
    expect(harnessDirs({}, 'darwin', '/Users/ana', '/opt/homebrew/bin/node')).toEqual(
      expect.arrayContaining(['/Users/ana/Library/pnpm', '/opt/homebrew/bin', '/usr/local/bin']),
    )
  })

  it('appends only the directories that exist and are missing, after the given PATH', () => {
    const exists = (dir: string) => dir !== '/home/mik/.bun/bin'
    expect(
      withHarnessDirs(
        '/usr/local/bin:/usr/bin:/home/mik/.npm-global/bin/',
        ['/home/mik/.local/bin', '/home/mik/.npm-global/bin', '/home/mik/.bun/bin', '/usr/bin'],
        ':',
        exists,
      ),
    ).toBe('/usr/local/bin:/usr/bin:/home/mik/.npm-global/bin/:/home/mik/.local/bin')
  })

  it('finds the PATH variable whatever its spelling', () => {
    expect(pathKey({ Path: 'C:\\Windows' })).toBe('Path')
    expect(pathKey({})).toBe('PATH')
  })
})

describe.runIf(unix)('a harness installed after the service was generated', () => {
  it('is found in ~/.local/bin, and each missing harness says why', async () => {
    const home = mkdtempSync(join(tmpdir(), 'aictiq-home-'))
    const bin = join(home, '.local', 'bin')
    script(bin, 'claude', 'echo "2.1.0 (Claude Code)"')
    script(bin, 'codex', 'exit 1')
    // The PATH a unit generated before the install froze.
    const frozen = { PATH: '/nonexistent' } as NodeJS.ProcessEnv

    const before = await probeAllHarnesses(frozen)
    expect(before.harnesses).toEqual([])
    expect(before.missing.find((m) => m.name === 'claude')).toEqual({
      name: 'claude',
      reason: 'not-on-path',
      command: 'claude',
    })

    const env = {
      PATH: withHarnessDirs(frozen.PATH!, harnessDirs({}, 'linux', home, '/nonexistent/node')),
    } as NodeJS.ProcessEnv
    expect(findOnPath('claude', env.PATH!)).toBe(join(bin, 'claude'))
    const after = await probeAllHarnesses(env)
    expect(after.harnesses).toEqual([{ name: 'claude', version: '2.1.0 (Claude Code)' }])
    const codex = after.missing.find((m) => m.name === 'codex')!
    expect(codex).toEqual({ name: 'codex', reason: 'version-failed', command: 'codex' })
    expect(describeMissing(codex)).toBe('`codex --version` failed')
    expect(after.missing.map((m) => m.name)).toEqual(['codex', 'opencode', 'cursor', 'copilot'])
  })
})

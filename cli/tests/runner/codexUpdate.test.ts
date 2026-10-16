import { mkdirSync, mkdtempSync, symlinkSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { describe, expect, it } from 'vitest'
import {
  CodexUpdater,
  npmCodexPackage,
  parseCodexVersion,
  type CodexUpdaterOptions,
} from '../../src/runner/codexUpdate.js'
import { codex } from '../../src/runner/harness/codex.js'
import {
  UpdateCheckIntervalMs,
  UpdateRetryMs,
  type CommandResult,
  type RunCommand,
} from '../../src/runner/update.js'

const ok = (stdout = ''): CommandResult => ({ code: 0, stdout, stderr: '' })

/** A global npm prefix with Codex installed in it and its bin link, as npm lays them out. */
function npmPrefix(): { root: string; bin: string } {
  const prefix = mkdtempSync(join(tmpdir(), 'aictiq-codex-'))
  const root = join(prefix, 'lib', 'node_modules')
  const packageDir = join(root, '@openai', 'codex', 'bin')
  mkdirSync(packageDir, { recursive: true })
  writeFileSync(join(packageDir, 'codex.js'), '')
  mkdirSync(join(prefix, 'bin'))
  const bin = join(prefix, 'bin', 'codex')
  symlinkSync(join(packageDir, 'codex.js'), bin)
  return { root, bin }
}

/** `npm root -g`, `npm install -g` and `codex --version` for a machine with Codex `installed`. */
function fakeNpm(root: string, state: { installed: string; fail?: string }) {
  const calls: string[][] = []
  const run: RunCommand = async (command, args) => {
    calls.push([command, ...args])
    if (command === 'codex') return ok(`codex-cli ${state.installed}\n`)
    if (command === 'npm' && args[0] === 'root') return ok(`${root}\n`)
    if (command === 'npm' && args[0] === 'install') {
      if (state.fail)
        return { code: 243, stdout: '', stderr: `npm error code EACCES\n${state.fail}` }
      state.installed = args.at(-1)!.split('@').at(-1)!
      return ok()
    }
    return { code: 1, stdout: '', stderr: 'unexpected' }
  }
  return { run, calls, installs: () => calls.filter((c) => c[1] === 'install') }
}

function registry(latest: string | null) {
  const fetched: string[] = []
  const fetchImpl = (async (url: string) => {
    fetched.push(url)
    if (latest === null) throw new Error('getaddrinfo ENOTFOUND registry.npmjs.org')
    return new Response(JSON.stringify({ latest }), { status: 200 })
  }) as unknown as typeof fetch
  return { fetchImpl, fetched }
}

function updater(
  overrides: Partial<CodexUpdaterOptions> & { latest?: string | null; installed?: string },
) {
  const { root, bin } = npmPrefix()
  const state: { installed: string; fail?: string } = {
    installed: overrides.installed ?? '0.160.0',
  }
  const npm = fakeNpm(root, state)
  const { fetchImpl, fetched } = registry(
    overrides.latest === undefined ? '0.162.1' : overrides.latest,
  )
  const logs: string[] = []
  let now = 1_000_000
  const instance = new CodexUpdater({
    log: (message) => logs.push(message),
    autoUpdate: () => true,
    env: {},
    fetch: fetchImpl,
    run: npm.run,
    locate: () => bin,
    writable: () => true,
    now: () => now,
    ...overrides,
  })
  return {
    instance,
    state,
    npm,
    fetched,
    logs,
    advance: (ms: number) => {
      now += ms
    },
  }
}

describe('CodexUpdater', () => {
  it('updates an outdated npm install before the run and logs old → new', async () => {
    const { instance, state, npm, fetched, logs } = updater({})
    let versionDuringRun = ''
    await instance.run(async () => {
      versionDuringRun = state.installed
    })
    expect(fetched[0]).toBe('https://registry.npmjs.org/-/package/@openai%2Fcodex/dist-tags')
    expect(npm.installs()).toEqual([
      ['npm', 'install', '--global', '--no-fund', '--no-audit', '@openai/codex@0.162.1'],
    ])
    expect(versionDuringRun).toBe('0.162.1')
    expect(logs).toContain('Updated Codex 0.160.0 → 0.162.1')
  })

  it('installs nothing when up to date, and checks again only after the interval', async () => {
    const { instance, npm, fetched, advance } = updater({ installed: '0.162.1' })
    await instance.run(async () => {})
    await instance.run(async () => {})
    expect(fetched).toHaveLength(1)
    expect(npm.installs()).toEqual([])
    advance(UpdateCheckIntervalMs)
    await instance.run(async () => {})
    expect(fetched).toHaveLength(2)
  })

  it('carries on when the registry is unreachable, and retries sooner', async () => {
    const { instance, npm, fetched, logs, advance } = updater({ latest: null })
    await expect(instance.run(async () => 'ran')).resolves.toBe('ran')
    expect(npm.installs()).toEqual([])
    expect(logs.at(-1)).toMatch(/Codex update check failed .*ENOTFOUND.*staying on 0\.160\.0/)
    advance(UpdateRetryMs)
    await instance.run(async () => {})
    expect(fetched).toHaveLength(2)
  })

  it('carries on with a warning when npm refuses the install', async () => {
    const { instance, state, logs } = updater({})
    state.fail = 'npm error path /usr/lib/node_modules/@openai/codex'
    await expect(instance.run(async () => 'ran')).resolves.toBe('ran')
    expect(state.installed).toBe('0.160.0')
    expect(logs.at(-1)).toMatch(
      /Codex update to 0\.162\.1 failed: .*exited 243: npm error code EACCES.*staying on 0\.160\.0/,
    )
  })

  it('leaves a global npm install this user cannot write alone', async () => {
    const { instance, npm, logs } = updater({ writable: () => false })
    await instance.run(async () => {})
    expect(npm.installs()).toEqual([])
    expect(logs.at(-1)).toMatch(/is not writable by this user/)
  })

  it('leaves Codex installed some other way alone', async () => {
    const { instance, npm, logs } = updater({ locate: () => '/opt/homebrew/bin/codex' })
    await instance.run(async () => {})
    expect(npm.installs()).toEqual([])
    expect(logs.at(-1)).toMatch(
      /\/opt\/homebrew\/bin\/codex is not a global npm install; update it by hand/,
    )
  })

  it('only logs a newer version with auto-update off', async () => {
    const { instance, npm, logs } = updater({ autoUpdate: () => false })
    await instance.run(async () => {})
    expect(npm.installs()).toEqual([])
    expect(logs.at(-1)).toBe(
      'Codex update check: @openai/codex 0.162.1 is available (installed 0.160.0); auto-update is off',
    )
  })

  it('never checks or updates while another Codex run is in flight', async () => {
    const { instance, fetched, advance } = updater({ installed: '0.162.1' })
    let finish!: () => void
    const first = instance.run(() => new Promise<void>((resolve) => (finish = resolve)))
    await new Promise((resolve) => setTimeout(resolve, 0))
    expect(fetched).toHaveLength(1)
    advance(UpdateCheckIntervalMs)
    await instance.run(async () => {})
    expect(fetched).toHaveLength(1)
    finish()
    await first
    await instance.run(async () => {})
    expect(fetched).toHaveLength(2)
  })

  it('makes runs that arrive during an install wait for it', async () => {
    const { instance, state } = updater({})
    const seen: string[] = []
    await Promise.all([
      instance.run(async () => void seen.push(state.installed)),
      instance.run(async () => void seen.push(state.installed)),
    ])
    expect(seen).toEqual(['0.162.1', '0.162.1'])
  })
})

describe('npmCodexPackage', () => {
  it('recognizes the bin link of a global npm install', async () => {
    const { root, bin } = npmPrefix()
    const { run } = fakeNpm(root, { installed: '0.1.0' })
    expect(await npmCodexPackage(bin, run, 'linux')).toMatch(/@openai[\\/]codex$/)
    expect(await npmCodexPackage('/usr/local/bin/codex', run, 'linux')).toBeNull()
  })

  it('recognizes npm’s shim in the prefix on Windows', async () => {
    const run: RunCommand = async () => ok('C:\\Users\\a\\AppData\\Roaming\\npm\\node_modules\n')
    expect(
      await npmCodexPackage('C:\\Users\\a\\AppData\\Roaming\\npm\\codex.cmd', run, 'win32'),
    ).not.toBeNull()
    expect(await npmCodexPackage('C:\\Tools\\codex.exe', run, 'win32')).toBeNull()
  })
})

describe('Codex invocation', () => {
  it('reads the version out of `codex --version`', () => {
    expect(parseCodexVersion('codex-cli 0.162.1')).toBe('0.162.1')
    expect(parseCodexVersion('codex-cli 0.163.0-alpha.2\n')).toBe('0.163.0-alpha.2')
    expect(parseCodexVersion('command not found')).toBeNull()
  })

  it('turns off Codex’s own update check in a run', () => {
    const { args } = codex.invocation({
      workspace: '/w',
      prompt: 'p',
      mcpServer: { command: 'aictiq', args: ['mcp', '--stdio'] },
    } as Parameters<typeof codex.invocation>[0])
    expect(args.join(' ')).toContain('-c check_for_update_on_startup=false')
  })
})

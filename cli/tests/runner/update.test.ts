import { mkdirSync, mkdtempSync, readFileSync, symlinkSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { describe, expect, it } from 'vitest'
import { readRunnerConfig, writeRunnerConfig } from '../../src/runner/config.js'
import { systemdUnit, UpdateExitCode } from '../../src/runner/service.js'
import {
  compareVersions,
  detectInstallation,
  installArgs,
  latestVersion,
  SelfUpdater,
  type CommandResult,
  type RunCommand,
} from '../../src/runner/update.js'

const ok = (stdout = ''): CommandResult => ({ code: 0, stdout, stderr: '' })

/** A global npm prefix with the CLI installed in it, as `npm root -g` would name it. */
function npmPrefix(): { root: string; packageRoot: string } {
  const root = join(mkdtempSync(join(tmpdir(), 'aictiq-update-')), 'lib', 'node_modules')
  const packageRoot = join(root, '@aictiq', 'cli')
  mkdirSync(packageRoot, { recursive: true })
  return { root, packageRoot }
}

/** Answers `npm root -g`, installs, and `--version` like an npm whose global CLI is `installed`. */
function fakeNpm(root: string, state: { installed: string; fail?: string }) {
  const calls: string[][] = []
  const run: RunCommand = async (command, args) => {
    calls.push([command, ...args])
    if (command === 'npm' && args[0] === 'root') return ok(`${root}\n`)
    if (args.at(-1) === '--version') return ok(`${state.installed}\n`)
    if (command === 'npm' && args[0] === 'install') {
      if (state.fail)
        return { code: 243, stdout: '', stderr: `npm error code EACCES\n${state.fail}` }
      state.installed = args.at(-1)!.split('@').at(-1)!
      return ok()
    }
    return { code: 1, stdout: '', stderr: 'unexpected' }
  }
  return { run, calls }
}

describe('compareVersions', () => {
  it('orders releases numerically and pre-releases before their release', () => {
    expect(compareVersions('0.10.0', '0.9.9')).toBe(1)
    expect(compareVersions('0.6.0', '0.6.0')).toBe(0)
    expect(compareVersions('0.6.0', '1.0.0')).toBe(-1)
    expect(compareVersions('1.0.0-beta.2', '1.0.0')).toBe(-1)
    expect(compareVersions('1.0.0-beta.10', '1.0.0-beta.2')).toBe(1)
  })

  it('never ranks garbage above a version', () => {
    expect(compareVersions('not-a-version', '0.0.1')).toBe(-1)
    expect(compareVersions('0.0.1', '')).toBe(1)
  })
})

describe('latestVersion', () => {
  it('reads the latest dist-tag from the configured registry', async () => {
    const urls: string[] = []
    const fetchImpl = (async (url: string) => {
      urls.push(url)
      return new Response(JSON.stringify({ latest: '0.7.0', next: '0.8.0-rc.1' }))
    }) as unknown as typeof fetch
    await expect(
      latestVersion({ npm_config_registry: 'https://npm.example/' }, fetchImpl),
    ).resolves.toBe('0.7.0')
    expect(urls).toEqual(['https://npm.example/-/package/@aictiq%2Fcli/dist-tags'])
  })

  it('fails with a reason when the registry is unreachable or has no version', async () => {
    const offline = (async () => {
      throw new Error('getaddrinfo ENOTFOUND')
    }) as unknown as typeof fetch
    await expect(latestVersion({}, offline)).rejects.toThrow(
      /cannot reach https:\/\/registry\.npmjs\.org: getaddrinfo/,
    )
    const missing = (async () => new Response('{}', { status: 404 })) as unknown as typeof fetch
    await expect(latestVersion({}, missing)).rejects.toThrow(/answered 404/)
    const empty = (async () => new Response('{}')) as unknown as typeof fetch
    await expect(latestVersion({}, empty)).rejects.toThrow(/no latest version/)
  })
})

describe('detectInstallation', () => {
  it('recognizes a global npm install, also through a symlinked prefix', async () => {
    const { root, packageRoot } = npmPrefix()
    const linked = join(mkdtempSync(join(tmpdir(), 'aictiq-link-')), 'node_modules')
    symlinkSync(root, linked)
    const { run } = fakeNpm(linked, { installed: '0.6.0' })
    await expect(detectInstallation(packageRoot, '/usr/bin/aictiq', run)).resolves.toEqual({
      manager: 'npm',
      entry: '/usr/bin/aictiq',
    })
  })

  it('recognizes pnpm from its store or global directory', async () => {
    const packageRoot = join(
      mkdtempSync(join(tmpdir(), 'aictiq-')),
      'pnpm',
      'store',
      'v11',
      'links',
      '@aictiq',
      'cli',
    )
    await expect(detectInstallation(packageRoot, '/e', async () => ok())).resolves.toEqual({
      manager: 'pnpm',
      entry: '/e',
    })
  })

  it('rejects a checkout or a package outside the global prefix', async () => {
    const { run } = fakeNpm('/usr/lib/node_modules', { installed: '0.6.0' })
    await expect(detectInstallation('/home/ana/src/aictiq/cli', '/e', run)).resolves.toBeNull()
    const { packageRoot } = npmPrefix() // an npx cache looks the same, but npm root -g is elsewhere
    await expect(detectInstallation(packageRoot, '/e', run)).resolves.toBeNull()
  })

  it('installs with the same manager, globally and without prompts', () => {
    expect(installArgs('npm', '0.7.0')).toEqual([
      'install',
      '--global',
      '--no-fund',
      '--no-audit',
      '@aictiq/cli@0.7.0',
    ])
    expect(installArgs('pnpm', '0.7.0')).toEqual(['add', '--global', '@aictiq/cli@0.7.0'])
  })
})

describe('SelfUpdater', () => {
  const updater = (
    current: string,
    packageRoot: string,
    run: RunCommand,
    latest = '0.7.0',
    writable?: (dir: string) => boolean,
  ) =>
    new SelfUpdater({
      current,
      packageRoot,
      entry: '/usr/bin/aictiq',
      log: () => {},
      run,
      ...(writable ? { writable } : {}),
      fetch: (async () => new Response(JSON.stringify({ latest }))) as unknown as typeof fetch,
    })

  it('reports a newer version, and none when the running one is the latest or newer', async () => {
    const run = async () => ok()
    await expect(updater('0.6.0', '/x', run).check()).resolves.toMatchObject({
      status: 'newer',
      latest: '0.7.0',
    })
    await expect(updater('0.7.0', '/x', run).check()).resolves.toMatchObject({ status: 'current' })
    await expect(updater('0.8.0-dev', '/x', run).check()).resolves.toMatchObject({
      status: 'current',
    })
  })

  it('installs the version and confirms the entry now reports it', async () => {
    const { root, packageRoot } = npmPrefix()
    const npm = fakeNpm(root, { installed: '0.6.0' })
    await expect(updater('0.6.0', packageRoot, npm.run).install('0.7.0')).resolves.toEqual({
      from: '0.6.0',
      to: '0.7.0',
    })
    expect(npm.calls.filter(([, verb]) => verb === 'install')).toHaveLength(1)
  })

  it('skips the install when the version is already on disk', async () => {
    const { root, packageRoot } = npmPrefix()
    const npm = fakeNpm(root, { installed: '0.7.0' })
    await expect(updater('0.6.0', packageRoot, npm.run).install('0.7.0')).resolves.toEqual({
      from: '0.6.0',
      to: '0.7.0',
    })
    expect(npm.calls.some(([, verb]) => verb === 'install')).toBe(false)
  })

  it('fails with the package manager’s reason when the install is refused', async () => {
    const { root, packageRoot } = npmPrefix()
    const npm = fakeNpm(root, {
      installed: '0.6.0',
      fail: 'permission denied, mkdir /usr/lib/node_modules',
    })
    await expect(updater('0.6.0', packageRoot, npm.run).install('0.7.0')).rejects.toThrow(
      /npm install --global .* failed \(exit 243\): npm error code EACCES \/ permission denied/,
    )
  })

  it('refuses a global npm install this user cannot write before trying it', async () => {
    const { root, packageRoot } = npmPrefix()
    const npm = fakeNpm(root, { installed: '0.6.0' })
    await expect(
      updater('0.6.0', packageRoot, npm.run, '0.7.0', () => false).installation(),
    ).rejects.toThrow(/is not writable by this user \(installed with sudo\?\)/)
    expect(npm.calls.some(([, verb]) => verb === 'install')).toBe(false)
  })

  it('refuses a CLI that is not a global install', async () => {
    await expect(
      updater('0.6.0', '/home/ana/src/aictiq/cli', async () => ok('/usr/lib/node_modules')).install(
        '0.7.0',
      ),
    ).rejects.toThrow(/not a global npm or pnpm install/)
  })
})

describe('restart and opt-out', () => {
  it('has systemd start the runner again after an upgrade', () => {
    const unit = systemdUnit({ node: '/n', entry: '/e', parallel: '1', path: '/bin', home: '/h' })
    expect(unit).toContain('Restart=on-failure')
    expect(unit).toContain(`RestartForceExitStatus=${UpdateExitCode}`)
  })

  it('keeps autoUpdate in runner.json', () => {
    const path = join(mkdtempSync(join(tmpdir(), 'aictiq-runner-')), 'runner.json')
    const base = {
      machineId: '4d1c7f2e-9b1a-4c3e-8f5d-2a6b7c8d9e0f',
      profiles: [],
      attachments: { maxCount: 1, maxBytes: 1 },
    }
    writeRunnerConfig({ ...base, autoUpdate: false }, path)
    expect(JSON.parse(readFileSync(path, 'utf8'))).toMatchObject({ autoUpdate: false })
    expect(readRunnerConfig(path)?.autoUpdate).toBe(false)
    writeRunnerConfig(base, path)
    expect(readRunnerConfig(path)?.autoUpdate).toBeUndefined()
  })
})

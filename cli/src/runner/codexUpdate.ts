import { dirname, join, sep, win32 } from 'node:path'
import { findOnPath, pathKey } from './path.js'
import {
  compareVersions,
  firstLines,
  InstallTimeoutMs,
  isWritable,
  latestVersion,
  realOrSelf,
  RegistryTimeoutMs,
  runCommand,
  UpdateCheckIntervalMs,
  UpdateRetryMs,
  type RunCommand,
} from './update.js'

export const CodexPackage = '@openai/codex'

export interface CodexUpdaterOptions {
  log: (message: string) => void
  /** Read at each check, like the CLI's own: `"autoUpdate": false` applies without a restart. */
  autoUpdate: () => boolean
  env?: NodeJS.ProcessEnv
  fetch?: typeof fetch
  run?: RunCommand
  /** The `codex` executable `execFile` would start, or null. */
  locate?: () => string | null
  /** Whether this process may replace files in `dir`. */
  writable?: (dir: string) => boolean
  now?: () => number
}

/**
 * Keeps an npm-installed Codex on the registry's `latest` before Codex runs. The check runs
 * before a Codex run at most every {@link UpdateCheckIntervalMs}, and only while no other Codex
 * run of this runner is in flight: `npm install -g` replaces the binary under a running one.
 * Runs that arrive during an install wait for it. Nothing here throws: every failure is logged
 * and the run goes ahead on the installed version.
 */
export class CodexUpdater {
  private readonly options: CodexUpdaterOptions
  private active = 0
  private updating: Promise<void> | null = null
  private nextCheckAt = 0

  constructor(options: CodexUpdaterOptions) {
    this.options = options
  }

  /** Runs `task`, a Codex run, after the update check when one is due. */
  async run<T>(task: () => Promise<T>): Promise<T> {
    await this.beforeRun()
    this.active++
    try {
      return await task()
    } finally {
      this.active--
    }
  }

  private async beforeRun(): Promise<void> {
    while (this.updating) await this.updating
    const now = (this.options.now ?? Date.now)()
    if (this.active > 0 || now < this.nextCheckAt) return
    this.nextCheckAt = now + UpdateCheckIntervalMs
    this.updating = this.check()
      .catch((error: unknown) => {
        this.options.log(
          `Codex update check failed (${error instanceof Error ? error.message : String(error)}); continuing`,
        )
      })
      .finally(() => {
        this.updating = null
      })
    await this.updating
  }

  private async check(): Promise<void> {
    const { log, run = runCommand } = this.options
    const env = this.options.env ?? process.env
    const current = await codexVersion(run)
    if (!current) return
    let latest: string
    try {
      latest = await latestVersion(env, this.options.fetch, CodexPackage)
    } catch (error) {
      this.nextCheckAt = (this.options.now ?? Date.now)() + UpdateRetryMs
      log(
        `Codex update check failed (${error instanceof Error ? error.message : String(error)}); staying on ${current}`,
      )
      return
    }
    if (compareVersions(latest, current) <= 0) {
      log(`Codex update check: ${current} is up to date (latest on npm: ${latest})`)
      return
    }
    if (!this.options.autoUpdate()) {
      log(
        `Codex update check: ${CodexPackage} ${latest} is available (installed ${current}); auto-update is off`,
      )
      return
    }
    const executable = (this.options.locate ?? (() => locateCodex(env)))()
    const packageDir = executable ? await npmCodexPackage(executable, run) : null
    if (!packageDir) {
      log(
        `Codex update check: ${CodexPackage} ${latest} is available (installed ${current}), but ${executable ?? 'codex'} is not a global npm install; update it by hand`,
      )
      return
    }
    // npm renames the package directory aside before it installs, which needs the scope writable.
    const scope = dirname(packageDir)
    if (!(this.options.writable ?? isWritable)(scope)) {
      log(
        `Codex update check: ${CodexPackage} ${latest} is available (installed ${current}), but ${scope} is not writable by this user (installed with sudo?); update it by hand with \`sudo npm install -g ${CodexPackage}\``,
      )
      return
    }
    log(`Installing ${CodexPackage}@${latest} with npm (installed ${current})`)
    const args = ['install', '--global', '--no-fund', '--no-audit', `${CodexPackage}@${latest}`]
    const result = await run('npm', args, InstallTimeoutMs)
    if (result.code !== 0) {
      const detail = firstLines(result.stderr || result.stdout)
      log(
        `Codex update to ${latest} failed: \`npm ${args.join(' ')}\` exited ${result.code ?? 'abnormally'}${detail ? `: ${detail}` : ''}; staying on ${current}`,
      )
      return
    }
    const after = await codexVersion(run)
    log(
      after && compareVersions(after, latest) >= 0
        ? `Updated Codex ${current} → ${after}`
        : `Codex update to ${latest}: npm finished, but codex reports ${after ?? 'no version'}`,
    )
  }
}

/** `codex-cli 0.162.1` → `0.162.1`; null when Codex is missing or answers something else. */
export function parseCodexVersion(output: string): string | null {
  const match = /(\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?)/.exec(output)
  return match ? match[1]! : null
}

async function codexVersion(run: RunCommand): Promise<string | null> {
  const result = await run('codex', ['--version'], RegistryTimeoutMs).catch(() => null)
  return result?.code === 0 ? parseCodexVersion(result.stdout) : null
}

function locateCodex(env: NodeJS.ProcessEnv): string | null {
  return findOnPath('codex', env[pathKey(env)] ?? '', process.platform, env)
}

/**
 * The `@openai/codex` package directory under `npm root -g` that `executable` starts, or null
 * for Codex installed any other way (Homebrew, the standalone installer, pnpm, bun). On Unix
 * the bin link resolves into the package; on Windows npm's `codex.cmd` shim sits in the prefix,
 * the directory above `node_modules`.
 */
export async function npmCodexPackage(
  executable: string,
  run: RunCommand = runCommand,
  platform: NodeJS.Platform = process.platform,
): Promise<string | null> {
  const root = await run('npm', ['root', '-g'], RegistryTimeoutMs).catch(() => null)
  const globalRoot = root?.code === 0 ? root.stdout.trim() : ''
  if (!globalRoot) return null
  if (platform === 'win32') {
    const shimDir = win32.dirname(executable).toLowerCase()
    return shimDir === win32.dirname(globalRoot).toLowerCase()
      ? win32.join(globalRoot, '@openai', 'codex')
      : null
  }
  const packageDir = join(realOrSelf(globalRoot), '@openai', 'codex')
  return realOrSelf(executable).startsWith(packageDir + sep) ? packageDir : null
}

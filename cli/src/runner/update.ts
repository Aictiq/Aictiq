import { execFile, spawn } from 'node:child_process'
import { realpathSync } from 'node:fs'
import { resolve, sep } from 'node:path'

export const PackageName = '@aictiq/cli'

/** How often a running runner asks the registry again. */
export const UpdateCheckIntervalMs = 6 * 60 * 60 * 1000

/**
 * How soon a check that could not reach the registry is tried again. A runner started at boot
 * often checks before the network is up, and waiting the full interval after that would leave
 * it on the old version for hours.
 */
export const UpdateRetryMs = 5 * 60 * 1000

const RegistryTimeoutMs = 15_000
const InstallTimeoutMs = 5 * 60 * 1000

export type PackageManager = 'npm' | 'pnpm'

export interface Installation {
  manager: PackageManager
  /**
   * The entry script as this process was started (`process.argv[1]`), not its real path: npm's
   * bin symlink and pnpm's global directory link both stay put across an upgrade, so running
   * it again - by hand, from a service definition, or here - starts whatever is installed now.
   */
  entry: string
}

export type CheckResult =
  | { status: 'unavailable'; current: string; reason: string }
  | { status: 'current'; current: string; latest: string }
  | { status: 'newer'; current: string; latest: string }

export class UpdateError extends Error {
  constructor(message: string) {
    super(message)
    this.name = 'UpdateError'
  }
}

/**
 * Orders `major.minor.patch[-pre]`: a pre-release sorts before its release, and anything that
 * is not a version sorts lowest, so a garbled answer from a registry never looks newer.
 */
export function compareVersions(left: string, right: string): number {
  const parse = (value: string) => {
    const match = /^v?(\d+)\.(\d+)\.(\d+)(?:-([0-9A-Za-z.-]+))?(?:\+.*)?$/.exec(value.trim())
    return match
      ? { parts: [Number(match[1]), Number(match[2]), Number(match[3])], pre: match[4] }
      : null
  }
  const a = parse(left)
  const b = parse(right)
  if (!a || !b) return a ? 1 : b ? -1 : 0
  for (let i = 0; i < 3; i++) {
    const diff = a.parts[i]! - b.parts[i]!
    if (diff !== 0) return Math.sign(diff)
  }
  if (a.pre === b.pre) return 0
  if (a.pre === undefined) return 1
  if (b.pre === undefined) return -1
  return a.pre.localeCompare(b.pre, 'en', { numeric: true })
}

/** The `latest` dist-tag of the CLI on the registry npm itself would use. */
export async function latestVersion(
  env: NodeJS.ProcessEnv = process.env,
  fetchImpl: typeof fetch = fetch,
): Promise<string> {
  const registry = (env.npm_config_registry || 'https://registry.npmjs.org/').replace(/\/+$/, '')
  const url = `${registry}/-/package/${PackageName.replaceAll('/', '%2F')}/dist-tags`
  let response: Response
  try {
    response = await fetchImpl(url, {
      headers: { accept: 'application/json' },
      signal: AbortSignal.timeout(RegistryTimeoutMs),
    })
  } catch (error) {
    throw new UpdateError(
      `cannot reach ${registry}: ${error instanceof Error ? error.message : String(error)}`,
    )
  }
  if (!response.ok)
    throw new UpdateError(`${registry} answered ${response.status} for ${PackageName}`)
  const tags = (await response.json().catch(() => null)) as { latest?: unknown } | null
  if (typeof tags?.latest !== 'string' || compareVersions(tags.latest, '0.0.0') < 0) {
    throw new UpdateError(`${registry} has no latest version of ${PackageName}`)
  }
  return tags.latest
}

export interface CommandResult {
  code: number | null
  stdout: string
  stderr: string
}

/** Runs a command without a terminal: an install that wants to prompt fails instead of hanging. */
export type RunCommand = (
  command: string,
  args: string[],
  timeoutMs: number,
) => Promise<CommandResult>

export const runCommand: RunCommand = (command, args, timeoutMs) =>
  new Promise((resolve) => {
    // npm and pnpm are .cmd shims on Windows, which only a shell starts.
    const child = execFile(
      command,
      args,
      {
        timeout: timeoutMs,
        maxBuffer: 4 * 1024 * 1024,
        shell: process.platform === 'win32',
        windowsHide: true,
      },
      (error, stdout, stderr) => {
        const code = error ? (typeof error.code === 'number' ? error.code : null) : 0
        resolve({ code, stdout: String(stdout), stderr: String(stderr) || (error?.message ?? '') })
      },
    )
    child.stdin?.end()
  })

/**
 * How this CLI was installed, from the real path of its package: a global npm install lives
 * under `npm root -g`, a pnpm one under pnpm's store or global directory. Anything else - a
 * checkout, `npx`, a project dependency - is null: there is no global install to upgrade.
 */
export async function detectInstallation(
  packageRoot: string,
  entry: string,
  run: RunCommand = runCommand,
): Promise<Installation | null> {
  const real = realOrSelf(packageRoot)
  const segments = real.split(/[\\/]+/)
  if (segments.includes('.pnpm') || segments.includes('pnpm')) return { manager: 'pnpm', entry }
  if (!segments.join('/').endsWith('node_modules/@aictiq/cli')) return null
  const root = await run('npm', ['root', '-g'], RegistryTimeoutMs).catch(() => null)
  const globalRoot = root?.code === 0 ? root.stdout.trim() : ''
  if (!globalRoot) return null
  const prefix = realOrSelf(globalRoot)
  return real.startsWith(prefix.endsWith(sep) ? prefix : prefix + sep)
    ? { manager: 'npm', entry }
    : null
}

export function installArgs(manager: PackageManager, version: string): string[] {
  const spec = `${PackageName}@${version}`
  return manager === 'pnpm'
    ? ['add', '--global', spec]
    : ['install', '--global', '--no-fund', '--no-audit', spec]
}

/** Installs `version` globally with the manager the CLI came from. Throws {@link UpdateError}. */
export async function installVersion(
  installation: Installation,
  version: string,
  run: RunCommand = runCommand,
): Promise<void> {
  const args = installArgs(installation.manager, version)
  const result = await run(installation.manager, args, InstallTimeoutMs)
  if (result.code !== 0) {
    const detail = firstLines(result.stderr || result.stdout)
    throw new UpdateError(
      `\`${installation.manager} ${args.join(' ')}\` failed${result.code === null ? '' : ` (exit ${result.code})`}${detail ? `: ${detail}` : ''}`,
    )
  }
}

/** The version a fresh start of this entry would run: what is on disk now, not what is loaded. */
export async function installedVersion(
  installation: Installation,
  run: RunCommand = runCommand,
): Promise<string | null> {
  const result = await run(
    process.execPath,
    [installation.entry, '--version'],
    RegistryTimeoutMs,
  ).catch(() => null)
  const value = result?.code === 0 ? result.stdout.trim() : ''
  return compareVersions(value, '0.0.0') >= 0 ? value : null
}

export interface SelfUpdaterOptions {
  current: string
  packageRoot: string
  entry: string
  log: (message: string) => void
  env?: NodeJS.ProcessEnv
  fetch?: typeof fetch
  run?: RunCommand
}

/**
 * Checks the registry, and installs the `latest` CLI with the package manager it came from.
 * Every failure is reported as an {@link UpdateError} for the caller to log; nothing here
 * retries, so a runner that cannot upgrade only tries again at its next check.
 */
export class SelfUpdater {
  private readonly options: SelfUpdaterOptions

  constructor(options: SelfUpdaterOptions) {
    this.options = options
  }

  async check(): Promise<CheckResult> {
    const { current } = this.options
    try {
      const latest = await latestVersion(this.options.env, this.options.fetch)
      return compareVersions(latest, current) > 0
        ? { status: 'newer', current, latest }
        : { status: 'current', current, latest }
    } catch (error) {
      return {
        status: 'unavailable',
        current,
        reason: error instanceof Error ? error.message : String(error),
      }
    }
  }

  /**
   * The global install to upgrade. Throws {@link UpdateError} for a CLI that is not one, so a
   * runner can say so before it stops claiming work for an upgrade that cannot happen.
   */
  async installation(): Promise<Installation> {
    const { packageRoot, entry, run } = this.options
    const installation = await detectInstallation(packageRoot, entry, run)
    if (!installation) {
      throw new UpdateError(
        `this CLI (${realOrSelf(packageRoot)}) is not a global npm or pnpm install; update it by hand`,
      )
    }
    return installation
  }

  /**
   * Makes `version` (or newer) what the entry starts. Skips the install when it is already on
   * disk - `aictiq runner update` ran while this runner was running - and confirms afterwards,
   * so a restart never happens for an install that did not take.
   */
  async install(version: string): Promise<{ from: string; to: string }> {
    const { current, run } = this.options
    const installation = await this.installation()
    const before = await installedVersion(installation, run)
    if (!before || compareVersions(before, version) < 0) {
      this.options.log(`Installing ${PackageName}@${version} with ${installation.manager}`)
      await installVersion(installation, version, run)
    }
    const after = await installedVersion(installation, run)
    if (!after || compareVersions(after, version) < 0) {
      throw new UpdateError(
        `${installation.manager} finished, but ${installation.entry} reports ${after ?? 'no version'} instead of ${version}`,
      )
    }
    return { from: current, to: after }
  }
}

/**
 * Starts this CLI again with the same arguments - now the upgraded one - and waits for it, for
 * a runner in a terminal. Ctrl-C reaches it from the terminal's process group, so this process
 * only swallows its own copy (passing it on would count as a second Ctrl-C and cancel runs);
 * a SIGTERM sent to this process alone is passed on.
 */
export function rerun(entry: string, args: string[]): Promise<number> {
  return new Promise((resolvePromise) => {
    const child = spawn(process.execPath, [entry, ...args], { stdio: 'inherit' })
    const ignore = () => {}
    const forward = (signal: NodeJS.Signals) => child.kill(signal)
    process.on('SIGINT', ignore)
    process.on('SIGTERM', forward)
    const done = (code: number) => {
      process.off('SIGINT', ignore)
      process.off('SIGTERM', forward)
      resolvePromise(code)
    }
    child.once('error', () => done(1))
    child.once('exit', (code) => done(code ?? 1))
  })
}

export function cliEntryPath(argv: string[] = process.argv): string {
  return resolve(argv[1] ?? 'aictiq')
}

function realOrSelf(path: string): string {
  try {
    return realpathSync(path)
  } catch {
    return resolve(path)
  }
}

/** npm and pnpm lead with the error code and path; the generic advice comes last. */
function firstLines(text: string): string {
  return text
    .split(/\r?\n/)
    .map((line) => line.trim())
    .filter(Boolean)
    .slice(0, 3)
    .join(' / ')
}

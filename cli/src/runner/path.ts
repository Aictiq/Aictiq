import { accessSync, constants, statSync } from 'node:fs'
import { homedir } from 'node:os'
import { delimiter as hostDelimiter, dirname, join } from 'node:path'

/**
 * Where harness installers put their executables per user. A login shell adds them to PATH in
 * a profile (`~/.bashrc`, `~/.zshrc`) that no service manager reads, so a runner started by
 * systemd, launchd or Task Scheduler would never see a harness installed there - and a
 * service definition's PATH is frozen when it is generated, before most harnesses are
 * installed. The runner appends the ones that exist to its own PATH on every probe instead.
 */
export function harnessDirs(
  env: NodeJS.ProcessEnv = process.env,
  platform: NodeJS.Platform = process.platform,
  home: string = homedir(),
  execPath: string = process.execPath,
): string[] {
  // The Node.js running the runner: an nvm, fnm or volta install puts `npm install -g` there.
  const node = dirname(execPath)
  if (platform === 'win32') {
    const appData = env.APPDATA ?? join(home, 'AppData', 'Roaming')
    const localAppData = env.LOCALAPPDATA ?? join(home, 'AppData', 'Local')
    return unique([
      join(home, '.local', 'bin'),
      join(appData, 'npm'),
      env.PNPM_HOME ?? join(localAppData, 'pnpm'),
      join(home, '.bun', 'bin'),
      node,
    ])
  }
  return unique([
    join(home, '.local', 'bin'),
    join(home, '.npm-global', 'bin'),
    join(home, '.bun', 'bin'),
    join(home, '.opencode', 'bin'),
    ...(env.NVM_BIN ? [env.NVM_BIN] : []),
    env.PNPM_HOME ??
      (platform === 'darwin'
        ? join(home, 'Library', 'pnpm')
        : join(home, '.local', 'share', 'pnpm')),
    node,
    ...(platform === 'darwin' ? ['/opt/homebrew/bin', '/usr/local/bin'] : []),
  ])
}

/**
 * `path` with every directory of `dirs` that exists and is missing appended, so what the
 * operator put first still wins.
 */
export function withHarnessDirs(
  path: string,
  dirs: string[],
  delimiter: string = hostDelimiter,
  isDirectory: (dir: string) => boolean = directoryExists,
): string {
  const entries = path.split(delimiter).filter(Boolean)
  const present = new Set(entries.map(normalize))
  const added = dirs.filter((dir) => !present.has(normalize(dir)) && isDirectory(dir))
  return [...entries, ...added].join(delimiter)
}

/**
 * Extends this process's PATH with {@link harnessDirs}, so the probe, every harness and git
 * see the same one. Runs on every probe: a directory an installer creates after the runner
 * started (`~/.local/bin` often does not exist yet) shows up at the next heartbeat.
 */
export function extendProcessPath(env: NodeJS.ProcessEnv = process.env): string {
  const key = pathKey(env)
  env[key] = withHarnessDirs(env[key] ?? '', harnessDirs(env))
  return env[key]
}

/** Windows spells it `Path`; Node keeps whichever spelling the environment had. */
export function pathKey(env: NodeJS.ProcessEnv): string {
  return Object.keys(env).find((key) => key.toUpperCase() === 'PATH') ?? 'PATH'
}

/** The executable `command` resolves to on `path`, or null; what `execFile` would start. */
export function findOnPath(
  command: string,
  path: string,
  platform: NodeJS.Platform = process.platform,
  env: NodeJS.ProcessEnv = process.env,
  delimiter: string = hostDelimiter,
): string | null {
  const extensions =
    platform === 'win32'
      ? ['', ...(env.PATHEXT ?? '.COM;.EXE;.BAT;.CMD').split(';').filter(Boolean)]
      : ['']
  for (const dir of path.split(delimiter).filter(Boolean)) {
    for (const extension of extensions) {
      const candidate = join(dir, command + extension)
      if (isExecutable(candidate, platform)) return candidate
    }
  }
  return null
}

function isExecutable(file: string, platform: NodeJS.Platform): boolean {
  try {
    if (!statSync(file).isFile()) return false
    if (platform !== 'win32') accessSync(file, constants.X_OK)
    return true
  } catch {
    return false
  }
}

function directoryExists(dir: string): boolean {
  try {
    return statSync(dir).isDirectory()
  } catch {
    return false
  }
}

function normalize(dir: string): string {
  return dir.replace(/[\\/]+$/, '')
}

function unique(dirs: string[]): string[] {
  return [...new Set(dirs)]
}

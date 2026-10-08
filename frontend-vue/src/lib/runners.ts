import type { Runner, RunnerMissingHarness } from '@/api/runners'

/**
 * The rules the Runners tab shows, kept out of the component so they can be tested: what
 * state a runner is in, and the exact command a person pastes on the machine.
 */

export type RunnerStatus = 'online' | 'offline' | 'never' | 'disabled'

export function runnerStatus(
  runner: Pick<Runner, 'isDisabled' | 'isOnline' | 'lastSeenAt'>,
): RunnerStatus {
  if (runner.isDisabled) return 'disabled'
  if (runner.isOnline) return 'online'
  // A runner that has never said hello is a registration nobody has used yet - a different
  // thing to fix from a machine that went quiet.
  return runner.lastSeenAt ? 'offline' : 'never'
}

export const runnerStatusLabel: Record<RunnerStatus, string> = {
  online: 'Online',
  offline: 'Offline',
  never: 'Waiting for first contact',
  disabled: 'Disabled',
}

/** Why a runner does not offer a harness, in the words of what to check on the machine. */
export function runnerMissingHarnessLabel(missing: RunnerMissingHarness): string {
  return missing.reason === 'version-failed'
    ? `${missing.command} is on PATH, but \`${missing.command} --version\` failed`
    : `${missing.command} is not on the runner's PATH`
}

/**
 * The command that registers this machine. The URL is the
 * page's own origin: the SPA is same-origin with the API, so the address in the browser bar
 * is the address a runner must reach.
 */
export function runnerRegisterCommand(origin: string, secret: string, name?: string): string {
  const base = `aictiq runner register --url ${origin.replace(/\/+$/, '')} --token ${secret}`
  return name ? `${base} --name ${shellQuote(name)}` : base
}

/** Quotes only when the shell would split or expand the value. */
function shellQuote(value: string): string {
  return /^[A-Za-z0-9._-]+$/.test(value) ? value : `'${value.replace(/'/g, `'\\''`)}'`
}

/** Quotes a path for a POSIX shell, leaving a leading `~/` outside so it still expands. */
export function shellPath(path: string): string {
  if (/^[\w./~-]+$/.test(path)) return path
  const [home, rest] = path.startsWith('~/') ? ['~/', path.slice(2)] : ['', path]
  return `${home}'${rest.replaceAll("'", "'\\''")}'`
}

/** A path hint without its trailing slashes; `/` stays `/`. */
const trimHint = (hint: string) => hint.trim().replace(/(.)\/+$/, '$1')

/** Trusts path hints under the directory that holds the hinted clone. */
export function runnerRootCommand(hint: string, slug: string): string {
  const path = trimHint(hint)
  const cut = path.lastIndexOf('/')
  const parent = cut > 0 ? path.slice(0, cut) : cut === 0 ? '/' : ''
  return `aictiq runner root ${parent ? shellPath(parent) : '<directory>'} --org ${slug}`
}

/** Maps one project to its clone on this runner only. */
export function runnerMapCommand(projectKey: string, hint: string, slug: string): string {
  const path = trimHint(hint)
  return `aictiq runner map ${projectKey} ${path ? shellPath(path) : '<path>'} --org ${slug}`
}

/** The three platforms `aictiq runner install-service` writes a definition for. */
export type RunnerPlatform = 'linux' | 'macos' | 'windows'

export type RunnerServiceSteps = {
  label: string
  commands: string
  note: string
}

/**
 * Installing the runner as a service, per platform - the same definitions the Factory guide
 * gives, so the screen that hands out the secret can hand out the rest of the setup too.
 * `install-service` only prints; these commands are what actually writes and enables it.
 */
export const runnerServiceSteps: Record<RunnerPlatform, RunnerServiceSteps> = {
  linux: {
    label: 'Linux',
    commands: [
      'mkdir -p ~/.config/systemd/user',
      'aictiq runner install-service > ~/.config/systemd/user/aictiq-runner.service',
      'systemctl --user daemon-reload',
      'systemctl --user enable --now aictiq-runner',
      'loginctl enable-linger "$USER"',
    ].join('\n'),
    note: 'Generate the unit from a shell whose PATH finds Node.js, aictiq and every harness: that path is embedded in it. Lingering keeps the runner up while nobody is logged in. Follow it with journalctl --user -u aictiq-runner -f.',
  },
  macos: {
    label: 'macOS',
    commands: [
      'mkdir -p ~/Library/LaunchAgents',
      'aictiq runner install-service > ~/Library/LaunchAgents/com.aictiq.runner.plist',
      'launchctl bootstrap gui/$(id -u) ~/Library/LaunchAgents/com.aictiq.runner.plist',
    ].join('\n'),
    note: 'The agent starts at login and runs while you are logged in; launchd restarts it after a crash. Generate it from a shell whose PATH finds Node.js and every harness. Its log is ~/Library/Logs/aictiq-runner.log.',
  },
  windows: {
    label: 'Windows',
    commands: [
      'aictiq runner install-service > install-runner.ps1',
      'powershell -NoProfile -ExecutionPolicy Bypass -File install-runner.ps1',
    ].join('\n'),
    note: 'In PowerShell. It registers an "Aictiq runner" task that starts at logon and writes %LOCALAPPDATA%\\aictiq\\runner.log. The runner’s git credential scripts need Git for Windows.',
  },
}

/**
 * The platform tab to open on. The browser is usually not the runner's machine, so this is a
 * guess anyone can override - it just saves the common case a click.
 */
export function runnerPlatformGuess(agent: string): RunnerPlatform {
  if (/windows|win32|win64/i.test(agent)) return 'windows'
  if (/mac|iphone|ipad/i.test(agent)) return 'macos'
  return 'linux'
}

import { CliError, ExitCode } from '../errors.js'
import { RunnerHttpError } from './client.js'
import type { RunnerClient } from './client.js'
import { profileLabel } from './config.js'
import type { RunnerConfig, RunnerProfile } from './config.js'

/** `--idle-exit` is whole minutes, at most a day. */
export const MaxIdleExitMinutes = 1440

/** How often `runner start --idle-exit` asks {@link IdleClock.expired}; the exit is at most this late. */
export const IdleCheckMs = 5000

/**
 * `aictiq runner start --idle-exit`: how long no run has been active on any profile. It counts
 * from its creation or from the end of the last run; while a run is in flight it never expires,
 * so a runner never leaves one half done.
 */
export class IdleClock {
  private readonly idleMs: number
  private readonly now: () => number
  private lastActive: number

  constructor(idleMs: number, now: () => number = Date.now) {
    this.idleMs = idleMs
    this.now = now
    this.lastActive = now()
  }

  /** A run was claimed or ended. */
  touch(): void {
    this.lastActive = this.now()
  }

  /** True once nothing has run for the whole idle time; `inFlight` is every profile's runs. */
  expired(inFlight: number): boolean {
    if (inFlight > 0) {
      this.lastActive = this.now()
      return false
    }
    return this.now() - this.lastActive >= this.idleMs
  }
}

/** Parses `--idle-exit`; null when it was not given. */
export function idleExitMinutes(value: string | undefined): number | null {
  if (value === undefined) return null
  const minutes = Number(value.trim())
  if (!/^\d+$/.test(value.trim()) || minutes < 1 || minutes > MaxIdleExitMinutes) {
    throw new CliError(`--idle-exit is a whole number of minutes between 1 and ${MaxIdleExitMinutes}.`, ExitCode.Validation)
  }
  return minutes
}

export interface RetireOptions {
  readConfig: () => RunnerConfig | null
  writeConfig: (config: RunnerConfig) => void
  client: (profile: RunnerProfile) => Pick<RunnerClient, 'deregister'>
  local: (message: string) => void
}

/**
 * The idle exit of a fleet runner: every ephemeral profile deregisters its runner and leaves
 * runner.json, so the instance does not keep a runner for a machine that is about to vanish.
 * A refusal (401, 404, 409) means the runner is already gone or cannot go now; it is logged
 * and the profile dropped all the same, as it is when the instance cannot be reached, since
 * nothing on this machine will use that secret again. Returns the profiles retired.
 */
export async function retireEphemeral(options: RetireOptions): Promise<RunnerProfile[]> {
  const config = options.readConfig()
  const retiring = config?.profiles.filter((p) => p.ephemeral) ?? []
  if (!config || retiring.length === 0) return []

  for (const profile of retiring) {
    const label = profileLabel(profile)
    try {
      await options.client(profile).deregister()
      options.local(`[${label}] Deregistered this ephemeral runner`)
    } catch (error) {
      const message = error instanceof Error ? error.message : String(error)
      options.local(
        error instanceof RunnerHttpError && [401, 404, 409].includes(error.status)
          ? `[${label}] The instance did not deregister this runner (${message}); dropping it here anyway`
          : `[${label}] Could not deregister this runner (${message}); dropping it here, and the instance will see it go offline`,
      )
    }
  }

  // Re-read: `register` may have changed the file while the runs went on.
  const current = options.readConfig() ?? config
  const tokens = new Set(retiring.map((p) => p.token))
  options.writeConfig({ ...current, profiles: current.profiles.filter((p) => !tokens.has(p.token)) })
  return retiring
}

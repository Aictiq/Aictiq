import { Floor } from './floor.js'
import { RunnerRevokedError } from './loop.js'
import type { RunnerLoop } from './loop.js'
import { profileLabel } from './config.js'
import type { RunnerConfig, RunnerProfile } from './config.js'
import type { RunnerHello } from './types.js'

export interface RunnerSupervisorOptions {
  /** Read again every {@link reloadMs}; a new registration starts, a removed one stops. */
  readConfig: () => RunnerConfig | null
  createLoop: (
    profile: RunnerProfile,
    floor: Floor,
    onHello: (hello: RunnerHello) => void,
  ) => RunnerLoop
  /** A profile's first hello named its organization; persist it. */
  learned?: (profile: RunnerProfile, hello: RunnerHello) => void
  local: (message: string) => void
  reloadMs?: number
  /** How long a profile that failed for a reason other than revocation waits before it is tried again. */
  retryMs?: number
  floor?: Floor
}

interface Running {
  profile: RunnerProfile
  loop: RunnerLoop
  done: Promise<void>
  /** Set when the supervisor stopped it (the profile left runner.json); its ending is expected. */
  retired: boolean
}

/**
 * `aictiq runner start` on a machine with several profiles: one {@link RunnerLoop} per
 * organization, all on one {@link Floor}. Profiles are keyed by their secret, so
 * `aictiq runner register` in another terminal - a new organization or a rotated secret -
 * takes effect within {@link reloadMs}, without a restart.
 *
 * One organization's secret being revoked stops that organization only. The process ends
 * with the revoked error (exit 5) once no profile in `runner.json` still works, which is
 * what the service definitions treat as "stop for good".
 */
export class RunnerSupervisor {
  private readonly options: RunnerSupervisorOptions
  private readonly floor: Floor
  private readonly running = new Map<string, Running>()
  /** Secrets that failed: revoked ones for good, others until the time given. */
  private readonly failed = new Map<
    string,
    { revoked: RunnerRevokedError | null; retryAt: number }
  >()
  private stopping = false
  private settle: (() => void) | undefined

  constructor(options: RunnerSupervisorOptions) {
    this.options = options
    this.floor = options.floor ?? new Floor()
  }

  get inFlight(): number {
    let total = 0
    for (const { loop } of this.running.values()) total += loop.inFlight
    return total
  }

  stop(): void {
    this.stopping = true
    for (const { loop } of this.running.values()) loop.stop()
    this.settle?.()
  }

  abort(): void {
    this.stopping = true
    for (const { loop } of this.running.values()) loop.abort()
    this.settle?.()
  }

  async run(): Promise<void> {
    const reload = setInterval(() => this.sync(), this.options.reloadMs ?? 5000)
    try {
      this.sync()
      while (!this.stopping) {
        const revoked = this.allRevoked()
        if (revoked) throw revoked
        await new Promise<void>((resolve) => (this.settle = resolve))
      }
      await Promise.all([...this.running.values()].map(({ done }) => done))
    } finally {
      clearInterval(reload)
      this.settle = undefined
    }
  }

  /** Every configured profile is revoked and nothing runs: the error to exit with, else null. */
  private allRevoked(): RunnerRevokedError | null {
    if (this.running.size > 0) return null
    const profiles = this.options.readConfig()?.profiles ?? []
    if (profiles.length === 0) return null
    let last: RunnerRevokedError | null = null
    for (const profile of profiles) {
      const failure = this.failed.get(profile.token)
      if (!failure?.revoked) return null
      last = failure.revoked
    }
    return profiles.length === 1 || !last
      ? last
      : new RunnerRevokedError(
          'Every organization this runner served disabled, deleted or rotated its runner. Register it again with `aictiq runner register`.',
        )
  }

  private sync(): void {
    if (this.stopping) return
    const config = this.options.readConfig()
    const profiles = config?.profiles ?? []
    const wanted = new Map(profiles.map((profile) => [profile.token, profile]))

    for (const [token, running] of this.running) {
      if (wanted.has(token) || running.retired) continue
      running.retired = true
      this.options.local(
        `[${profileLabel(running.profile)}] Removed from runner.json; stopping after its runs finish`,
      )
      running.loop.stop()
    }

    for (const profile of wanted.values()) {
      if (this.running.has(profile.token)) continue
      const failure = this.failed.get(profile.token)
      if (failure && (failure.revoked || Date.now() < failure.retryAt)) continue
      this.failed.delete(profile.token)
      this.start(profile)
    }
    this.settle?.()
  }

  private start(profile: RunnerProfile): void {
    const label = () => profileLabel(profile)
    const loop = this.options.createLoop(profile, this.floor, (hello) => {
      if (profile.organization !== hello.organizationSlug) {
        profile.organization = hello.organizationSlug
        this.options.learned?.(profile, hello)
      }
    })
    const running: Running = { profile, loop, done: Promise.resolve(), retired: false }
    running.done = loop
      .run()
      .catch((error: unknown) => {
        if (error instanceof RunnerRevokedError) {
          this.options.local(`[${label()}] ${error.message}`)
          this.failed.set(profile.token, { revoked: error, retryAt: Number.POSITIVE_INFINITY })
          return
        }
        const retryMs = this.options.retryMs ?? 60_000
        this.options.local(
          `[${label()}] Stopped: ${error instanceof Error ? error.message : String(error)}; trying again in ${Math.round(retryMs / 1000)}s`,
        )
        this.failed.set(profile.token, { revoked: null, retryAt: Date.now() + retryMs })
      })
      .finally(() => {
        this.running.delete(profile.token)
        this.settle?.()
      })
    this.running.set(profile.token, running)
  }
}

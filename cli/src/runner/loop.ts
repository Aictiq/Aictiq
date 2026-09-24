import { RunnerHttpError, RunnerNetworkError } from './client.js'
import type { RunnerClient } from './client.js'
import type { Floor } from './floor.js'
import type { ClaimedRun, RunnerCapabilities, RunnerHello } from './types.js'

/** Every harness the CLI has an adapter for, offered when none is detected (see below). */
export const KnownHarnesses = ['claude', 'codex', 'opencode'] as const

export interface RunnerLoopOptions {
  client: RunnerClient
  parallel: number
  probe: () => Promise<RunnerCapabilities>
  execute: (run: ClaimedRun, hello: RunnerHello, shutdown: AbortSignal) => Promise<unknown>
  local: (message: string) => void
  backoffMs?: (attempt: number) => number
  /**
   * Shared by every organization this machine serves, so their runs never execute at the
   * same time. Absent, the loop answers only to its own `parallel`.
   */
  floor?: Floor
  /** This loop's place on the floor: one key per organization. */
  floorKey?: string
  /** Called once the instance has said who this runner is. */
  onHello?: (hello: RunnerHello) => void
}

export class RunnerRevokedError extends Error {
  constructor(message: string) {
    super(message)
    this.name = 'RunnerRevokedError'
  }
}

/**
 * The runner's life: `hello`, then claim → execute in up to `parallel` slots, heartbeating
 * the runner alongside. `stop()` is the first Ctrl-C: claim nothing more and let the runs
 * in flight finish. `abort()` is the second: stop the harnesses and report them cancelled.
 */
export class RunnerLoop {
  private readonly options: RunnerLoopOptions
  private readonly active = new Set<Promise<void>>()
  private readonly claimAbort = new AbortController()
  private readonly shutdown = new AbortController()
  private stopping = false
  private revoked: RunnerRevokedError | undefined
  private wake: (() => void) | undefined

  constructor(options: RunnerLoopOptions) {
    this.options = options
  }

  get inFlight(): number {
    return this.active.size
  }

  stop(): void {
    this.stopping = true
    this.claimAbort.abort()
    this.wake?.()
  }

  abort(): void {
    this.stop()
    this.shutdown.abort()
  }

  /** Resolves when the loop has stopped and every run it took is reported. */
  async run(): Promise<void> {
    const { client, local } = this.options
    let capabilities = await this.options.probe()
    const hello = await this.retrying(() => client.hello(capabilities))
    if (!hello) return
    this.options.onHello?.(hello)
    local(
      `Runner "${hello.name}" connected to ${client.baseUrl} (${hello.organizationSlug}); harnesses: ${
        capabilities.harnesses.map((h) => h.name).join(', ') || 'none detected'
      }`,
    )

    const heartbeat = setInterval(() => {
      void (async () => {
        try {
          capabilities = await this.options.probe()
          await client.heartbeat(capabilities)
        } catch (error) {
          if (error instanceof RunnerHttpError && error.revoked) this.revoke(error)
          else local(`Heartbeat failed: ${error instanceof Error ? error.message : String(error)}`)
        }
      })()
    }, hello.heartbeatIntervalSeconds * 1000)

    const { floor } = this.options
    const floorKey = this.options.floorKey ?? client.baseUrl
    try {
      let attempt = 0
      while (!this.stopping) {
        if (floor) {
          await floor.turn(floorKey, this.claimAbort.signal)
          if (this.stopping) break
        }
        const free = this.options.parallel - this.active.size
        if (free <= 0) {
          await this.waitForSlot()
          continue
        }

        // With no harness detected the runner still offers every one it knows, so a run
        // dispatched to a misconfigured machine fails at once with `harness-unavailable`
        // instead of waiting in the queue with no explanation.
        const detected = capabilities.harnesses.map((h) => h.name)
        const offered = detected.length > 0 ? detected : [...KnownHarnesses]
        let claimed: ClaimedRun | null
        try {
          claimed = await client.claim(offered, free, this.claimAbort.signal)
          attempt = 0
        } catch (error) {
          if (this.stopping) break
          if (error instanceof RunnerHttpError && error.revoked) {
            this.revoke(error)
            break
          }
          attempt++
          const wait =
            this.options.backoffMs?.(attempt) ?? Math.min(60_000, 1000 * 2 ** (attempt - 1))
          local(
            `Claim failed (${error instanceof Error ? error.message : String(error)}); retrying in ${Math.round(wait / 1000)}s`,
          )
          await this.sleep(wait)
          continue
        }
        if (!claimed) continue
        if (floor && !floor.enter(floorKey) && !(await this.handBack(claimed, floor, floorKey)))
          continue

        local(`Claimed ${claimed.itemKey} (run ${claimed.runId}, ${claimed.harness})`)
        const execution = this.options
          .execute(claimed, hello, this.shutdown.signal)
          .then(() => undefined)
          .catch((error: unknown) => {
            if (error instanceof RunnerHttpError && error.revoked) this.revoke(error)
            else
              local(
                `Run ${claimed.runId} ended with an error: ${error instanceof Error ? error.message : String(error)}`,
              )
          })
          .finally(() => {
            floor?.leave(floorKey)
            this.active.delete(execution)
            this.wake?.()
          })
        this.active.add(execution)
      }

      if (this.active.size > 0) local(`Waiting for ${this.active.size} run(s) to finish…`)
      await Promise.all([...this.active])
    } finally {
      clearInterval(heartbeat)
    }

    if (this.revoked) throw this.revoked
  }

  /**
   * A run claimed while another organization took the floor. It goes back to the queue;
   * true only when it could not be given back and has now waited for the floor instead,
   * so the caller executes it.
   */
  private async handBack(run: ClaimedRun, floor: Floor, key: string): Promise<boolean> {
    const { client, local } = this.options
    try {
      await client.release(run.runId)
      local(
        `Gave ${run.itemKey} back to the queue: another organization has runs executing on this machine`,
      )
      return false
    } catch (error) {
      if (error instanceof RunnerHttpError && error.revoked) {
        this.revoke(error)
        return false
      }
      // A problem document means the instance answered about the run: it was cancelled or
      // swept meanwhile and is no longer ours. A bare 404 or a network failure means the
      // run is still ours (an instance without the release route): wait, then execute it.
      if (error instanceof RunnerHttpError && error.problem?.type) return false
      local(
        `Could not give ${run.itemKey} back (${error instanceof Error ? error.message : String(error)}); it starts when the other organization's runs finish`,
      )
      while (!floor.enter(key)) await floor.turn(key)
      return true
    }
  }

  private revoke(error: RunnerHttpError): void {
    this.revoked ??= new RunnerRevokedError(
      error.problem?.type?.endsWith('token-revoked')
        ? 'This runner was disabled, deleted or rotated. Register it again with `aictiq runner register`.'
        : `The instance refused the runner secret (${error.message}). Register it again with \`aictiq runner register\`.`,
    )
    this.abort()
  }

  /** Retries network failures until stopped; a refusal is thrown. */
  private async retrying<T>(action: () => Promise<T>): Promise<T | null> {
    for (let attempt = 1; !this.stopping; attempt++) {
      try {
        return await action()
      } catch (error) {
        if (error instanceof RunnerHttpError && error.revoked) {
          this.revoke(error)
          throw this.revoked
        }
        if (!(error instanceof RunnerNetworkError)) throw error
        const wait =
          this.options.backoffMs?.(attempt) ?? Math.min(60_000, 1000 * 2 ** (attempt - 1))
        this.options.local(`${error.message}; retrying in ${Math.round(wait / 1000)}s`)
        await this.sleep(wait)
      }
    }
    return null
  }

  private waitForSlot(): Promise<void> {
    return new Promise((resolve) => {
      this.wake = () => {
        this.wake = undefined
        resolve()
      }
    })
  }

  private sleep(ms: number): Promise<void> {
    return new Promise((resolve) => {
      const timer = setTimeout(done, ms)
      this.wake = done
      function done() {
        clearTimeout(timer)
        resolve()
      }
    })
  }
}

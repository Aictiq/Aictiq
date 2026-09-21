import { RunnerHttpError, RunnerNetworkError } from './client.js'
import type { RunnerClient } from './client.js'
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

    try {
      let attempt = 0
      while (!this.stopping) {
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

/**
 * The factory floor of one machine that serves several organizations: runs of one
 * organization may execute side by side, runs of two never do. While a run is live, the
 * other organizations' agents cannot read its token from the process table, find its
 * checkout under the workspace root, or push with its GitHub token.
 *
 * Every organization's loop asks for its {@link turn} before it claims, and must
 * {@link enter} before it executes what it claimed. Loops of an idle machine poll side by
 * side, so two claims can land together; the loser's `enter` fails and it gives its run
 * back. When the floor frees, the organization that held it waits a moment
 * ({@link handoffMs}) if another one is waiting, so one busy queue cannot keep the others
 * out indefinitely.
 */
export class Floor {
  private holder: string | null = null
  private count = 0
  private last: string | null = null
  private freedAt = 0
  private readonly waiting = new Map<string, number>()
  private changed = deferred()

  constructor(private readonly handoffMs = 2000) {}

  /** The organization with runs executing, or null when the floor is free. */
  get current(): string | null {
    return this.holder
  }

  /**
   * Resolves when `key` may claim: the floor is free, or its own runs hold it. Also resolves
   * when `signal` aborts; the caller checks why.
   */
  async turn(key: string, signal?: AbortSignal): Promise<void> {
    this.waiting.set(key, (this.waiting.get(key) ?? 0) + 1)
    try {
      while (!signal?.aborted) {
        const yieldFor = this.yieldFor(key)
        if (this.count > 0 ? this.holder === key : yieldFor === 0) return

        const waits: Promise<unknown>[] = [this.changed.promise]
        let timer: NodeJS.Timeout | undefined
        if (yieldFor > 0)
          waits.push(new Promise((resolve) => (timer = setTimeout(resolve, yieldFor))))
        let onAbort: (() => void) | undefined
        if (signal) {
          waits.push(
            new Promise<void>((resolve) => {
              onAbort = () => resolve()
              signal.addEventListener('abort', onAbort, { once: true })
            }),
          )
        }
        await Promise.race(waits)
        clearTimeout(timer)
        if (onAbort) signal?.removeEventListener('abort', onAbort)
      }
    } finally {
      const left = (this.waiting.get(key) ?? 1) - 1
      if (left === 0) this.waiting.delete(key)
      else this.waiting.set(key, left)
    }
  }

  /** Takes the floor for one run of `key`; false when another organization's runs hold it. */
  enter(key: string): boolean {
    if (this.count > 0 && this.holder !== key) return false
    this.holder = key
    this.count++
    this.notify()
    return true
  }

  leave(key: string): void {
    if (this.holder !== key || this.count === 0) return
    this.count--
    if (this.count === 0) {
      this.holder = null
      this.last = key
      this.freedAt = Date.now()
    }
    this.notify()
  }

  /** How long `key` stands back from a free floor so a waiting organization goes first. */
  private yieldFor(key: string): number {
    if (this.count > 0 || key !== this.last) return 0
    const othersWaiting = [...this.waiting.keys()].some((other) => other !== key)
    if (!othersWaiting) return 0
    return Math.max(0, this.freedAt + this.handoffMs - Date.now())
  }

  private notify(): void {
    const previous = this.changed
    this.changed = deferred()
    previous.resolve()
  }
}

function deferred(): { promise: Promise<void>; resolve: () => void } {
  let resolve!: () => void
  const promise = new Promise<void>((done) => (resolve = done))
  return { promise, resolve }
}

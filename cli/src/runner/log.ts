import { RunnerHttpError } from './client.js'
import type { LogChunk } from './client.js'
import type { LogStream } from './types.js'

/** The server's per-chunk text limit (`RunLogChunk.MaxTextLength`) and per-batch count. */
const MaxChunkChars = 65_536
const MaxChunksPerBatch = 512

export interface LogStreamerOptions {
  send: (chunks: LogChunk[]) => Promise<void>
  /** Secrets to blank out of every line before it leaves the machine. */
  secrets: string[]
  maxBatchBytes: number
  flushIntervalMs?: number
  /** Where the runner says something about the log itself (the cap was hit). */
  local?: (message: string) => void
  now?: () => Date
  retryDelayMs?: (attempt: number) => number
}

/**
 * Replaces each secret with a marker. Longest first, so a secret that contains another
 * (an installation token that happens to share a prefix) is not half-redacted.
 */
export function redact(text: string, secrets: readonly string[]): string {
  let result = text
  for (const secret of [...secrets]
    .filter((s) => s.length >= 8)
    .sort((a, b) => b.length - a.length)) {
    result = result.split(secret).join('[redacted]')
  }
  return result
}

/**
 * Batches log lines for `POST /runner/runs/{id}/log`: every second or when a batch reaches
 * the server's byte limit, whichever comes first. Each line gets its own `seq`, assigned
 * when it is queued, so a failed batch is re-sent with the same numbers and the server's
 * `ON CONFLICT DO NOTHING` makes the overlap harmless. Sends are strictly sequential.
 */
export class LogStreamer {
  private readonly options: LogStreamerOptions
  private readonly queue: LogChunk[] = []
  private seq = 0
  private timer: NodeJS.Timeout | undefined
  private sending: Promise<void> = Promise.resolve()
  private capped = false
  private closed = false
  private closing = false

  constructor(options: LogStreamerOptions) {
    this.options = options
  }

  /** True once the server refused the log for exceeding its cap; later lines are dropped. */
  get truncated(): boolean {
    return this.capped
  }

  push(stream: LogStream, line: string): void {
    if (this.capped || this.closed) return
    const text = redact(line, this.options.secrets)
    const at = (this.options.now?.() ?? new Date()).toISOString()
    // A line longer than one chunk may hold (or than one batch may carry) is split, so a
    // minified bundle printed by a build step cannot wedge the stream on a 400.
    for (const piece of splitByBytes(text, Math.min(this.options.maxBatchBytes, MaxChunkChars))) {
      this.queue.push({ seq: this.seq++, stream, text: piece, at })
    }

    if (this.queuedBytes() >= this.options.maxBatchBytes) {
      void this.flush()
    } else {
      this.timer ??= setTimeout(() => {
        this.timer = undefined
        void this.flush()
      }, this.options.flushIntervalMs ?? 1000)
    }
  }

  /** Sends everything queued so far; resolves once it is stored or the log is capped. */
  flush(): Promise<void> {
    if (this.timer) {
      clearTimeout(this.timer)
      this.timer = undefined
    }
    this.sending = this.sending.then(() => this.drain())
    return this.sending
  }

  /** Flushes and stops accepting lines. */
  async close(): Promise<void> {
    this.closing = true
    await this.flush()
    this.closed = true
  }

  private async drain(): Promise<void> {
    while (this.queue.length > 0 && !this.capped) {
      const batch = this.takeBatch()
      let attempt = 0
      while (true) {
        try {
          await this.options.send(batch)
          this.queue.splice(0, batch.length)
          break
        } catch (error) {
          if (error instanceof RunnerHttpError && error.status === 413) {
            this.capped = true
            this.queue.length = 0
            this.options.local?.(
              'The instance capped this run log; further output is kept out of it.',
            )
            return
          }
          // 409: the run is finished server-side, so its log is closed for good. Any
          // other refusal is a bug worth not looping on; only the network is retried.
          if (error instanceof RunnerHttpError) {
            this.queue.length = 0
            this.capped = true
            this.options.local?.(`The instance refused the run log: ${error.message}`)
            return
          }
          attempt++
          // While running, the network may come back and the log should survive it; at
          // close the run has to be reported, and a log that cannot be sent must not block it.
          if (attempt > 8 && this.closing) {
            this.queue.length = 0
            return
          }
          await delay(this.options.retryDelayMs?.(attempt) ?? Math.min(30_000, 500 * 2 ** attempt))
        }
      }
    }
  }

  private takeBatch(): LogChunk[] {
    const batch: LogChunk[] = []
    let bytes = 0
    for (const chunk of this.queue) {
      const size = Buffer.byteLength(chunk.text, 'utf8')
      if (
        batch.length > 0 &&
        (bytes + size > this.options.maxBatchBytes || batch.length >= MaxChunksPerBatch)
      )
        break
      batch.push(chunk)
      bytes += size
    }
    return batch
  }

  private queuedBytes(): number {
    let bytes = 0
    for (const chunk of this.queue) bytes += Buffer.byteLength(chunk.text, 'utf8')
    return bytes
  }
}

/** Splits on code-point boundaries so that each piece is at most `maxBytes` of UTF-8. */
export function splitByBytes(text: string, maxBytes: number): string[] {
  if (Buffer.byteLength(text, 'utf8') <= maxBytes) return [text]
  const pieces: string[] = []
  let current = ''
  let bytes = 0
  for (const char of text) {
    const size = Buffer.byteLength(char, 'utf8')
    if (bytes + size > maxBytes) {
      pieces.push(current)
      current = ''
      bytes = 0
    }
    current += char
    bytes += size
  }
  pieces.push(current)
  return pieces
}

function delay(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms))
}

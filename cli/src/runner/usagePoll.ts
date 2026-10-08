import { execFile } from 'node:child_process'
import {
  closeSync,
  fstatSync,
  openSync,
  readdirSync,
  readFileSync,
  readSync,
  statSync,
} from 'node:fs'
import { homedir, platform } from 'node:os'
import { join } from 'node:path'
import { promisify } from 'node:util'
import { codexSessionsDir, limitsOf, rolloutDays } from './harness/codex.js'
import { readUsageLimits, recordUsageLimits, usageLimitsPath, usageWindow } from './limits.js'
import type { ParsedLine } from './types.js'

type Windows = NonNullable<ParsedLine['limits']>

/** How often the newest Codex rollout is read: it is a local file, so once a minute is cheap. */
export const CodexReadIntervalMs = 60_000
/** How often Anthropic is asked for Claude usage when the machine opted in. */
export const ClaudePollIntervalMs = 5 * 60_000
/** The longest wait after repeated failures (429, network). */
export const ClaudeMaxBackoffMs = 60 * 60_000
export const ClaudeUsageUrl = 'https://api.anthropic.com/api/oauth/usage'

/** Only the end of a rollout is read: the last `token_count` is near it, and a long session is megabytes. */
const RolloutTailBytes = 256 * 1024
/** Day directories looked through for the newest rollout; an interactive session may have started days ago. */
const RolloutDays = 7
/** Rollouts tried, newest first, when the newest has no `rate_limits` yet (a session that just started). */
const RolloutFiles = 5

export interface ClaudeCredentials {
  accessToken: string
  /** Unix milliseconds, as Claude Code stores it. */
  expiresAt?: number
}

export interface UsagePollerOptions {
  /** Read on every poll, so `aictiq runner usage --claude-oauth` applies without a restart. */
  claudeEnabled: () => boolean
  log: (message: string) => void
  fetch?: typeof fetch
  readClaudeCredentials?: () => Promise<ClaudeCredentials | null>
  now?: () => number
  codexSessions?: string
  limitsPath?: string
}

/**
 * Keeps `harness-limits.json` current between runs. Codex: the newest rollout file on the
 * machine, interactive sessions included, with no provider call. Claude: Anthropic's OAuth usage
 * endpoint, only when the machine opted in, with Claude Code's own token, which it never
 * refreshes. One per process: the file is the machine's, whichever organizations it serves.
 */
export class UsagePoller {
  private codexNext = 0
  private claudeNext = 0
  private claudeFailures = 0
  private readonly logged = new Set<string>()
  private running: Promise<void> | null = null
  private readonly now: () => number

  constructor(private readonly options: UsagePollerOptions) {
    this.now = options.now ?? Date.now
  }

  /** Never throws, and every profile's heartbeat may call it: the work is done at most once at a time. */
  refresh(): Promise<void> {
    this.running ??= this.poll().finally(() => {
      this.running = null
    })
    return this.running
  }

  private async poll(): Promise<void> {
    const path = this.options.limitsPath ?? usageLimitsPath()
    if (this.now() >= this.codexNext) {
      this.codexNext = this.now() + CodexReadIntervalMs
      try {
        const latest = latestCodexLimits(this.options.codexSessions ?? codexSessionsDir())
        const stored = readUsageLimits(path).find((entry) => entry.harness === 'codex')
        // Unchanged since the last read, or older than what a run recorded: nothing to write.
        if (latest && (!stored || Date.parse(stored.observedAt) < latest.observedAt.getTime())) {
          recordUsageLimits('codex', latest.limits, latest.observedAt, path)
        }
      } catch (error) {
        this.once('codex', `Codex usage read failed: ${message(error)}`)
      }
    }
    if (!this.options.claudeEnabled()) {
      this.claudeNext = 0
      this.claudeFailures = 0
      return
    }
    if (this.now() < this.claudeNext) return
    try {
      await this.pollClaude(path)
    } catch (error) {
      this.backoff()
      this.once('claude-error', `Claude usage poll failed: ${message(error)}`)
    }
  }

  private async pollClaude(path: string): Promise<void> {
    this.claudeNext = this.now() + ClaudePollIntervalMs
    const credentials = await (this.options.readClaudeCredentials ?? readClaudeCredentials)()
    if (!credentials) {
      this.once(
        'claude-missing',
        'Claude usage poll skipped: no Claude Code OAuth sign-in on this machine (an API key has no 5-hour or weekly usage)',
      )
      return
    }
    // Never refresh: that rotates the refresh token and could sign Claude Code out. Its next use refreshes it.
    if (credentials.expiresAt !== undefined && credentials.expiresAt <= this.now()) {
      this.once(
        'claude-expired',
        'Claude usage poll skipped: the OAuth token expired; Claude Code renews it on its next use',
      )
      return
    }
    const response = await (this.options.fetch ?? fetch)(ClaudeUsageUrl, {
      headers: {
        Authorization: `Bearer ${credentials.accessToken}`,
        'anthropic-beta': 'oauth-2025-04-20',
        Accept: 'application/json',
      },
      signal: AbortSignal.timeout(15_000),
    })
    if (response.status === 401 || response.status === 403) {
      this.once(
        'claude-refused',
        `Claude usage poll skipped: Anthropic refused the OAuth token (${response.status}); Claude Code renews it on its next use`,
      )
      return
    }
    if (!response.ok) {
      const retryAfter = Number(response.headers.get('retry-after'))
      this.backoff(Number.isFinite(retryAfter) && retryAfter > 0 ? retryAfter * 1000 : 0)
      this.once(
        `claude-${response.status}`,
        `Claude usage poll failed: HTTP ${response.status}; backing off`,
      )
      return
    }
    const body = (await response.json()) as unknown
    const windows = parseClaudeOAuthUsage(body)
    if (!windows) {
      this.once('claude-shape', `Claude usage poll: unexpected response shape ${shapeOf(body)}`)
      return
    }
    this.claudeFailures = 0
    for (const key of [...this.logged]) if (key.startsWith('claude')) this.logged.delete(key)
    recordUsageLimits('claude', windows, new Date(this.now()), path)
  }

  private backoff(atLeastMs = 0): void {
    this.claudeFailures++
    const wait = Math.min(ClaudeMaxBackoffMs, ClaudePollIntervalMs * 2 ** (this.claudeFailures - 1))
    this.claudeNext = this.now() + Math.max(wait, Math.min(atLeastMs, ClaudeMaxBackoffMs))
  }

  /** The runner's log is read by people: a poll that keeps failing the same way says so once. */
  private once(key: string, text: string): void {
    if (this.logged.has(key)) return
    this.logged.add(key)
    this.options.log(text)
  }
}

/**
 * The last `rate_limits` in the newest rollout under `sessions`, with the event's own time:
 * a reading is as old as the Codex turn that produced it, not the moment it was read.
 */
export function latestCodexLimits(sessions: string): { limits: Windows; observedAt: Date } | null {
  const files: { path: string; modified: number }[] = []
  for (const dir of rolloutDays(sessions, RolloutDays)) {
    let names: string[]
    try {
      names = readdirSync(dir)
    } catch {
      continue
    }
    for (const name of names) {
      if (!name.startsWith('rollout-') || !name.endsWith('.jsonl')) continue
      try {
        files.push({ path: join(dir, name), modified: statSync(join(dir, name)).mtimeMs })
      } catch {
        // Removed meanwhile.
      }
    }
  }
  files.sort((left, right) => right.modified - left.modified)
  for (const file of files.slice(0, RolloutFiles)) {
    const lines = tail(file.path, RolloutTailBytes)
    for (let i = lines.length - 1; i >= 0; i--) {
      if (!lines[i]!.includes('"rate_limits"')) continue
      let event: Record<string, unknown>
      try {
        event = JSON.parse(lines[i]!) as Record<string, unknown>
      } catch {
        continue
      }
      const limits = typeof event === 'object' && event !== null ? limitsOf(event) : null
      if (!limits) continue
      const observed =
        typeof event.timestamp === 'string' ? Date.parse(event.timestamp) : Number.NaN
      return { limits, observedAt: new Date(Number.isFinite(observed) ? observed : file.modified) }
    }
  }
  return null
}

/** The whole lines in the last `bytes` of a file. */
export function tail(path: string, bytes: number): string[] {
  let fd: number
  try {
    fd = openSync(path, 'r')
  } catch {
    return []
  }
  try {
    const size = fstatSync(fd).size
    const start = Math.max(0, size - bytes)
    const buffer = Buffer.alloc(size - start)
    const read = readSync(fd, buffer, 0, buffer.length, start)
    const lines = buffer.subarray(0, read).toString('utf8').split('\n')
    // The first line is cut short unless the read started at the beginning of the file.
    return start > 0 ? lines.slice(1) : lines
  } catch {
    return []
  } finally {
    closeSync(fd)
  }
}

/**
 * `GET /api/oauth/usage`: `five_hour` and `seven_day`, each `{ utilization, resets_at }` or null.
 * Unlike stream-json's, `utilization` is already a percentage. Undocumented, so anything else
 * reads as null and the caller logs the shape.
 */
export function parseClaudeOAuthUsage(body: unknown): Windows | null {
  if (typeof body !== 'object' || body === null) return null
  const record = body as Record<string, unknown>
  if (!('five_hour' in record) && !('seven_day' in record)) return null
  const windows: Windows = {}
  for (const [key, slot] of [
    ['five_hour', 'fiveHour'],
    ['seven_day', 'weekly'],
  ] as const) {
    const window = record[key]
    if (window === null || window === undefined) continue
    if (typeof window !== 'object') return null
    const { utilization, resets_at } = window as Record<string, unknown>
    const parsed = usageWindow(utilization, resets_at)
    if (!parsed) return null
    windows[slot] = parsed
  }
  return windows
}

/** Claude Code's OAuth sign-in: the macOS Keychain, else `.credentials.json` in its config directory. */
export async function readClaudeCredentials(
  env: NodeJS.ProcessEnv = process.env,
  os: NodeJS.Platform = platform(),
): Promise<ClaudeCredentials | null> {
  if (os === 'darwin') {
    try {
      const { stdout } = await promisify(execFile)(
        'security',
        ['find-generic-password', '-s', 'Claude Code-credentials', '-w'],
        { timeout: 10_000 },
      )
      const fromKeychain = credentialsOf(stdout)
      if (fromKeychain) return fromKeychain
    } catch {
      // No Keychain item: Claude Code may keep the file instead.
    }
  }
  try {
    const dir = env.CLAUDE_CONFIG_DIR || join(homedir(), '.claude')
    return credentialsOf(readFileSync(join(dir, '.credentials.json'), 'utf8'))
  } catch {
    return null
  }
}

export function credentialsOf(text: string): ClaudeCredentials | null {
  try {
    const oauth = (JSON.parse(text) as { claudeAiOauth?: Record<string, unknown> } | null)
      ?.claudeAiOauth
    if (!oauth || typeof oauth.accessToken !== 'string' || oauth.accessToken === '') return null
    return {
      accessToken: oauth.accessToken,
      ...(typeof oauth.expiresAt === 'number' ? { expiresAt: oauth.expiresAt } : {}),
    }
  } catch {
    return null
  }
}

/** Keys and value types only, never values: enough to see the endpoint changed. */
function shapeOf(value: unknown, depth = 0): string {
  if (value === null) return 'null'
  if (Array.isArray(value)) return 'array'
  if (typeof value !== 'object') return typeof value
  if (depth > 1) return 'object'
  const entries = Object.entries(value as Record<string, unknown>).slice(0, 20)
  return `{${entries.map(([key, inner]) => `${key}: ${shapeOf(inner, depth + 1)}`).join(', ')}}`
}

function message(error: unknown): string {
  return error instanceof Error ? error.message : String(error)
}

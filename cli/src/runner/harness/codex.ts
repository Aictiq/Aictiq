import { readdirSync, readFileSync } from 'node:fs'
import { homedir } from 'node:os'
import { join } from 'node:path'
import { usageWindow } from '../limits.js'
import type { HarnessAdapter, HarnessInfo, ParsedLine } from '../types.js'
import { versionOf } from './claude.js'
import { failedOutcome } from './failure.js'

const limit = (value: string, length: number) =>
  value.length > length ? `${value.slice(0, Math.max(0, length - 1))}…` : value

const recordOf = (value: unknown): Record<string, unknown> | null =>
  typeof value === 'object' && value !== null ? (value as Record<string, unknown>) : null

const MCP_ENV = ['AICTIQ_URL', 'AICTIQ_TOKEN', 'AICTIQ_ORG', 'AICTIQ_ITEM', 'AICTIQ_RUN']

export const codex: HarnessAdapter = {
  name: 'codex',

  async available(env?: NodeJS.ProcessEnv): Promise<HarnessInfo | null> {
    const version = await versionOf('codex', env)
    return version === null ? null : { name: 'codex', version }
  },

  invocation(context) {
    // `exec resume` takes the same flags as `exec` except `-C`; the runner starts it in the
    // checkout, which is its working directory either way.
    const start = context.resumeSessionId
      ? ['exec', 'resume', context.resumeSessionId]
      : ['exec']
    return {
      command: 'codex',
      args: [
        ...start,
        '--json',
        // 0.154.0 removed --full-auto. The runner is the ticket's explicit trust boundary:
        // the agent needs unrestricted network access for the MCP server and git push.
        '--dangerously-bypass-approvals-and-sandbox',
        // This mode also permits the absolute attachment paths named in prompt.md outside
        // the checkout. The runner itself remains the trust boundary for those files.
        '--skip-git-repo-check',
        ...(context.resumeSessionId ? [] : ['-C', context.workspace]),
        '-c',
        `mcp_servers.aictiq.command=${JSON.stringify(context.mcpServer.command)}`,
        '-c',
        `mcp_servers.aictiq.args=${JSON.stringify(context.mcpServer.args)}`,
        // Codex starts MCP servers with a scrubbed environment; without this the bridge
        // never sees the run's credential and the handshake fails. Names only - the values
        // stay in the process environment, out of argv.
        '-c',
        `mcp_servers.aictiq.env_vars=${JSON.stringify(MCP_ENV)}`,
        '-',
      ],
      stdin: context.prompt,
    }
  },

  parse(line): ParsedLine {
    let event: Record<string, unknown>
    try {
      const parsed = JSON.parse(line) as unknown
      event =
        recordOf(parsed) ??
        (() => {
          throw new Error('not an object')
        })()
    } catch {
      return { log: line }
    }

    if (event.type === 'thread.started' && typeof event.thread_id === 'string') {
      return { log: null, sessionId: event.thread_id }
    }

    const item = recordOf(event.item)
    if ((event.type === 'item.completed' || event.type === 'item.started') && item) {
      if (item.type === 'agent_message' && typeof item.text === 'string') {
        return { log: item.text, result: item.text }
      }
      // A command arrives twice, started and completed; log it once, when it starts.
      if (item.type === 'command_execution') {
        if (event.type === 'item.completed') return { log: null }
        const command =
          typeof item.command === 'string' ? item.command : JSON.stringify(item.command ?? '')
        return { log: `→ command ${limit(command, 200)}` }
      }
      if (item.type === 'file_change') {
        const changes = Array.isArray(item.changes) ? item.changes : []
        const paths = changes
          .map((change) => recordOf(change)?.path)
          .filter((path): path is string => typeof path === 'string')
        return { log: paths.length > 0 ? `→ file_change ${limit(paths.join(', '), 200)}` : null }
      }
    }

    const limits = limitsOf(event)
    const usage = usageOf(event)
    if (usage) {
      const input =
        numberOf(usage.input_tokens) +
        numberOf(usage.cached_input_tokens) +
        numberOf(usage.cache_write_input_tokens)
      return {
        log: null,
        inputTokens: input || undefined,
        outputTokens: numberOrUndefined(usage.output_tokens),
        ...(limits ? { limits } : {}),
      }
    }
    return { log: null, ...(limits ? { limits } : {}) }
  },

  limits(sessionId) {
    // `exec --json` leaves rate limits out of its output; the session's rollout file has them.
    if (!sessionId) return null
    const file = rolloutFile(sessionId)
    if (!file) return null
    let lines: string[]
    try {
      lines = readFileSync(file, 'utf8').split('\n')
    } catch {
      return null
    }
    for (let i = lines.length - 1; i >= 0; i--) {
      if (!lines[i]!.includes('"rate_limits"')) continue
      try {
        const limits = limitsOf(recordOf(JSON.parse(lines[i]!)) ?? {})
        if (limits) return limits
      } catch {
        // A line cut short while Codex was writing it; an earlier one will do.
      }
    }
    return null
  },

  outcome(exitCode, lastLines, lastResult, resuming) {
    if (exitCode !== 0) return failedOutcome(exitCode, lastLines, resuming)
    return {
      outcome: 'succeeded',
      failureReason: null,
      summary: limit(lastResult ?? lastLines.slice(-20).join('\n'), 4_000) || null,
    }
  },
}

function usageOf(event: Record<string, unknown>): Record<string, unknown> | null {
  if (event.type === 'turn.completed') return recordOf(event.usage)
  if (event.type === 'token_count') return recordOf(event.usage) ?? recordOf(event.info)
  const payload = recordOf(event.payload)
  if (payload?.type === 'token_count') {
    return recordOf(payload.usage) ?? recordOf(recordOf(payload.info)?.total_token_usage)
  }
  return null
}

/** Day directories looked through for a session's rollout, newest first: a resume appends to the original. */
const MaxRolloutDays = 60

/** `$CODEX_HOME/sessions/YYYY/MM/DD/rollout-<time>-<session id>.jsonl`. */
function rolloutFile(sessionId: string): string | null {
  const sessions = join(process.env.CODEX_HOME || join(homedir(), '.codex'), 'sessions')
  const newestFirst = (dir: string) => {
    try {
      return readdirSync(dir).filter((name) => /^\d+$/.test(name)).sort().reverse()
    } catch {
      return []
    }
  }
  let days = 0
  for (const year of newestFirst(sessions)) {
    for (const month of newestFirst(join(sessions, year))) {
      for (const day of newestFirst(join(sessions, year, month))) {
        if (++days > MaxRolloutDays) return null
        const dir = join(sessions, year, month, day)
        try {
          const name = readdirSync(dir).find((file) => file.endsWith(`-${sessionId}.jsonl`))
          if (name) return join(dir, name)
        } catch {
          // Removed meanwhile.
        }
      }
    }
  }
  return null
}

/**
 * `rate_limits` on a `token_count` event: `primary` and `secondary` windows, told apart by their
 * length (300 minutes, 10080 minutes) rather than their order.
 */
function limitsOf(event: Record<string, unknown>): ParsedLine['limits'] | null {
  const payload = recordOf(event.payload)
  const rateLimits = recordOf(event.rate_limits) ?? recordOf(payload?.rate_limits)
  if (!rateLimits) return null
  const observed = typeof event.timestamp === 'string' ? Date.parse(event.timestamp) : Date.now()
  const limits: NonNullable<ParsedLine['limits']> = {}
  for (const [key, fallback] of [
    ['primary', 'fiveHour'],
    ['secondary', 'weekly'],
  ] as const) {
    const window = recordOf(rateLimits[key])
    if (!window) continue
    const minutes = numberOrUndefined(window.window_minutes)
    const slot = minutes === 300 ? 'fiveHour' : minutes === 10080 ? 'weekly' : minutes ? null : fallback
    if (!slot) continue
    const resetsIn = numberOrUndefined(window.resets_in_seconds)
    const resetsAt =
      window.resets_at ??
      (resetsIn !== undefined && Number.isFinite(observed) ? observed / 1000 + resetsIn : null)
    const parsed = usageWindow(window.used_percent, resetsAt)
    if (parsed) limits[slot] = parsed
  }
  return Object.keys(limits).length > 0 ? limits : null
}

function numberOf(value: unknown): number {
  return typeof value === 'number' && Number.isFinite(value) ? value : 0
}

function numberOrUndefined(value: unknown): number | undefined {
  return typeof value === 'number' && Number.isFinite(value) ? value : undefined
}

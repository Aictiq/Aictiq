import { execFile } from 'node:child_process'
import { usageWindow } from '../limits.js'
import type { HarnessAdapter, HarnessInfo, ParsedLine } from '../types.js'
import { failedOutcome } from './failure.js'

const limit = (value: string, length: number) =>
  value.length > length ? `${value.slice(0, Math.max(0, length - 1))}…` : value

const recordOf = (value: unknown): Record<string, unknown> | null =>
  typeof value === 'object' && value !== null ? (value as Record<string, unknown>) : null

/** Reads only the first line: some CLIs add build information after their version. */
export function versionOf(
  command: string,
  env: NodeJS.ProcessEnv = process.env,
): Promise<string | null> {
  return new Promise((resolve) => {
    execFile(command, ['--version'], { env, timeout: 5_000 }, (error, stdout, stderr) => {
      if (error) return resolve(null)
      const output = String(stdout || stderr).trim()
      const firstLine = output.split(/\r?\n/)[0]?.slice(0, 100) ?? ''
      resolve(firstLine || null)
    })
  })
}

export const claude: HarnessAdapter = {
  name: 'claude',

  async available(env?: NodeJS.ProcessEnv): Promise<HarnessInfo | null> {
    const version = await versionOf('claude', env)
    return version === null ? null : { name: 'claude', version }
  },

  invocation(context) {
    return {
      command: 'claude',
      args: [
        '-p',
        '--output-format',
        'stream-json',
        '--verbose',
        '--permission-mode',
        'bypassPermissions',
        '--mcp-config',
        context.mcpConfigFile,
        // Claude's working directory remains the checkout. Attachments are deliberately
        // beside it, so grant this one read-only run directory explicitly.
        '--add-dir',
        context.attachmentsDir,
        // The session lives in Claude's own store, keyed by this checkout's path; the kept
        // workspace is the same path, so the conversation picks up where it stopped.
        ...(context.resumeSessionId ? ['--resume', context.resumeSessionId] : []),
      ],
      // Claude's -p mode consumes stdin when there is no positional prompt.
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

    if (event.type === 'system' && event.subtype === 'init') {
      return {
        log: `[init] model ${typeof event.model === 'string' ? event.model : 'unknown'}`,
        ...(typeof event.session_id === 'string' ? { sessionId: event.session_id } : {}),
      }
    }

    if (event.type === 'assistant') {
      const message = recordOf(event.message)
      const content = Array.isArray(message?.content) ? message.content : []
      const logs = content.flatMap((block) => {
        const part = recordOf(block)
        if (!part || part.type === 'thinking') return []
        if (part.type === 'text' && typeof part.text === 'string') return [part.text]
        if (part.type === 'tool_use') {
          const name = typeof part.name === 'string' ? part.name : 'tool'
          const input = limit(JSON.stringify(part.input ?? {}), 200)
          return [`→ ${name} ${input}`]
        }
        return []
      })
      return { log: logs.length > 0 ? logs.join('\n') : null }
    }

    if (event.type === 'user') {
      const message = recordOf(event.message)
      const content = Array.isArray(message?.content) ? message.content[0] : message?.content
      const result = recordOf(content)
      if (result?.type !== 'tool_result') return { log: null }
      const text =
        typeof result.content === 'string' ? result.content : JSON.stringify(result.content ?? '')
      const error = result.is_error === true ? '[error] ' : ''
      return { log: `← ${error}${limit(text.split(/\r?\n/)[0] ?? '', 200)}` }
    }

    if (event.type === 'rate_limit_event') {
      const limits = limitsOf(recordOf(event.rate_limit_info))
      return { log: null, ...(limits ? { limits } : {}) }
    }

    if (event.type === 'result') {
      const usage = recordOf(event.usage)
      const input =
        numberOf(usage?.input_tokens) +
        numberOf(usage?.cache_creation_input_tokens) +
        numberOf(usage?.cache_read_input_tokens)
      return {
        // The marker is what `outcome` reads: the runner keeps the logged lines, not the raw JSON.
        log: `[result] ${typeof event.subtype === 'string' ? event.subtype : 'unknown'}${event.is_error === true ? ' (error)' : ''} in ${numberOf(event.duration_ms)}ms`,
        costUsd: numberOrUndefined(event.total_cost_usd),
        inputTokens: input || undefined,
        outputTokens: numberOrUndefined(usage?.output_tokens),
        result: typeof event.result === 'string' ? event.result : undefined,
      }
    }

    return { log: null }
  },

  outcome(exitCode, lastLines, lastResult, resuming) {
    if (exitCode !== 0) return failedOutcome(exitCode, lastLines, resuming)
    // An error result (a refused prompt, an exhausted budget) can still exit 0; the result
    // line `parse` logged is what says so. Its text still tells a rate limit from a refusal.
    if (lastLines.some((line) => /^\[result\] .*\(error\)/.test(line))) {
      const lines = lastResult ? [...lastLines, lastResult] : lastLines
      return failedOutcome(exitCode, lines, resuming, 'harness-error')
    }
    return {
      outcome: 'succeeded',
      failureReason: null,
      summary: limit(lastResult ?? lastLines.slice(-20).join('\n'), 4_000) || null,
    }
  },
}

/**
 * A subscription's 5-hour and 7-day windows. `unifiedWindows` holds both, with utilization as a
 * fraction; without it, the event describes only the window named by `rateLimitType`.
 */
function limitsOf(info: Record<string, unknown> | null): ParsedLine['limits'] | null {
  if (!info) return null
  const windowOf = (window: Record<string, unknown> | null) =>
    window && typeof window.utilization === 'number'
      ? (usageWindow(window.utilization * 100, window.resetsAt) ?? undefined)
      : undefined
  const unified = recordOf(info.unifiedWindows)
  const limits: NonNullable<ParsedLine['limits']> = {}
  if (unified) {
    limits.fiveHour = windowOf(recordOf(unified.five_hour))
    limits.weekly = windowOf(recordOf(unified.seven_day))
  } else if (info.rateLimitType === 'five_hour') limits.fiveHour = windowOf(info)
  else if (info.rateLimitType === 'seven_day') limits.weekly = windowOf(info)
  if (limits.fiveHour === undefined) delete limits.fiveHour
  if (limits.weekly === undefined) delete limits.weekly
  return Object.keys(limits).length > 0 ? limits : null
}

function numberOf(value: unknown): number {
  return typeof value === 'number' && Number.isFinite(value) ? value : 0
}

function numberOrUndefined(value: unknown): number | undefined {
  return typeof value === 'number' && Number.isFinite(value) ? value : undefined
}

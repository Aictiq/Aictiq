import type { HarnessAdapter, HarnessInfo, ParsedLine } from '../types.js'
import { versionOf } from './claude.js'

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
    return {
      command: 'codex',
      args: [
        'exec',
        '--json',
        // 0.154.0 removed --full-auto. The runner is the ticket's explicit trust boundary:
        // the agent needs unrestricted network access for the MCP server and git push.
        '--dangerously-bypass-approvals-and-sandbox',
        // This mode also permits the absolute attachment paths named in prompt.md outside
        // the checkout. The runner itself remains the trust boundary for those files.
        '--skip-git-repo-check',
        '-C',
        context.workspace,
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
      }
    }
    return { log: null }
  },

  outcome(exitCode, lastLines, lastResult) {
    if (exitCode !== 0) return failedOutcome(exitCode, lastLines)
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

function failedOutcome(exitCode: number | null, lastLines: string[]) {
  return {
    outcome: 'failed' as const,
    failureReason: exitCode === null ? 'harness-killed' : `harness-exit-${exitCode}`,
    summary: limit(lastLines.slice(-20).join('\n'), 4_000) || null,
  }
}

function numberOf(value: unknown): number {
  return typeof value === 'number' && Number.isFinite(value) ? value : 0
}

function numberOrUndefined(value: unknown): number | undefined {
  return typeof value === 'number' && Number.isFinite(value) ? value : undefined
}

import { existsSync } from 'node:fs'
import type { HarnessAdapter, HarnessInfo, ParsedLine } from '../types.js'
import { versionOf } from './claude.js'
import { failedOutcome } from './failure.js'

const limit = (value: string, length: number) =>
  value.length > length ? `${value.slice(0, Math.max(0, length - 1))}…` : value

const recordOf = (value: unknown): Record<string, unknown> | null =>
  typeof value === 'object' && value !== null ? (value as Record<string, unknown>) : null

export const opencode: HarnessAdapter = {
  name: 'opencode',

  async available(env?: NodeJS.ProcessEnv): Promise<HarnessInfo | null> {
    const version = await versionOf('opencode', env)
    return version === null ? null : { name: 'opencode', version }
  },

  invocation(context) {
    return {
      command: 'opencode',
      // `--file` is a variadic option: anything after it is taken as another file to
      // attach, so the message goes first and the prompt file last. A resumed session
      // already has the instructions and attachments; it only gets the continue message.
      args: context.resumeSessionId
        ? [
            'run',
            context.prompt,
            '--format',
            'json',
            '--dir',
            context.workspace,
            '--session',
            context.resumeSessionId,
          ]
        : [
            'run',
            'Follow the instructions in the attached file.',
            '--format',
            'json',
            '--dir',
            context.workspace,
            '--file',
            context.promptFile,
            // The runner only creates the attachments directory when the item has
            // attachments, and OpenCode exits with "File not found" for a missing one.
            ...(existsSync(context.attachmentsDir) ? [context.attachmentsDir] : []),
          ],
      env: {
        OPENCODE_CONFIG_CONTENT: JSON.stringify({
          $schema: 'https://opencode.ai/config.json',
          mcp: {
            aictiq: {
              type: 'local',
              command: [context.mcpServer.command, ...context.mcpServer.args],
              enabled: true,
            },
          },
        }),
      },
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

    const part = recordOf(event.part)
    // Every event carries the session it belongs to, at the top or on its part.
    const session = event.sessionID ?? part?.sessionID
    const sessionId = typeof session === 'string' ? { sessionId: session } : {}
    if (!part) return { log: null, ...sessionId }
    if (event.type === 'text' && typeof part.text === 'string') {
      // Returning each text part as result lets the runner retain the final one per run.
      return { log: part.text, result: part.text, ...sessionId }
    }
    if (event.type === 'tool_use') {
      const tool =
        typeof part.tool === 'string'
          ? part.tool
          : typeof part.name === 'string'
            ? part.name
            : 'tool'
      const state = recordOf(part.state)
      return {
        log: `→ ${tool} ${limit(JSON.stringify(state?.input ?? part.input ?? {}), 200)}`,
        ...sessionId,
      }
    }
    // Each step reports its own usage, so the runner adds them up. Cache reads and writes
    // are input the model processed, as the Codex adapter counts them; reasoning is output.
    if (event.type === 'step_finish') {
      const tokens = recordOf(part.tokens)
      const cache = recordOf(tokens?.cache)
      return {
        log: null,
        accumulate: true,
        costUsd: numberOrUndefined(part.cost),
        inputTokens: sumOf(tokens?.input, cache?.read, cache?.write),
        outputTokens: sumOf(tokens?.output, tokens?.reasoning),
        ...sessionId,
      }
    }
    return { log: null, ...sessionId }
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

function sumOf(...values: unknown[]): number | undefined {
  const numbers = values.filter(
    (value): value is number => typeof value === 'number' && Number.isFinite(value),
  )
  return numbers.length === 0 ? undefined : numbers.reduce((total, value) => total + value, 0)
}

function numberOrUndefined(value: unknown): number | undefined {
  return typeof value === 'number' && Number.isFinite(value) ? value : undefined
}

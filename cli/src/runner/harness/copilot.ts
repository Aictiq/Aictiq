import { randomUUID } from 'node:crypto'
import { readFileSync, rmSync } from 'node:fs'
import { dirname, join } from 'node:path'
import type { HarnessAdapter, HarnessInfo, HarnessOutcome, ParsedLine } from '../types.js'
import { versionOf } from './claude.js'
import { failedOutcome } from './failure.js'

const limit = (value: string, length: number) =>
  value.length > length ? `${value.slice(0, Math.max(0, length - 1))}…` : value

const recordOf = (value: unknown): Record<string, unknown> | null =>
  typeof value === 'object' && value !== null && !Array.isArray(value)
    ? (value as Record<string, unknown>)
    : null

/** `GitHub Copilot CLI 1.0.92.` today, a bare `0.0.339` in older releases. */
const CopilotVersion = /(\d+\.\d+\.\d+(?:-[\w.]+)?)/

/** Beside prompt.md in the run directory, outside the checkout. */
const UsageFile = 'copilot-usage.json'

const NotAuthenticated =
  /not authenticated|no authentication information|authentication (?:failed|required)|bad credentials|not logged in|please (?:run|use) [`'"]?(?:copilot login|\/login)/i
// Signed in, but the account has no Copilot plan, or its organization turned the CLI off.
const NotEntitled =
  /no (?:active )?(?:github )?copilot (?:subscription|plan|license|access)|(?:not|isn't) (?:entitled|subscribed) to|(?:do|does) not have (?:access to )?(?:an? )?(?:active )?(?:github )?copilot|copilot (?:cli )?(?:is )?(?:not enabled|disabled)/i
const ModelUnavailable =
  /model "[^"]*" from --model flag is not available|model .*not available|(?:unknown|invalid|unsupported) model|model not (?:found|supported)/i
const SessionMissing = /no session, task, or name matched/i
// Copilot bills premium requests against a monthly allowance; running out is a limit, not a fault.
const QuotaExhausted =
  /premium requests? (?:limit|quota|allowance)|exceeded your (?:monthly )?(?:premium )?(?:request|quota|allowance)/i

export const copilot: HarnessAdapter = {
  name: 'copilot',

  async available(env?: NodeJS.ProcessEnv): Promise<HarnessInfo | null> {
    const version = await versionOf('copilot', env)
    const match = version === null ? null : CopilotVersion.exec(version)
    return match ? { name: 'copilot', version: match[1]! } : null
  },

  invocation(context) {
    // Copilot only names its session in the final `result` event, so a run that stops halfway
    // would leave nothing to resume. The runner picks the id up front instead.
    const sessionId = context.resumeSessionId ?? randomUUID()
    return {
      command: 'copilot',
      args: [
        '--output-format',
        'json',
        // Tools, paths and URLs without prompts: the runner is the ticket's explicit trust
        // boundary, as for the other harnesses. Paths include the attachments beside the
        // checkout; --add-dir is left out because it would also load an attachment's
        // .github/skills and agents as trusted configuration.
        '--allow-all',
        '--no-ask-user',
        // The runner's own mcp.json, outside the checkout. Copilot starts local MCP servers
        // with its environment, which holds the run's credential, so the file has no values.
        // --secret-env-vars would redact AICTIQ_TOKEN from the server too, so it is not used.
        '--additional-mcp-config',
        `@${context.mcpConfigFile}`,
        // Token counts are only written here, once Copilot exits; `usage` reads them.
        '--usage-output-file',
        usageFile(context.promptFile),
        // --resume fails on an unknown id, where --session-id would silently start afresh.
        context.resumeSessionId ? `--resume=${sessionId}` : `--session-id=${sessionId}`,
      ],
      // Copilot reads the prompt from stdin when there is no -p.
      stdin: context.prompt,
      sessionId,
    }
  },

  usage(context) {
    // A resumed session reports its whole history, which the earlier run already counted.
    if (context.resumeSessionId) return null
    const file = usageFile(context.promptFile)
    try {
      const details = recordOf(recordOf(JSON.parse(readFileSync(file, 'utf8')))?.tokenDetails)
      const count = (key: string) => numberOf(recordOf(details?.[key])?.tokenCount)
      const input = count('input') + count('cache_read') + count('cache_write')
      const output = count('output')
      return details ? { inputTokens: input, outputTokens: output } : null
    } catch {
      return null
    } finally {
      rmSync(file, { force: true })
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
    const type = typeof event.type === 'string' ? event.type : ''
    const data = recordOf(event.data)

    // Sent once the session's tools are ready, naming the model it settled on.
    if (type === 'session.tools_updated') {
      return { log: `[init] model ${typeof data?.model === 'string' ? data.model : 'unknown'}` }
    }

    // One event per whole message; streamed deltas and reasoning are dropped. A message that
    // only requests tools has empty content. The last message with text is the summary.
    if (type === 'assistant.message') {
      const text = typeof data?.content === 'string' ? data.content : ''
      return text.trim() ? { log: text, result: text } : { log: null }
    }

    // Logged once, when it starts, with its name and arguments only: a tool's output may
    // print anything in the run's environment, the token included.
    if (type === 'tool.execution_start') {
      const server = data?.mcpServerName
      const tool = data?.mcpToolName
      const name =
        typeof server === 'string' && typeof tool === 'string'
          ? `${server}.${tool}`
          : typeof data?.toolName === 'string'
            ? data.toolName
            : 'tool'
      return { log: `→ ${name} ${limit(JSON.stringify(data?.arguments ?? {}), 200)}` }
    }

    if (type.endsWith('.error') || type.endsWith('_error')) {
      const text = typeof data?.message === 'string' ? data.message : JSON.stringify(data ?? {})
      return { log: `[error] ${limit(text, 1_000)}` }
    }

    if (type === 'result') {
      const usage = recordOf(event.usage)
      const exitCode = numberOf(event.exitCode)
      const premium = numberOf(usage?.premiumRequests)
      return {
        // The marker is what `outcome` reads: the runner keeps the logged lines, not the raw JSON.
        log: `[result] exit ${exitCode}${exitCode !== 0 ? ' (error)' : ''} in ${numberOf(usage?.sessionDurationMs)}ms, ${premium} premium request${premium === 1 ? '' : 's'}`,
        ...(typeof event.sessionId === 'string' ? { sessionId: event.sessionId } : {}),
      }
    }

    // MCP and tool notices, the prompt echo, turn boundaries and usage checkpoints.
    return { log: null }
  },

  outcome(exitCode, lastLines, lastResult, resuming): HarnessOutcome {
    const errored = lastLines.some((line) => /^\[result\] .*\(error\)/.test(line))
    if (exitCode === 0 && !errored) {
      return {
        outcome: 'succeeded',
        failureReason: null,
        summary: limit(lastResult ?? lastLines.slice(-20).join('\n'), 4_000) || null,
      }
    }
    const text = lastLines.join('\n')
    if (resuming && SessionMissing.test(text))
      return failedOutcome(exitCode, lastLines, resuming, 'session-unavailable')
    if (QuotaExhausted.test(text))
      return failedOutcome(exitCode, lastLines, resuming, 'harness-rate-limited')
    if (NotAuthenticated.test(text) || NotEntitled.test(text))
      return failedOutcome(exitCode, lastLines, resuming, 'harness-not-authenticated')
    if (ModelUnavailable.test(text))
      return failedOutcome(exitCode, lastLines, resuming, 'harness-model-unavailable')
    return failedOutcome(exitCode, lastLines, resuming, errored ? 'harness-error' : undefined)
  },
}

function usageFile(promptFile: string): string {
  return join(dirname(promptFile), UsageFile)
}

function numberOf(value: unknown): number {
  return typeof value === 'number' && Number.isFinite(value) ? value : 0
}

import { execFile } from 'node:child_process'
import {
  existsSync,
  mkdirSync,
  readFileSync,
  readdirSync,
  rmdirSync,
  rmSync,
  writeFileSync,
} from 'node:fs'
import { isAbsolute, join } from 'node:path'
import type { HarnessAdapter, HarnessInfo, HarnessOutcome, ParsedLine } from '../types.js'
import { versionOf } from './claude.js'
import { failedOutcome } from './failure.js'

const limit = (value: string, length: number) =>
  value.length > length ? `${value.slice(0, Math.max(0, length - 1))}…` : value

const recordOf = (value: unknown): Record<string, unknown> | null =>
  typeof value === 'object' && value !== null && !Array.isArray(value)
    ? (value as Record<string, unknown>)
    : null

const MCP_ENV = ['AICTIQ_URL', 'AICTIQ_TOKEN', 'AICTIQ_ORG', 'AICTIQ_ITEM', 'AICTIQ_RUN']

/** Where Cursor looks for a project's MCP servers; it has no flag for them. */
export const CURSOR_MCP_FILE = '.cursor/mcp.json'
const ExcludeLine = '/.cursor/mcp.json'

// Current installs ship both names; `agent` is generic enough that another program may own
// it, so only a Cursor-style version (a date and a build hash) counts.
const Commands = ['agent', 'cursor-agent'] as const
const CursorVersion = /^\d{4}\.\d{1,2}\.\d{1,2}/

/** The executable `available` last found, which `invocation` then starts. */
let command: string = Commands[0]

const NotAuthenticated =
  /authentication required|not logged in|api key is invalid|invalid api key|unauthenticated|please run '?(?:cursor-)?agent login/i
const ModelUnavailable =
  /model not found|cannot use this model|invalid model|unknown model|model .*not available/i
const ChatMissing =
  /\b(?:chat|session|conversation)\b[^\n]{0,80}\bnot found\b|no previous chats found/i

export const cursor: HarnessAdapter = {
  name: 'cursor',

  async available(env?: NodeJS.ProcessEnv): Promise<HarnessInfo | null> {
    for (const candidate of Commands) {
      const version = await versionOf(candidate, env)
      if (version !== null && CursorVersion.test(version)) {
        command = candidate
        return { name: 'cursor', version }
      }
    }
    return null
  },

  async prepare(context) {
    return writeMcpConfig(context.workspace, context.mcpServer)
  },

  invocation(context) {
    return {
      command,
      args: [
        '-p',
        '--output-format',
        'stream-json',
        // Without --force, edits and shell commands are only proposed. The runner is the
        // ticket's explicit trust boundary, as for the other harnesses.
        '--force',
        // Headless mode would otherwise stop at the workspace trust and MCP approval prompts.
        '--trust',
        '--approve-mcps',
        '--workspace',
        context.workspace,
        '--add-dir',
        context.attachmentsDir,
        // Cursor keeps its chats per workspace path; the kept workspace is the same path.
        ...(context.resumeSessionId ? ['--resume', context.resumeSessionId] : []),
        // Cursor takes its prompt as an argument only, so it gets the path of the 0600 prompt
        // file, never the prompt itself. A resumed run's file holds the continue message.
        `Read ${context.promptFile} and follow the instructions in it.`,
      ],
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

    // Every event names the chat; `--resume` takes it.
    const sessionId = typeof event.session_id === 'string' ? { sessionId: event.session_id } : {}

    if (event.type === 'system' && event.subtype === 'init') {
      return {
        log: `[init] model ${typeof event.model === 'string' ? event.model : 'unknown'}`,
        ...sessionId,
      }
    }

    if (event.type === 'assistant') {
      const message = recordOf(event.message)
      const content = Array.isArray(message?.content) ? message.content : []
      const text = content
        .map((block) => recordOf(block))
        .filter((part) => part?.type === 'text' && typeof part.text === 'string')
        .map((part) => part!.text as string)
        .join('')
      return { log: text.trim() ? text : null, ...sessionId }
    }

    // A tool call arrives started and completed; log it once, when it starts. The completed
    // event of a shell call may carry an environment snapshot, the run's token included, so
    // nothing but the tool's name and arguments ever reaches the log.
    if (event.type === 'tool_call') {
      if (event.subtype !== 'started') return { log: null, ...sessionId }
      const call = recordOf(event.tool_call)
      const [kind, value] = Object.entries(call ?? {})[0] ?? ['tool', {}]
      const name = toolName(kind, recordOf(value))
      const args = recordOf(value)?.args ?? {}
      return { log: `→ ${name} ${limit(JSON.stringify(args), 200)}`, ...sessionId }
    }

    if (event.type === 'result') {
      const usage = recordOf(event.usage)
      const input =
        numberOf(usage?.inputTokens) +
        numberOf(usage?.cacheReadTokens) +
        numberOf(usage?.cacheWriteTokens)
      return {
        // The marker is what `outcome` reads: the runner keeps the logged lines, not the raw JSON.
        log: `[result] ${typeof event.subtype === 'string' ? event.subtype : 'unknown'}${event.is_error === true ? ' (error)' : ''} in ${numberOf(event.duration_ms)}ms`,
        inputTokens: input || undefined,
        outputTokens: numberOrUndefined(usage?.outputTokens),
        ...(typeof event.result === 'string' ? { result: event.result } : {}),
        ...sessionId,
      }
    }

    // The prompt echo, thinking deltas and connection notices stay out of the log.
    return { log: null, ...sessionId }
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
    const lines = errored && lastResult ? [...lastLines, lastResult] : lastLines
    const text = lines.join('\n')
    if (resuming && ChatMissing.test(text))
      return failedOutcome(exitCode, lines, resuming, 'session-unavailable')
    if (NotAuthenticated.test(text))
      return failedOutcome(exitCode, lines, resuming, 'harness-not-authenticated')
    if (ModelUnavailable.test(text))
      return failedOutcome(exitCode, lines, resuming, 'harness-model-unavailable')
    return failedOutcome(exitCode, lines, resuming, errored ? 'harness-error' : undefined)
  },
}

/** `readToolCall` → `read`; an MCP call is named by its server and tool. */
function toolName(kind: string, value: Record<string, unknown> | null): string {
  if (kind === 'mcpToolCall') {
    const args = recordOf(value?.args)
    const tool = args?.toolName ?? args?.name
    const server = args?.providerIdentifier ?? args?.serverName
    if (typeof tool === 'string') return typeof server === 'string' ? `${server}.${tool}` : tool
  }
  return kind.replace(/ToolCall$/, '') || 'tool'
}

/**
 * Puts the Aictiq server into the checkout's `.cursor/mcp.json` for the run and returns how to
 * put the file back. The file names the run's variables, never their values: Cursor expands
 * `${env:NAME}` from its own environment, which the runner gives the run's credential. Git is
 * told to leave the file alone - excluded when it is new, skip-worktree when the repository
 * tracks one - so an agent's `git add -A` cannot commit it.
 */
export async function writeMcpConfig(
  checkout: string,
  server: { command: string; args: string[] },
): Promise<() => Promise<void>> {
  const dir = join(checkout, '.cursor')
  const file = join(checkout, CURSOR_MCP_FILE)
  const createdDir = !existsSync(dir)
  const original = existsSync(file) ? readFileSync(file, 'utf8') : null
  let config: Record<string, unknown> = {}
  if (original !== null) {
    try {
      config = recordOf(JSON.parse(original)) ?? {}
    } catch {
      // A file Cursor could not read either; the run's copy replaces it until the run ends.
    }
  }
  const servers = recordOf(config.mcpServers) ?? {}
  const env = Object.fromEntries(MCP_ENV.map((name) => [name, `\${env:${name}}`]))
  const merged = {
    ...config,
    mcpServers: { ...servers, aictiq: { command: server.command, args: server.args, env } },
  }

  const tracked = await git(checkout, ['ls-files', '--error-unmatch', '--', CURSOR_MCP_FILE])
    .then(() => true)
    .catch(() => false)
  const excludeFile = tracked ? null : await gitPath(checkout, 'info/exclude').catch(() => null)
  if (tracked) await git(checkout, ['update-index', '--skip-worktree', '--', CURSOR_MCP_FILE])
  const excluded = excludeFile !== null && addLine(excludeFile, ExcludeLine)

  mkdirSync(dir, { recursive: true })
  writeFileSync(file, `${JSON.stringify(merged, null, 2)}\n`, { mode: 0o600 })

  return async () => {
    if (original === null) rmSync(file, { force: true })
    else writeFileSync(file, original)
    if (createdDir && existsSync(dir) && readdirSync(dir).length === 0) rmdirSync(dir)
    if (tracked)
      await git(checkout, ['update-index', '--no-skip-worktree', '--', CURSOR_MCP_FILE]).catch(
        () => {},
      )
    if (excluded) removeLine(excludeFile!, ExcludeLine)
  }
}

/** False when the line was already there, so the restore leaves it alone. */
function addLine(file: string, line: string): boolean {
  const current = existsSync(file) ? readFileSync(file, 'utf8') : ''
  if (current.split(/\r?\n/).includes(line)) return false
  mkdirSync(join(file, '..'), { recursive: true })
  writeFileSync(file, `${current}${current && !current.endsWith('\n') ? '\n' : ''}${line}\n`)
  return true
}

function removeLine(file: string, line: string) {
  if (!existsSync(file)) return
  const current = readFileSync(file, 'utf8')
  const kept = current.split('\n').filter((existing) => existing !== line)
  writeFileSync(file, kept.join('\n'))
}

async function gitPath(checkout: string, path: string): Promise<string> {
  const resolved = (await git(checkout, ['rev-parse', '--git-path', path])).trim()
  return isAbsolute(resolved) ? resolved : join(checkout, resolved)
}

function git(cwd: string, args: string[]): Promise<string> {
  return new Promise((resolve, reject) => {
    execFile('git', args, { cwd, timeout: 10_000 }, (error, stdout) =>
      error ? reject(error) : resolve(String(stdout)),
    )
  })
}

function numberOf(value: unknown): number {
  return typeof value === 'number' && Number.isFinite(value) ? value : 0
}

function numberOrUndefined(value: unknown): number | undefined {
  return typeof value === 'number' && Number.isFinite(value) ? value : undefined
}

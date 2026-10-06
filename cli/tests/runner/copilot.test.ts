import { chmodSync, existsSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { copilot } from '../../src/runner/harness/copilot.js'
import type { InvocationContext } from '../../src/runner/types.js'

const context: InvocationContext = {
  prompt: 'Do the work, including a line that must stay out of argv.',
  promptFile: '/run/dir/prompt.md',
  workspace: '/run/dir/repo',
  attachmentsDir: '/run/dir/attachments',
  mcpConfigFile: '/run/dir/mcp.json',
  mcpServer: { command: 'aictiq', args: ['mcp', '--stdio'] },
}

// Captured from GitHub Copilot CLI 1.0.92 (`--output-format json`), with the streamed tool-call
// deltas, background task notices and the usage checkpoint left out.
const fixture = readFileSync(new URL('../fixtures/harness/copilot.jsonl', import.meta.url), 'utf8')
  .trim()
  .split('\n')

const session = '11111111-2222-4333-8444-555555555555'

describe('copilot invocation', () => {
  it('runs headless with every permission, the prompt on stdin and a session of its own', () => {
    const invocation = copilot.invocation(context)
    expect(invocation.command).toBe('copilot')
    const sessionArg = invocation.args.at(-1)!
    expect(sessionArg).toMatch(/^--session-id=[0-9a-f-]{36}$/)
    expect(invocation.sessionId).toBe(sessionArg.slice('--session-id='.length))
    expect(invocation.args).toEqual([
      '--output-format',
      'json',
      '--allow-all',
      '--no-ask-user',
      '--additional-mcp-config',
      '@/run/dir/mcp.json',
      '--usage-output-file',
      '/run/dir/copilot-usage.json',
      sessionArg,
    ])
    expect(invocation.args.join(' ')).not.toContain(context.prompt)
    expect(invocation.stdin).toBe(context.prompt)
    // Trusted skills and agents would load from an attachment's .github directory.
    expect(invocation.args).not.toContain('--add-dir')
  })

  it('starts each run in a new session', () => {
    expect(copilot.invocation(context).sessionId).not.toBe(copilot.invocation(context).sessionId)
  })

  it('resumes a session by id, which fails rather than starting afresh when it is gone', () => {
    const invocation = copilot.invocation({ ...context, resumeSessionId: session })
    expect(invocation.args.at(-1)).toBe(`--resume=${session}`)
    expect(invocation.sessionId).toBe(session)
  })
})

describe('copilot json parser', () => {
  const parsed = fixture.map((line) => copilot.parse(line))

  it('logs the model, tool calls and messages, and drops deltas and notices', () => {
    expect(parsed.map((line) => line.log).filter((log) => log !== null)).toEqual([
      '[init] model mai-code-1.1-flash',
      '→ bash {"command":"echo hello","description":"Print a greeting as requested","mode":"sync","initial_wait":10}',
      '→ probe.envcheck {}',
      'Hello from the environment check.',
      '[result] exit 0 in 7105ms, 1 premium request',
    ])
  })

  it('reads the session and takes the last message as the result', () => {
    expect(new Set(parsed.map((line) => line.sessionId).filter(Boolean))).toEqual(
      new Set([session]),
    )
    const results = parsed.filter((line) => line.result !== undefined).map((line) => line.result)
    expect(results).toEqual(['Hello from the environment check.'])
  })

  it('never logs what a tool printed', () => {
    const line = JSON.stringify({
      type: 'tool.execution_complete',
      data: {
        toolCallId: 'call-1',
        success: true,
        result: { content: 'AICTIQ_TOKEN=secret-run-token' },
      },
    })
    const output = JSON.stringify({
      type: 'tool.shell_output',
      data: { toolCallId: 'call-1', stream: 'stdout', text: 'AICTIQ_TOKEN=secret-run-token\n' },
    })
    expect(copilot.parse(line)).toEqual({ log: null })
    expect(copilot.parse(output)).toEqual({ log: null })
  })

  it('marks a failed result and logs an error event', () => {
    const result = copilot.parse(
      JSON.stringify({ type: 'result', sessionId: session, exitCode: 1, usage: {} }),
    )
    expect(result.log).toBe('[result] exit 1 (error) in 0ms, 0 premium requests')
    expect(copilot.parse('{"type":"session.error","data":{"message":"boom"}}').log).toBe(
      '[error] boom',
    )
  })

  it('keeps malformed output as a log line', () => {
    expect(copilot.parse('not json')).toEqual({ log: 'not json' })
  })
})

describe('copilot usage file', () => {
  let dir: string
  beforeEach(() => {
    dir = mkdtempSync(join(tmpdir(), 'aictiq-copilot-run-'))
  })
  afterEach(() => rmSync(dir, { recursive: true, force: true }))

  const at = (extra: Partial<InvocationContext> = {}) => ({
    ...context,
    promptFile: join(dir, 'prompt.md'),
    ...extra,
  })
  const write = () =>
    writeFileSync(
      join(dir, 'copilot-usage.json'),
      JSON.stringify({
        totalPremiumRequestCost: 1,
        tokenDetails: {
          input: { tokenCount: 5332 },
          cache_read: { tokenCount: 31232 },
          cache_write: { tokenCount: 10 },
          output: { tokenCount: 62 },
        },
      }),
    )

  it('counts input with the cache, and removes the file', () => {
    write()
    expect(copilot.usage!(at())).toEqual({ inputTokens: 5332 + 31232 + 10, outputTokens: 62 })
    expect(existsSync(join(dir, 'copilot-usage.json'))).toBe(false)
  })

  it('reports nothing when Copilot wrote no file', () => {
    expect(copilot.usage!(at())).toBeNull()
  })

  it('reports nothing for a resumed session, whose file counts the earlier run too', () => {
    write()
    expect(copilot.usage!(at({ resumeSessionId: session }))).toBeNull()
  })
})

describe('copilot outcomes', () => {
  it('succeeds with the final message as summary', () => {
    expect(copilot.outcome(0, ['[result] exit 0 in 5ms, 1 premium request'], 'all done')).toEqual({
      outcome: 'succeeded',
      failureReason: null,
      summary: 'all done',
    })
  })

  it('maps the errors the CLI prints', () => {
    // Printed by 1.0.92 for a model the account cannot use.
    expect(
      copilot.outcome(1, ['Error: Model "gpt-9" from --model flag is not available.'], null)
        .failureReason,
    ).toBe('harness-model-unavailable')
    // Printed by 1.0.92 when --resume names no stored session.
    const missing = [`Error: No session, task, or name matched '${session}'.`]
    expect(copilot.outcome(1, missing, null, true).failureReason).toBe('session-unavailable')
    expect(copilot.outcome(1, missing, null, false).failureReason).toBe('harness-exit-1')
    expect(
      copilot.outcome(1, ['Error: Not authenticated. Please run `copilot login`.'], null)
        .failureReason,
    ).toBe('harness-not-authenticated')
    expect(
      copilot.outcome(1, ['Error: You do not have an active GitHub Copilot subscription.'], null)
        .failureReason,
    ).toBe('harness-not-authenticated')
    expect(
      copilot.outcome(1, ["Error: You've exceeded your premium request allowance."], null)
        .failureReason,
    ).toBe('harness-rate-limited')
    expect(copilot.outcome(1, ['429 Too Many Requests'], null).failureReason).toBe(
      'harness-rate-limited',
    )
    expect(copilot.outcome(1, ['fetch failed'], null).failureReason).toBe('harness-transient')
  })

  it('fails an error result despite a zero exit', () => {
    expect(
      copilot.outcome(0, ['[result] exit 1 (error) in 5ms, 0 premium requests'], null)
        .failureReason,
    ).toBe('harness-error')
  })
})

describe('copilot detection', () => {
  let bin: string
  beforeEach(() => {
    bin = mkdtempSync(join(tmpdir(), 'aictiq-copilot-bin-'))
  })
  afterEach(() => rmSync(bin, { recursive: true, force: true }))

  const script = (output: string) => {
    writeFileSync(join(bin, 'copilot'), `#!/bin/sh\nprintf '${output}'\n`)
    chmodSync(join(bin, 'copilot'), 0o755)
  }

  it.skipIf(process.platform === 'win32')('reports the version copilot prints', async () => {
    script("GitHub Copilot CLI 1.0.92.\\nRun 'copilot update' to check for updates.\\n")
    await expect(copilot.available({ PATH: bin })).resolves.toEqual({
      name: 'copilot',
      version: '1.0.92',
    })
  })

  it.skipIf(process.platform === 'win32')('is absent when copilot is not on PATH', async () => {
    await expect(copilot.available({ PATH: bin })).resolves.toBeNull()
  })
})

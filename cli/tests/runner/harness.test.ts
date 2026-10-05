import { mkdtempSync, readFileSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { describe, expect, it } from 'vitest'
import { claude } from '../../src/runner/harness/claude.js'
import { codex } from '../../src/runner/harness/codex.js'
import { harnesses, probeHarnesses, versionOf } from '../../src/runner/harness/index.js'
import { opencode } from '../../src/runner/harness/opencode.js'
import type { InvocationContext } from '../../src/runner/types.js'

const context: InvocationContext = {
  prompt: 'Do the work, including a line that must stay out of argv.',
  promptFile: '/tmp/aictiq-prompt',
  workspace: '/work/run',
  attachmentsDir: '/work/attachments',
  mcpConfigFile: '/tmp/aictiq-mcp.json',
  mcpServer: { command: 'aictiq', args: ['mcp', '--stdio'] },
}

const fixture = (name: string) =>
  readFileSync(new URL(`../fixtures/harness/${name}.jsonl`, import.meta.url), 'utf8')
    .trim()
    .split('\n')

describe('harness invocations', () => {
  it('keeps Claude and Codex prompts off argv', () => {
    const claudeInvocation = claude.invocation(context)
    const codexInvocation = codex.invocation(context)

    expect(claudeInvocation.args).toEqual([
      '-p',
      '--output-format',
      'stream-json',
      '--verbose',
      '--permission-mode',
      'bypassPermissions',
      '--mcp-config',
      '/tmp/aictiq-mcp.json',
      '--add-dir',
      '/work/attachments',
    ])
    expect(claudeInvocation.stdin).toBe(context.prompt)
    expect(claudeInvocation.args.join(' ')).not.toContain(context.prompt)
    expect(codexInvocation.args).toContain('--dangerously-bypass-approvals-and-sandbox')
    expect(codexInvocation.args).not.toContain(context.attachmentsDir)
    expect(codexInvocation.args).toContain('--skip-git-repo-check')
    expect(codexInvocation.args).toContain('mcp_servers.aictiq.command="aictiq"')
    expect(codexInvocation.args).toContain('mcp_servers.aictiq.args=["mcp","--stdio"]')
    expect(codexInvocation.args).toContain(
      'mcp_servers.aictiq.env_vars=["AICTIQ_URL","AICTIQ_TOKEN","AICTIQ_ORG","AICTIQ_ITEM","AICTIQ_RUN"]',
    )
    expect(codexInvocation.args).toContain('-')
    expect(codexInvocation.args.join(' ')).not.toContain(context.prompt)
    expect(codexInvocation.stdin).toBe(context.prompt)
  })

  it('passes OpenCode a prompt file and an MCP config override', () => {
    const attachmentsDir = mkdtempSync(join(tmpdir(), 'aictiq-opencode-attachments-'))
    const invocation = opencode.invocation({ ...context, attachmentsDir })
    rmSync(attachmentsDir, { recursive: true, force: true })
    expect(invocation.args).toEqual([
      'run',
      'Follow the instructions in the attached file.',
      '--format',
      'json',
      '--dir',
      '/work/run',
      '--file',
      '/tmp/aictiq-prompt',
      attachmentsDir,
    ])
    expect(JSON.parse(invocation.env?.OPENCODE_CONFIG_CONTENT ?? '')).toEqual({
      $schema: 'https://opencode.ai/config.json',
      mcp: {
        aictiq: {
          type: 'local',
          command: ['aictiq', 'mcp', '--stdio'],
          enabled: true,
        },
      },
    })
  })

  it('attaches no directory to OpenCode when the item has no attachments', () => {
    // OpenCode exits with "File not found" for a missing --file path.
    expect(opencode.invocation(context).args.slice(-2)).toEqual(['--file', '/tmp/aictiq-prompt'])
  })
})

describe('harness output parsers', () => {
  it.each([
    ['claude', claude],
    ['codex', codex],
    ['opencode', opencode],
  ] as const)('parses every %s fixture line without throwing', (name, harness) => {
    expect(() => fixture(name).forEach((line) => harness.parse(line))).not.toThrow()
  })

  it('extracts Claude costs, tokens, and final result', () => {
    const resultLine = fixture('claude').find((line) => JSON.parse(line).type === 'result')!
    expect(claude.parse(resultLine)).toMatchObject({
      costUsd: 0.21175500000000003,
      inputTokens: 35255,
      outputTokens: 435,
      result: 'done',
    })
  })

  it('extracts Codex and OpenCode token counts', () => {
    const codexLine = fixture('codex').find((line) => JSON.parse(line).type === 'turn.completed')!
    const opencodeLine = fixture('opencode').find(
      (line) => JSON.parse(line).type === 'step_finish',
    )!
    expect(codex.parse(codexLine)).toMatchObject({ inputTokens: 53905, outputTokens: 92 })
    expect(opencode.parse(opencodeLine)).toMatchObject({
      accumulate: true,
      inputTokens: 4406 + 2752,
      outputTokens: 69,
      costUsd: 0,
    })
  })

  it('logs a Codex command once, when it starts', () => {
    const command = (type: string) =>
      JSON.stringify({ type, item: { id: 'i1', type: 'command_execution', command: 'ls' } })
    expect(codex.parse(command('item.started'))).toEqual({ log: '→ command ls' })
    expect(codex.parse(command('item.completed'))).toEqual({ log: null })
  })

  it('preserves malformed output as a log line', () => {
    expect(claude.parse('{not json')).toEqual({ log: '{not json' })
    expect(codex.parse('not json')).toEqual({ log: 'not json' })
    expect(opencode.parse('not json')).toEqual({ log: 'not json' })
  })
})

describe('session ids and resume invocations', () => {
  it.each([
    ['claude', claude, 'ed68b27b-fc06-4480-91fd-685c05f3a581'],
    ['codex', codex, '01a0b01c-1219-78a1-bd0c-aa8b55d7a829'],
    ['opencode', opencode, 'ses_f4fe5eee7ffe7zgcmFa6ohCxwN'],
  ] as const)('reads the %s session id from its fixture', (name, harness, id) => {
    const ids = fixture(name)
      .map((line) => harness.parse(line).sessionId)
      .filter((value) => value !== undefined)
    expect(ids.length).toBeGreaterThan(0)
    expect(new Set(ids)).toEqual(new Set([id]))
  })

  const resuming: InvocationContext = {
    ...context,
    prompt: 'Continue where you stopped.',
    resumeSessionId: 'sess-1',
  }

  it('resumes Claude by session id with the continue message on stdin', () => {
    const invocation = claude.invocation(resuming)
    expect(invocation.args.slice(-2)).toEqual(['--resume', 'sess-1'])
    expect(invocation.args).toContain('--mcp-config')
    expect(invocation.stdin).toBe('Continue where you stopped.')
  })

  it('resumes Codex with exec resume, reading the message from stdin', () => {
    const invocation = codex.invocation(resuming)
    expect(invocation.args.slice(0, 4)).toEqual(['exec', 'resume', 'sess-1', '--json'])
    // `exec resume` has no -C; the runner starts it in the checkout.
    expect(invocation.args).not.toContain('-C')
    expect(invocation.args).toContain('mcp_servers.aictiq.command="aictiq"')
    expect(invocation.args.at(-1)).toBe('-')
    expect(invocation.stdin).toBe('Continue where you stopped.')
  })

  it('resumes OpenCode with --session and only the continue message', () => {
    const invocation = opencode.invocation(resuming)
    expect(invocation.args).toEqual([
      'run',
      'Continue where you stopped.',
      '--format',
      'json',
      '--dir',
      '/work/run',
      '--session',
      'sess-1',
    ])
    expect(invocation.env?.OPENCODE_CONFIG_CONTENT).toContain('aictiq')
  })
})

describe('outcomes and capability probes', () => {
  it('maps success, failures, and killed harnesses', () => {
    expect(codex.outcome(0, ['last line'], 'final answer')).toEqual({
      outcome: 'succeeded',
      failureReason: null,
      summary: 'final answer',
    })
    expect(opencode.outcome(2, ['one', 'two'], null)).toEqual({
      outcome: 'failed',
      failureReason: 'harness-exit-2',
      summary: 'one\ntwo',
    })
    expect(claude.outcome(null, ['last'], null)).toMatchObject({
      outcome: 'failed',
      failureReason: 'harness-crashed',
    })
  })

  it('tells transient failures from the rest so the instance can continue them', () => {
    expect(claude.outcome(1, ['API Error: 429 rate_limit_error'], null).failureReason).toBe(
      'harness-rate-limited',
    )
    expect(codex.outcome(1, ['stream disconnected before completion'], null).failureReason).toBe(
      'harness-transient',
    )
    expect(opencode.outcome(1, ['Error: overloaded_error'], null).failureReason).toBe(
      'harness-transient',
    )
    expect(codex.outcome(1, ['error: unknown flag'], null).failureReason).toBe('harness-exit-1')
    // A refused prompt stays a plain error; the same result with a rate limit does not.
    const logged = claude.parse(
      '{"type":"result","subtype":"success","is_error":true,"duration_ms":5,"result":"nope"}',
    ).log!
    expect(claude.outcome(0, [logged], 'nope').failureReason).toBe('harness-error')
    expect(claude.outcome(0, [logged], 'Claude AI usage limit reached').failureReason).toBe(
      'harness-rate-limited',
    )
  })

  it('reports a session that will not resume as session-unavailable, only when resuming', () => {
    const lines = ['No conversation found with session ID: 0b7e']
    expect(claude.outcome(1, lines, null, true).failureReason).toBe('session-unavailable')
    expect(claude.outcome(1, lines, null, false).failureReason).toBe('harness-exit-1')
  })

  it('treats a Claude error result as a failure despite a zero exit', () => {
    const logged = claude.parse(
      '{"type":"result","subtype":"success","is_error":true,"duration_ms":5,"result":"nope"}',
    ).log!
    expect(claude.outcome(0, [logged], 'nope')).toMatchObject({
      outcome: 'failed',
      failureReason: 'harness-error',
    })
  })

  it('returns null for an absent executable and exposes all adapters', async () => {
    await expect(versionOf('aictiq-command-that-does-not-exist')).resolves.toBeNull()
    expect(Object.keys(harnesses)).toEqual(['claude', 'codex', 'opencode', 'cursor'])
    await expect(probeHarnesses({ PATH: '' } as NodeJS.ProcessEnv)).resolves.toEqual([])
  })
})

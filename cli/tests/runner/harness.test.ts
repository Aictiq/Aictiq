import { readFileSync } from 'node:fs'
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
    const invocation = opencode.invocation(context)
    expect(invocation.args).toEqual([
      'run',
      'Follow the instructions in the attached file.',
      '--format',
      'json',
      '--dir',
      '/work/run',
      '--file',
      '/tmp/aictiq-prompt',
      '/work/attachments',
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
      failureReason: 'harness-killed',
    })
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
    expect(Object.keys(harnesses)).toEqual(['claude', 'codex', 'opencode'])
    await expect(probeHarnesses({ PATH: '' } as NodeJS.ProcessEnv)).resolves.toEqual([])
  })
})

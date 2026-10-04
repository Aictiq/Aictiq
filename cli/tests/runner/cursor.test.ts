import { execFileSync } from 'node:child_process'
import {
  chmodSync,
  existsSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  rmSync,
  writeFileSync,
} from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { cursor, writeMcpConfig } from '../../src/runner/harness/cursor.js'
import type { InvocationContext } from '../../src/runner/types.js'

const context: InvocationContext = {
  prompt: 'Do the work, including a line that must stay out of argv.',
  promptFile: '/tmp/aictiq-prompt',
  workspace: '/work/run',
  attachmentsDir: '/work/attachments',
  mcpConfigFile: '/tmp/aictiq-mcp.json',
  mcpServer: { command: 'aictiq', args: ['mcp', '--stdio'] },
}

const fixture = readFileSync(new URL('../fixtures/harness/cursor.jsonl', import.meta.url), 'utf8')
  .trim()
  .split('\n')

const session = 'd1c7a0e2-5b7f-4c1e-9a3d-6f2b8e4c9a10'

describe('cursor invocation', () => {
  it('runs headless with edits allowed and points at the prompt file instead of passing it', () => {
    const invocation = cursor.invocation(context)
    expect(invocation.command).toBe('agent')
    expect(invocation.args).toEqual([
      '-p',
      '--output-format',
      'stream-json',
      '--force',
      '--trust',
      '--approve-mcps',
      '--workspace',
      '/work/run',
      '--add-dir',
      '/work/attachments',
      'Read /tmp/aictiq-prompt and follow the instructions in it.',
    ])
    expect(invocation.args.join(' ')).not.toContain(context.prompt)
    expect(invocation.stdin).toBeUndefined()
  })

  it('resumes the chat by id', () => {
    const invocation = cursor.invocation({ ...context, resumeSessionId: 'chat-1' })
    expect(invocation.args.slice(-3, -1)).toEqual(['--resume', 'chat-1'])
  })
})

describe('cursor stream-json parser', () => {
  const parsed = fixture.map((line) => cursor.parse(line))

  it('logs messages and tool calls, and drops the prompt echo and thinking', () => {
    expect(parsed.map((line) => line.log).filter((log) => log !== null)).toEqual([
      '[init] model Claude 4.6 Sonnet',
      "I'll read the instructions first.",
      '→ read {"path":"/tmp/aictiq-prompt"}',
      '→ shell {"command":"git status","workingDirectory":""}',
      'Done: the change is pushed.',
      '[result] success in 4210ms',
    ])
  })

  it('never logs the environment snapshot a completed shell call carries', () => {
    expect(JSON.stringify(parsed)).not.toContain('secret-run-token')
  })

  it('reads the session, the token counts and the final result', () => {
    expect(new Set(parsed.map((line) => line.sessionId))).toEqual(new Set([session]))
    expect(parsed.at(-1)).toMatchObject({
      inputTokens: 1200 + 8000 + 500,
      outputTokens: 340,
      result: "I'll read the instructions first.Done: the change is pushed.",
    })
  })

  it('names an MCP call by its server and tool', () => {
    const line = JSON.stringify({
      type: 'tool_call',
      subtype: 'started',
      tool_call: {
        mcpToolCall: {
          args: { providerIdentifier: 'aictiq', toolName: 'get_item', args: { key: 'A-1' } },
        },
      },
    })
    expect(cursor.parse(line).log).toMatch(/^→ aictiq\.get_item /)
  })

  it('keeps malformed output as a log line', () => {
    expect(cursor.parse('not json')).toEqual({ log: 'not json' })
  })
})

describe('cursor outcomes', () => {
  it('succeeds with the final result as summary', () => {
    expect(cursor.outcome(0, ['[result] success in 5ms'], 'all done')).toEqual({
      outcome: 'succeeded',
      failureReason: null,
      summary: 'all done',
    })
  })

  it('tells a missing login, an unknown model and a lost chat apart', () => {
    const auth = [
      "Error: Authentication required. Please run 'agent login' first, or set CURSOR_API_KEY environment variable.",
    ]
    expect(cursor.outcome(1, auth, null).failureReason).toBe('harness-not-authenticated')
    expect(
      cursor.outcome(1, ['⚠ Warning: The provided API key is invalid.'], null).failureReason,
    ).toBe('harness-not-authenticated')
    expect(cursor.outcome(1, ['Model not found: gpt-9'], null).failureReason).toBe(
      'harness-model-unavailable',
    )
    expect(cursor.outcome(1, ['Session chat-1 not found'], null, true).failureReason).toBe(
      'session-unavailable',
    )
    expect(cursor.outcome(1, ['Session chat-1 not found'], null, false).failureReason).toBe(
      'harness-exit-1',
    )
    expect(cursor.outcome(1, ['429 Too Many Requests'], null).failureReason).toBe(
      'harness-rate-limited',
    )
  })

  it('fails an error result despite a zero exit', () => {
    const logged = cursor.parse(
      '{"type":"result","subtype":"error","is_error":true,"duration_ms":5,"result":"nope"}',
    ).log!
    expect(cursor.outcome(0, [logged], 'nope').failureReason).toBe('harness-error')
  })
})

describe('cursor detection', () => {
  let bin: string
  beforeEach(() => {
    bin = mkdtempSync(join(tmpdir(), 'aictiq-cursor-bin-'))
  })
  afterEach(() => rmSync(bin, { recursive: true, force: true }))

  const script = (name: string, version: string) => {
    writeFileSync(join(bin, name), `#!/bin/sh\necho '${version}'\n`)
    chmodSync(join(bin, name), 0o755)
  }

  it.skipIf(process.platform === 'win32')('finds agent and reports its version', async () => {
    script('agent', '2026.10.01-e373342')
    await expect(cursor.available({ PATH: bin })).resolves.toEqual({
      name: 'cursor',
      version: '2026.10.01-e373342',
    })
    expect(cursor.invocation(context).command).toBe('agent')
  })

  it.skipIf(process.platform === 'win32')(
    'ignores another program called agent and falls back to cursor-agent',
    async () => {
      script('agent', 'agent 1.2.3')
      script('cursor-agent', '2025.09.18-7ae6800')
      await expect(cursor.available({ PATH: bin })).resolves.toEqual({
        name: 'cursor',
        version: '2025.09.18-7ae6800',
      })
      expect(cursor.invocation(context).command).toBe('cursor-agent')
    },
  )

  it('is not listed without either executable', async () => {
    await expect(cursor.available({ PATH: bin })).resolves.toBeNull()
  })
})

describe('cursor MCP config in the checkout', () => {
  let repo: string
  const server = { command: '/usr/bin/aictiq', args: ['mcp'] }
  const git = (...args: string[]) =>
    execFileSync('git', args, { cwd: repo, encoding: 'utf8' }).trim()

  beforeEach(() => {
    repo = mkdtempSync(join(tmpdir(), 'aictiq-cursor-repo-'))
    git('init', '-q')
    git('config', 'user.email', 'runner@example.test')
    git('config', 'user.name', 'Runner')
    writeFileSync(join(repo, 'README.md'), 'hello\n')
    git('add', '-A')
    git('commit', '-qm', 'init')
  })
  afterEach(() => rmSync(repo, { recursive: true, force: true }))

  it('adds the server by variable name, keeps it out of commits, and removes it afterwards', async () => {
    const excludeBefore = readFileSync(join(repo, '.git/info/exclude'), 'utf8')
    const restore = await writeMcpConfig(repo, server)

    const config = JSON.parse(readFileSync(join(repo, '.cursor/mcp.json'), 'utf8'))
    expect(config.mcpServers.aictiq).toEqual({
      command: '/usr/bin/aictiq',
      args: ['mcp'],
      env: {
        AICTIQ_URL: '${env:AICTIQ_URL}',
        AICTIQ_TOKEN: '${env:AICTIQ_TOKEN}',
        AICTIQ_ORG: '${env:AICTIQ_ORG}',
        AICTIQ_ITEM: '${env:AICTIQ_ITEM}',
        AICTIQ_RUN: '${env:AICTIQ_RUN}',
      },
    })
    // What an agent would do before committing.
    git('add', '-A')
    expect(git('status', '--porcelain')).toBe('')

    await restore()
    expect(existsSync(join(repo, '.cursor'))).toBe(false)
    expect(readFileSync(join(repo, '.git/info/exclude'), 'utf8')).toBe(excludeBefore)
  })

  it('merges into a tracked config, hides the change from git, and puts the original back', async () => {
    mkdirSync(join(repo, '.cursor'))
    const original = '{ "mcpServers": { "docs": { "url": "https://docs.example" } } }\n'
    writeFileSync(join(repo, '.cursor/mcp.json'), original)
    git('add', '-A')
    git('commit', '-qm', 'cursor config')

    const restore = await writeMcpConfig(repo, server)
    const config = JSON.parse(readFileSync(join(repo, '.cursor/mcp.json'), 'utf8'))
    expect(Object.keys(config.mcpServers)).toEqual(['docs', 'aictiq'])
    git('add', '-A')
    expect(git('status', '--porcelain')).toBe('')

    await restore()
    expect(readFileSync(join(repo, '.cursor/mcp.json'), 'utf8')).toBe(original)
    expect(git('ls-files', '-v', '.cursor/mcp.json')).toBe('H .cursor/mcp.json')
  })
})

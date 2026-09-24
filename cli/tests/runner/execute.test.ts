import { execFileSync } from 'node:child_process'
import { mkdirSync, mkdtempSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { RunnerClient } from '../../src/runner/client.js'
import type { LogChunk } from '../../src/runner/client.js'
import { executeRun } from '../../src/runner/execute.js'
import type { ExecuteOptions } from '../../src/runner/execute.js'
import type { Workspace } from '../../src/runner/workspace.js'
import { claimedRun, hello, runnerToken, scriptAdapter } from './helpers.js'
import { FakeInstance } from './fake-instance.js'

import { agentToken } from './helpers.js'

function stubWorkspace(): Workspace {
  const runDir = mkdtempSync(join(tmpdir(), 'aictiq-ws-'))
  const checkout = join(runDir, 'repo')
  mkdirSync(checkout)
  return {
    runDir,
    checkout,
    branch: 'aictiq/acme-42',
    promptFile: join(runDir, 'prompt.md'),
    mcpConfigFile: join(runDir, 'mcp.json'),
    attachmentsDir: join(runDir, 'attachments'),
    prompt: 'Implement ACME-42',
    env: {},
    cleanup: async () => {},
  }
}

describe('executeRun', () => {
  let instance: FakeInstance
  let options: ExecuteOptions

  beforeEach(async () => {
    instance = await new FakeInstance().start()
    options = {
      client: new RunnerClient({ baseUrl: instance.url, token: runnerToken }),
      hello,
      adapters: {},
      runnerToken,
      workspace: {
        root: mkdtempSync(join(tmpdir(), 'aictiq-root-')),
        repositories: {},
        keep: false,
        mcpServer: { command: 'aictiq', args: ['mcp'] },
      },
      provision: async () => stubWorkspace(),
      findPullRequest: async () => null,
      heartbeatMs: 50,
      graceMs: 300,
      flushIntervalMs: 20,
    }
    instance.on((r) =>
      r.path.endsWith('/heartbeat') && r.path.includes('/runner/runs/')
        ? { status: 200, body: { cancelRequested: false } }
        : undefined,
    )
  })

  afterEach(async () => {
    await instance.stop()
  })

  const logLines = () =>
    instance
      .to('/log')
      .flatMap((r) => (r.body as { chunks: LogChunk[] }).chunks)
      .sort((a, b) => a.seq - b.seq)

  it('passes the harness none of the runner’s own AICTIQ_ variables, only the run’s', async () => {
    process.env.AICTIQ_RUNNER_TOKEN = 'jrn_other_org_secret_value'
    process.env.AICTIQ_CONFIG_HOME = '/somewhere/else'
    try {
      options.adapters.fake = scriptAdapter(`
        console.log('inherited ' + Object.keys(process.env).filter((k) => k.startsWith('AICTIQ_')).sort().join(','))
        console.log('RESULT ok')
      `)
      await executeRun(claimedRun(), options)
    } finally {
      delete process.env.AICTIQ_RUNNER_TOKEN
      delete process.env.AICTIQ_CONFIG_HOME
    }
    expect(logLines().map((c) => c.text)).toContain('inherited AICTIQ_ITEM,AICTIQ_ORG,AICTIQ_RUN,AICTIQ_TOKEN,AICTIQ_URL')
  })

  it('runs the harness in the workspace, streams its log and reports success with the pull request', async () => {
    options.adapters.fake = scriptAdapter(`
      let prompt = ''
      process.stdin.on('data', (d) => (prompt += d)).on('end', () => {
        console.log('prompt: ' + prompt)
        console.log('item ' + process.env.AICTIQ_ITEM + ' as ' + process.env.AICTIQ_TOKEN)
        console.log('Opened https://github.com/acme/app/pull/17')
        console.log('RESULT All done')
      })
    `)
    const report = await executeRun(claimedRun(), options)

    expect(report).toMatchObject({
      outcome: 'succeeded',
      exitCode: 0,
      summary: 'All done',
      pullRequestUrl: 'https://github.com/acme/app/pull/17',
      costUsd: 0.5,
      inputTokens: 10,
      outputTokens: 3,
    })
    const paths = instance.requests.map((r) => r.path)
    expect(paths[0]).toBe(`/api/v1/runner/runs/${claimedRun().runId}/started`)
    expect(paths.at(-1)).toBe(`/api/v1/runner/runs/${claimedRun().runId}/finish`)
    expect(instance.to('/finish')[0]!.body).toMatchObject({
      outcome: 'succeeded',
      pullRequestUrl: 'https://github.com/acme/app/pull/17',
    })

    const lines = logLines()
    expect(lines.map((c) => c.seq)).toEqual(lines.map((_c, i) => i))
    expect(lines.filter((c) => c.stream === 'stdout').map((c) => c.text)).toEqual([
      'prompt: Implement ACME-42',
      'item ACME-42 as [redacted]',
      'Opened https://github.com/acme/app/pull/17',
    ])
    expect(
      lines.some((c) => c.stream === 'event' && c.text.includes('Runner picked up ACME-42')),
    ).toBe(true)
    expect(JSON.stringify(instance.requests.map((r) => r.body))).not.toContain(agentToken)

    // The item's claim is refreshed with the agent's credential, not the runner's.
    const itemBeat = instance.to('/items/ACME-42/heartbeat')[0]
    expect(itemBeat?.path).toBe('/api/v1/orgs/acme/items/ACME-42/heartbeat')
    expect(itemBeat?.authorization).toBe(`Bearer ${agentToken}`)
    expect(instance.to('/runs/' + claimedRun().runId + '/heartbeat')[0]?.authorization).toBe(
      `Bearer ${runnerToken}`,
    )
  })

  it('fails with harness-unavailable at once when the harness is not installed', async () => {
    options.adapters.fake = scriptAdapter('', false)
    const started = Date.now()
    const report = await executeRun(claimedRun(), options)
    expect(report).toMatchObject({ outcome: 'failed', failureReason: 'harness-unavailable' })
    expect(Date.now() - started).toBeLessThan(3000)
  })

  it('reports a non-zero exit as failed with the last lines', async () => {
    options.adapters.fake = scriptAdapter(
      `console.log('compiling'); console.error('boom'); process.exit(3)`,
    )
    const report = await executeRun(claimedRun(), options)
    expect(report).toMatchObject({
      outcome: 'failed',
      exitCode: 3,
      failureReason: 'harness-exit-3',
    })
    expect(report?.summary).toContain('boom')
  })

  it('adds up usage a harness reports per step', async () => {
    options.adapters.fake = scriptAdapter(`console.log('STEP'); console.log('STEP')`)
    const report = await executeRun(claimedRun(), options)
    expect(report).toMatchObject({ costUsd: 0.5, inputTokens: 10, outputTokens: 4 })
  })

  it('strips terminal colours from log lines and the summary', async () => {
    options.adapters.fake = scriptAdapter(
      `console.error('\\x1b[91m\\x1b[1mError: \\x1b[0mFile not found'); process.exit(1)`,
    )
    const report = await executeRun(claimedRun(), options)
    expect(report?.summary).toBe('Error: File not found')
    expect(logLines().find((c) => c.stream === 'stderr')?.text).toBe('Error: File not found')
  })

  it('stops the harness and reports cancelled when the heartbeat says a cancel was requested', async () => {
    let beats = 0
    instance.on((r) =>
      r.path.includes('/runner/runs/') && r.path.endsWith('/heartbeat')
        ? { status: 200, body: { cancelRequested: ++beats >= 3 } }
        : undefined,
    )
    // A handler registered later loses to the default one, so drop the default.
    ;(instance as unknown as { handlers: unknown[] }).handlers.shift()
    options.adapters.fake = scriptAdapter(
      `process.on('SIGTERM', () => {}); console.log('working'); setInterval(() => {}, 1000)`,
    )

    const report = await executeRun(claimedRun(), options)
    expect(report).toMatchObject({ outcome: 'cancelled' })
    expect(logLines().some((c) => c.text.includes('Cancel requested'))).toBe(true)
  })

  it('stops the harness at maxMinutes and reports a timed-out failure', async () => {
    options.adapters.fake = scriptAdapter(`setInterval(() => {}, 1000)`)
    const report = await executeRun(claimedRun({ maxMinutes: 0.005 }), options)
    expect(report).toMatchObject({ outcome: 'failed', failureReason: 'timed-out' })
  })

  it('reports the shutdown as a cancellation', async () => {
    options.adapters.fake = scriptAdapter(`setInterval(() => {}, 1000)`)
    const shutdown = new AbortController()
    options.shutdown = shutdown.signal
    setTimeout(() => shutdown.abort(), 200)
    expect(await executeRun(claimedRun(), options)).toMatchObject({
      outcome: 'cancelled',
      failureReason: 'runner-shutdown',
    })
  })

  it('stops the harness at once and rethrows when the runner secret is revoked mid-run', async () => {
    instance.on((r) =>
      r.path.includes('/runner/runs/') && r.path.endsWith('/heartbeat')
        ? { status: 401, body: { type: 'https://aictiq.com/problems/token-revoked' } }
        : undefined,
    )
    ;(instance as unknown as { handlers: unknown[] }).handlers.shift()
    options.adapters.fake = scriptAdapter(`console.log('working'); setInterval(() => {}, 1000)`)
    const started = Date.now()
    await expect(executeRun(claimedRun(), options)).rejects.toMatchObject({ status: 401 })
    expect(Date.now() - started).toBeLessThan(5000)
    expect(instance.to('/finish')).toHaveLength(0)
  })

  it('does not report a run the instance already closed', async () => {
    instance.on((r) =>
      r.path.endsWith('/started') ? { status: 409, body: { title: 'Conflict.' } } : undefined,
    )
    ;(instance as unknown as { handlers: unknown[] }).handlers.reverse()
    options.adapters.fake = scriptAdapter(`console.log('never')`)
    expect(await executeRun(claimedRun(), options)).toBeNull()
    expect(instance.to('/finish')).toHaveLength(0)
  })

  it('fails with no-local-repository when the project has no mapped clone', async () => {
    delete options.provision
    options.adapters.fake = scriptAdapter(`console.log('never')`)
    const report = await executeRun(claimedRun(), options)
    expect(report).toMatchObject({ outcome: 'failed', failureReason: 'no-local-repository' })
  })

  it('provisions a real worktree from a mapped local repository', async () => {
    const env = { ...process.env, GIT_CONFIG_GLOBAL: '/dev/null', GIT_CONFIG_NOSYSTEM: '1' }
    const git = (cwd: string, ...args: string[]) =>
      execFileSync('git', args, { cwd, env, stdio: 'pipe' }).toString()
    const base = mkdtempSync(join(tmpdir(), 'aictiq-repo-'))
    const origin = join(base, 'origin.git')
    const clone = join(base, 'clone')
    git(base, 'init', '--bare', '-b', 'main', origin)
    git(base, 'clone', origin, clone)
    writeFileSync(join(clone, 'README.md'), 'hi\n')
    git(clone, 'add', '.')
    git(clone, '-c', 'user.email=t@example.com', '-c', 'user.name=t', 'commit', '-m', 'init')
    git(clone, 'push', 'origin', 'main')

    delete options.provision
    options.workspace.repositories = { ACME: clone }
    options.workspace.env = env
    options.adapters.fake = scriptAdapter(`
      import { execFileSync } from 'node:child_process'
      console.log('branch ' + execFileSync('git', ['rev-parse', '--abbrev-ref', 'HEAD']).toString().trim())
    `)
    const report = await executeRun(claimedRun(), options)
    expect(report).toMatchObject({ outcome: 'succeeded' })
    expect(logLines().map((c) => c.text)).toContain('branch aictiq/acme-42')
    // Cleaned up: the worktree is gone, the branch stays for the pull request.
    expect(git(clone, 'worktree', 'list').trim().split('\n')).toHaveLength(1)
    expect(git(clone, 'branch', '--list', 'aictiq/acme-42')).toContain('aictiq/acme-42')
  })
})

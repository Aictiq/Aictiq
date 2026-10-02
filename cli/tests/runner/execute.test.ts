import { execFileSync } from 'node:child_process'
import { mkdirSync, mkdtempSync, readFileSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { RunnerClient } from '../../src/runner/client.js'
import type { LogChunk } from '../../src/runner/client.js'
import { executeRun } from '../../src/runner/execute.js'
import type { ExecuteOptions } from '../../src/runner/execute.js'
import type { Workspace } from '../../src/runner/workspace.js'
import { RunFailure } from '../../src/runner/types.js'
import type { HarnessAdapter, InvocationContext } from '../../src/runner/types.js'
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
    retain: () => {},
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

  it('finishes a direct run without discovering or linking an incidental pull request', async () => {
    let lookedUp = false
    options.findPullRequest = async () => {
      lookedUp = true
      return 'https://github.com/acme/app/pull/99'
    }
    options.adapters.fake = scriptAdapter(`
      console.log('Related https://github.com/acme/app/pull/17')
      console.log('RESULT Pushed to main')
    `)
    const report = await executeRun(
      claimedRun({ workOnDefaultBranch: true, branchName: 'main' }),
      options,
    )
    expect(report).toMatchObject({
      outcome: 'succeeded',
      pullRequestUrl: null,
      summary: 'Pushed to main',
    })
    expect(lookedUp).toBe(false)
    expect(instance.to('/finish')[0]!.body).toMatchObject({
      outcome: 'succeeded',
      pullRequestUrl: null,
    })
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

  describe('continuing failed runs', () => {
    /** Reports `SESSION <id>` lines as the harness session, and records how it was started. */
    const sessionAdapter = (script: string, contexts: InvocationContext[]): HarnessAdapter => {
      const base = scriptAdapter(script)
      return {
        ...base,
        invocation: (context) => {
          contexts.push(context)
          return base.invocation(context)
        },
        parse: (line) =>
          line.startsWith('SESSION ') ? { log: null, sessionId: line.slice(8) } : base.parse(line),
      }
    }
    const tracked = () => {
      const calls = { retained: [] as string[], cleaned: 0, pruned: [] as unknown[] }
      const workspace = stubWorkspace()
      workspace.retain = (id) => calls.retained.push(id)
      workspace.cleanup = async () => {
        calls.cleaned++
      }
      options.provision = async () => workspace
      options.prune = async (_root, filter) => {
        calls.pruned.push(filter?.item ?? null)
        return []
      }
      return calls
    }

    it('reports the session and keeps the workspace of a run that failed after the harness started', async () => {
      const calls = tracked()
      options.adapters.fake = sessionAdapter(`console.log('SESSION sess-1'); process.exit(3)`, [])
      const report = await executeRun(claimedRun(), options)

      expect(report).toMatchObject({ outcome: 'failed', sessionId: 'sess-1', workspacePath: expect.stringMatching(/aictiq-ws-.*\/repo$/) })
      expect(instance.to('/finish')[0]!.body).toMatchObject({ sessionId: 'sess-1', workspacePath: expect.stringMatching(/aictiq-ws-.*\/repo$/) })
      expect(calls).toMatchObject({ retained: ['sess-1'], cleaned: 0 })
    })

    it('keeps the workspace of a successful run with a session, and leaves the item’s other kept workspaces', async () => {
      const calls = tracked()
      options.adapters.fake = sessionAdapter(`console.log('SESSION sess-1'); console.log('RESULT ok')`, [])
      const report = await executeRun(claimedRun(), options)

      expect(report).toMatchObject({ outcome: 'succeeded', sessionId: 'sess-1', workspacePath: expect.stringMatching(/aictiq-ws-.*\/repo$/) })
      expect(calls).toMatchObject({ retained: ['sess-1'], cleaned: 0 })
      // The item's worktree is released before provisioning; after the run only expiry sweeps.
      expect(calls.pruned).toEqual([{ organizationSlug: 'acme', itemKey: 'ACME-42' }, null])
    })

    it('sends the checkout with the session on the run heartbeat', async () => {
      tracked()
      options.adapters.fake = sessionAdapter(
        `console.log('SESSION sess-1'); setTimeout(() => console.log('RESULT ok'), 300)`,
        [],
      )
      await executeRun(claimedRun(), options)
      const beats = instance.to('/runner/runs/' + claimedRun().runId + '/heartbeat')
      expect(beats.map((beat) => beat.body)).toContainEqual({ sessionId: 'sess-1', workspacePath: expect.stringMatching(/aictiq-ws-.*\/repo$/) })
    })

    it('cleans up a failed run without a session: there is nothing to continue', async () => {
      const calls = tracked()
      options.adapters.fake = sessionAdapter(`process.exit(3)`, [])
      await executeRun(claimedRun(), options)
      expect(calls).toMatchObject({ retained: [], cleaned: 1 })
    })

    it('resumes the session in the kept workspace with the continue message', async () => {
      const calls = tracked()
      const contexts: InvocationContext[] = []
      let reattached: string | undefined
      options.provision = async () => {
        throw new Error('a continue run must not provision a fresh workspace')
      }
      options.reattach = async (run, prompt) => {
        reattached = run.resume?.continuesRunId
        return {
          ...stubWorkspace(),
          prompt,
          retain: (id) => calls.retained.push(id),
          cleanup: async () => { calls.cleaned++ },
        }
      }
      options.adapters.fake = sessionAdapter(
        `let p = ''; process.stdin.on('data', (d) => (p += d)).on('end', () => { console.log('got ' + p); console.log('RESULT ok') })`,
        contexts,
      )
      const report = await executeRun(
        claimedRun({ resume: { continuesRunId: 'run-0', sessionId: 'sess-0', failureReason: 'harness-transient' } }),
        options,
      )

      expect(report?.outcome).toBe('succeeded')
      expect(reattached).toBe('run-0')
      expect(contexts[0]).toMatchObject({ resumeSessionId: 'sess-0' })
      expect(contexts[0]!.prompt).toContain('Continue where you stopped')
      expect(contexts[0]!.prompt).toContain('harness-transient')
      expect(logLines().map((c) => c.text)).toContain('Continuing run run-0 (session sess-0)')
      // Kept again, so the finished session can still be resumed by hand.
      expect(calls).toMatchObject({ retained: ['sess-0'], cleaned: 0 })
    })

    it('keeps the workspace of a resumed run that fails before the harness names its session', async () => {
      const retained: string[] = []
      options.reattach = async (_run, prompt) => ({
        ...stubWorkspace(),
        prompt,
        retain: (id) => retained.push(id),
      })
      options.prune = async () => []
      options.adapters.fake = sessionAdapter(`process.exit(3)`, [])
      const report = await executeRun(
        claimedRun({ resume: { continuesRunId: 'run-0', sessionId: 'sess-0', failureReason: null } }),
        options,
      )
      expect(report).toMatchObject({ outcome: 'failed', sessionId: 'sess-0' })
      expect(retained).toEqual(['sess-0'])
    })

    it('fails with session-unavailable when the kept workspace is gone', async () => {
      tracked()
      options.reattach = async () => {
        throw new RunFailure('session-unavailable', 'This runner no longer has the workspace.')
      }
      options.adapters.fake = sessionAdapter(`console.log('RESULT ok')`, [])
      const report = await executeRun(
        claimedRun({ resume: { continuesRunId: 'run-0', sessionId: 'sess-0', failureReason: null } }),
        options,
      )
      expect(report).toMatchObject({ outcome: 'failed', failureReason: 'session-unavailable' })
    })

    describe('follow-ups', () => {
      const env = {
        ...process.env,
        GIT_CONFIG_GLOBAL: '/dev/null',
        GIT_CONFIG_NOSYSTEM: '1',
        GIT_AUTHOR_NAME: 't',
        GIT_AUTHOR_EMAIL: 't@example.com',
        GIT_COMMITTER_NAME: 't',
        GIT_COMMITTER_EMAIL: 't@example.com',
      }
      const git = (cwd: string, ...args: string[]) =>
        execFileSync('git', args, { cwd, env, stdio: 'pipe' }).toString()
      const branch = 'aictiq/acme-42'
      const pullRequest = 'https://github.com/acme/app/pull/7'

      /** An origin with the earlier run's branch pushed, and a local clone holding only main. */
      const repository = () => {
        const base = mkdtempSync(join(tmpdir(), 'aictiq-repo-'))
        const origin = join(base, 'origin.git')
        const clone = join(base, 'clone')
        git(base, 'init', '--bare', '-b', 'main', origin)
        git(base, 'clone', origin, clone)
        writeFileSync(join(clone, 'README.md'), 'hi\n')
        git(clone, 'add', '.')
        git(clone, 'commit', '-m', 'init')
        git(clone, 'push', 'origin', 'main')
        git(clone, 'checkout', '-b', branch)
        writeFileSync(join(clone, 'fix.txt'), 'first attempt\n')
        git(clone, 'add', '.')
        git(clone, 'commit', '-m', 'first attempt')
        git(clone, 'push', 'origin', branch)
        git(clone, 'checkout', 'main')
        git(clone, 'branch', '-D', branch)
        options.workspace.repositories = { ACME: clone }
        options.workspace.env = env
        return { base, origin, clone }
      }
      const followUpRun = (sessionId: string | null = 'sess-1') =>
        claimedRun({
          followUp: {
            previousRunId: 'run-1',
            sessionId,
            previousBranchName: branch,
            pullRequestUrl: pullRequest,
            newBranchName: 'aictiq/acme-42-follow-up',
            instruction: 'Rename the flag.',
            requestedByName: 'Ana',
            commentId: 'comment-1',
          },
        })
      const printsCheckout = `
        import { execFileSync } from 'node:child_process'
        const head = (args) => execFileSync('git', args).toString().trim()
        console.log('on ' + head(['branch', '--show-current']) + ' at ' + head(['rev-parse', 'HEAD']))
        console.log('RESULT done')
      `
      let lookedUp: string[]
      beforeEach(() => {
        lookedUp = []
        options.findPullRequest = async (_checkout, branch) => {
          lookedUp.push(branch)
          return null
        }
        options.prune = async () => []
      })

      it('resumes the earlier session on its branch while the pull request is open', async () => {
        const { base, origin } = repository()
        const runDir = join(base, 'kept')
        git(base, 'clone', origin, join(runDir, 'repo'))
        git(join(runDir, 'repo'), 'checkout', branch)
        const retained: string[] = []
        let reattachedFor: string | undefined
        options.reattach = async (run, prompt) => {
          reattachedFor = run.resume?.continuesRunId
          writeFileSync(join(runDir, 'prompt.md'), prompt)
          return {
            ...stubWorkspace(),
            runDir,
            checkout: join(runDir, 'repo'),
            promptFile: join(runDir, 'prompt.md'),
            prompt,
            retain: (id) => retained.push(id),
          }
        }
        options.pullRequestState = async () => 'open'
        const contexts: InvocationContext[] = []
        options.adapters.fake = sessionAdapter(printsCheckout, contexts)

        const report = await executeRun(followUpRun(), options)
        expect(report).toMatchObject({ outcome: 'succeeded', sessionId: 'sess-1' })
        expect(report).not.toHaveProperty('branchName')
        expect(reattachedFor).toBe('run-1')
        expect(contexts[0]).toMatchObject({ resumeSessionId: 'sess-1' })
        expect(contexts[0]!.prompt).toContain('Ana asked for a follow-up')
        expect(contexts[0]!.prompt).toContain('> Rename the flag.')
        expect(contexts[0]!.prompt).toContain(`Keep working on branch \`${branch}\``)
        expect(readFileSync(join(runDir, 'prompt.md'), 'utf8')).toBe(contexts[0]!.prompt)
        expect(logLines().map((c) => c.text)).toContain(
          `Pull request ${pullRequest} is open; continuing on branch ${branch}`,
        )
        expect(lookedUp).toEqual([branch])
        expect(retained).toEqual(['sess-1'])
      })

      it('starts a fresh session on the pushed branch when the workspace is gone', async () => {
        const { origin } = repository()
        delete options.provision
        options.reattach = async () => {
          throw new RunFailure('session-unavailable', 'This runner no longer has the workspace.')
        }
        const pruned: unknown[] = []
        options.prune = async (_root, filter) => {
          pruned.push(filter?.item ?? null)
          return []
        }
        options.pullRequestState = async () => 'open'
        const contexts: InvocationContext[] = []
        options.adapters.fake = sessionAdapter(printsCheckout, contexts)

        const report = await executeRun(followUpRun(), options)
        expect(report).toMatchObject({ outcome: 'succeeded' })
        expect(report).not.toHaveProperty('sessionId')
        expect(contexts[0]!.resumeSessionId).toBeUndefined()
        expect(contexts[0]!.prompt.startsWith('Implement ACME-42')).toBe(true)
        expect(contexts[0]!.prompt).toContain('## Follow-up branch')
        const tip = git(origin, 'rev-parse', branch).trim()
        const lines = logLines().map((c) => c.text)
        expect(lines).toContain(`on ${branch} at ${tip}`)
        expect(lines).toContain(
          'This runner no longer has the workspace of run run-1; starting a fresh session',
        )
        expect(pruned[0]).toEqual({ organizationSlug: 'acme', itemKey: 'ACME-42' })
      })

      it('starts a new branch from main once the pull request was merged', async () => {
        const { origin, clone } = repository()
        delete options.provision
        options.pullRequestState = async () => 'merged'
        const contexts: InvocationContext[] = []
        options.adapters.fake = sessionAdapter(printsCheckout, contexts)

        const report = await executeRun(followUpRun(null), options)
        expect(report).toMatchObject({
          outcome: 'succeeded',
          branchName: 'aictiq/acme-42-follow-up',
        })
        expect(instance.to('/finish')[0]!.body).toMatchObject({
          branchName: 'aictiq/acme-42-follow-up',
        })
        expect(contexts[0]!.prompt).toContain('not the branch named above')
        const main = git(origin, 'rev-parse', 'main').trim()
        expect(logLines().map((c) => c.text)).toContain(`on aictiq/acme-42-follow-up at ${main}`)
        expect(lookedUp).toEqual(['aictiq/acme-42-follow-up'])
        // The empty branch provisioning made for the old name is gone again.
        expect(git(clone, 'branch', '--list', branch).trim()).toBe('')
      })

      it('fails with follow-up-target-missing without starting the harness', async () => {
        repository()
        delete options.provision
        options.pullRequestState = async () => 'missing'
        const contexts: InvocationContext[] = []
        options.adapters.fake = sessionAdapter(printsCheckout, contexts)

        const report = await executeRun(followUpRun(null), options)
        const summary = `I didn't start: the pull request ${pullRequest} from run run-1 can no longer be found.`
        expect(report).toMatchObject({
          outcome: 'failed',
          failureReason: 'follow-up-target-missing',
          summary,
        })
        expect(instance.to('/finish')[0]!.body).toMatchObject({ summary })
        expect(contexts).toHaveLength(0)
      })
    })
  })
})

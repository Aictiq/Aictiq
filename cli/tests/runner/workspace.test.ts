import { execFileSync } from 'node:child_process'
import {
  existsSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  rmSync,
  statSync,
  symlinkSync,
  writeFileSync,
} from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import {
  defaultWorkspaceRoot,
  locateLocalRepositories,
  provisionWorkspace,
  pruneKeptWorkspaces,
  reattachWorkspace,
  type WorkspaceOptions,
} from '../../src/runner/workspace.js'
import { RunFailure, type ClaimedRun, type RunRepoEntry } from '../../src/runner/types.js'
import { makeZip } from './zip-fixture.js'

const zip = makeZip([
  { name: 'docs/codename.txt', data: 'BLUEBIRD' },
  { name: '../escape.txt', data: 'x' },
])

// Isolated from the developer's own git configuration (signing, hooks, default branch).
const gitEnv: NodeJS.ProcessEnv = {
  ...process.env,
  GIT_CONFIG_GLOBAL: '/dev/null',
  GIT_CONFIG_NOSYSTEM: '1',
  GIT_AUTHOR_NAME: 'Test',
  GIT_AUTHOR_EMAIL: 'test@example.com',
  GIT_COMMITTER_NAME: 'Test',
  GIT_COMMITTER_EMAIL: 'test@example.com',
}

const git = (cwd: string, ...args: string[]) =>
  execFileSync('git', args, {
    cwd,
    env: gitEnv,
    encoding: 'utf8',
    stdio: ['ignore', 'pipe', 'pipe'],
  })

let scratch: string
let origin: string
let local: string
let root: string
let events: string[]

beforeEach(() => {
  scratch = mkdtempSync(join(tmpdir(), 'aictiq-workspace-'))
  origin = join(scratch, 'owner', 'app.git')
  git(scratch, 'init', '--bare', '-b', 'main', origin)
  const seed = join(scratch, 'seed')
  git(scratch, 'clone', origin, seed)
  writeFileSync(join(seed, 'README.md'), '# app\n')
  git(seed, 'add', '.')
  git(seed, 'commit', '-m', 'initial')
  git(seed, 'push', 'origin', 'HEAD:main')

  local = join(scratch, 'local')
  git(scratch, 'clone', origin, local)
  // origin moves on after the local clone: the worktree must start from the fetched tip.
  writeFileSync(join(seed, 'CHANGELOG.md'), 'later\n')
  git(seed, 'add', '.')
  git(seed, 'commit', '-m', 'later')
  git(seed, 'push', 'origin', 'HEAD:main')

  root = join(scratch, 'runs')
  events = []
})

afterEach(() => {
  rmSync(scratch, { recursive: true, force: true })
})

const claimed = (
  overrides: Partial<ClaimedRun> = {},
  repo: Partial<ClaimedRun['repo']> = {},
): ClaimedRun => ({
  runId: '0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b',
  itemId: 'item-1',
  itemKey: 'APP-12',
  projectId: 'project-1',
  projectKey: 'APP',
  organizationSlug: 'acme',
  harness: 'claude',
  prompt: 'Fix the bug in APP-12.',
  playbookRevisionId: null,
  repo: { source: 'local', repoFullName: null, cloneToken: null, localPathHint: null, ...repo },
  defaultBranch: 'main',
  branchName: 'aictiq/app-12-fix-the-bug',
  maxMinutes: 60,
  aictiqUrl: 'https://aictiq.example.com',
  agentToken: 'aiq_agent_secret',
  agentTokenDisplay: null,
  heartbeatIntervalSeconds: 60,
  ...overrides,
})

const options = (overrides: Partial<WorkspaceOptions> = {}): WorkspaceOptions => ({
  root,
  repositories: { APP: local },
  keep: false,
  mcpServer: { command: 'aictiq', args: ['mcp'] },
  event: (line) => events.push(line),
  env: gitEnv,
  ...overrides,
})

const failure = async (promise: Promise<unknown>): Promise<RunFailure> => {
  try {
    await promise
  } catch (error) {
    if (error instanceof RunFailure) return error
    throw error
  }
  throw new Error('expected a RunFailure')
}

describe('defaultWorkspaceRoot', () => {
  it('prefers XDG_DATA_HOME', () => {
    expect(defaultWorkspaceRoot({ XDG_DATA_HOME: '/data' })).toBe('/data/aictiq/runner')
    expect(defaultWorkspaceRoot({})).toMatch(/\.local\/share\/aictiq\/runner$/)
  })
})

describe('local workspaces', () => {
  it('pushes directly from an isolated clone while main is checked out and dirty locally', async () => {
    const originalHead = git(local, 'rev-parse', 'HEAD').trim()
    writeFileSync(join(local, 'README.md'), 'developer work in progress\n')
    const ws = await provisionWorkspace(
      claimed({ workOnDefaultBranch: true, branchName: 'main' }),
      options(),
    )
    expect(git(ws.checkout, 'branch', '--show-current').trim()).toBe('main')
    expect(git(ws.checkout, 'rev-parse', 'HEAD').trim()).toBe(
      git(origin, 'rev-parse', 'main').trim(),
    )
    expect(git(ws.checkout, 'remote', 'get-url', 'origin').trim()).toBe(origin)
    writeFileSync(join(ws.checkout, 'fix.txt'), 'fixed\n')
    git(ws.checkout, 'add', '.')
    git(ws.checkout, 'commit', '-m', 'APP-12 fix')
    git(ws.checkout, 'push')
    expect(git(origin, 'log', '-1', '--format=%s').trim()).toBe('APP-12 fix')
    expect(git(local, 'rev-parse', 'HEAD').trim()).toBe(originalHead)
    expect(readFileSync(join(local, 'README.md'), 'utf8')).toBe('developer work in progress\n')
    expect(git(local, 'branch', '--show-current').trim()).toBe('main')
    await ws.cleanup()
    expect(existsSync(ws.runDir)).toBe(false)
    expect(git(local, 'worktree', 'list').trim().split('\n')).toHaveLength(1)
  })

  it('uses a dedicated default branch and rejects a push if another run advances it', async () => {
    git(local, 'checkout', '-b', 'develop')
    git(local, 'push', 'origin', 'develop')
    const ws = await provisionWorkspace(
      claimed({ workOnDefaultBranch: true, defaultBranch: 'develop', branchName: 'develop' }),
      options(),
    )
    expect(git(ws.checkout, 'branch', '--show-current').trim()).toBe('develop')
    writeFileSync(join(ws.checkout, 'fix.txt'), 'agent fix\n')
    git(ws.checkout, 'add', '.')
    git(ws.checkout, 'commit', '-m', 'APP-12 fix')
    writeFileSync(join(local, 'other.txt'), 'other work\n')
    git(local, 'add', '.')
    git(local, 'commit', '-m', 'other work')
    git(local, 'push', 'origin', 'develop')
    expect(() => git(ws.checkout, 'push')).toThrow()
    expect(git(origin, 'log', '-1', '--format=%s', 'develop').trim()).toBe('other work')
    await ws.cleanup()
  })

  it('fails with no-remote before cloning when a direct run has no origin to push to', async () => {
    git(local, 'remote', 'remove', 'origin')
    const error = await failure(
      provisionWorkspace(
        claimed({ workOnDefaultBranch: true, branchName: 'main' }),
        options(),
      ),
    )

    expect(error.reason).toBe('no-remote')
    expect(error.message).toContain('no origin remote')
    expect(existsSync(join(root, claimed().runId))).toBe(false)
  })

  it('still works on an item branch in a repository without origin', async () => {
    git(local, 'remote', 'remove', 'origin')
    const ws = await provisionWorkspace(claimed(), options())
    expect(git(ws.checkout, 'branch', '--show-current').trim()).toBe('aictiq/app-12-fix-the-bug')
    await ws.cleanup()
  })

  it('provisions item and comment attachments beside the checkout and inventories them in the prompt', async () => {
    const downloads: string[] = []
    const ws = await provisionWorkspace(claimed(), options({
      keep: true,
      attachments: {
        list: async () => [
          { id: 'a1', fileName: 'screenshot.png', contentType: 'image/webp', sizeBytes: 3 },
          { id: 'a2', fileName: 'screenshot.png', contentType: 'text/plain', sizeBytes: 5, commentId: 'c1' },
        ],
        download: async (id) => {
          downloads.push(id)
          return new TextEncoder().encode(id === 'a1' ? 'png' : 'notes')
        },
      },
    }))

    expect(downloads).toEqual(['a1', 'a2'])
    expect(ws.attachmentsDir).toBe(join(root, claimed().runId, 'attachments'))
    expect(readFileSync(join(ws.attachmentsDir, 'screenshot.png'), 'utf8')).toBe('png')
    expect(readFileSync(join(ws.attachmentsDir, 'screenshot-2.png'), 'utf8')).toBe('notes')
    expect(statSync(ws.attachmentsDir).mode & 0o777).toBe(0o700)
    expect(statSync(join(ws.attachmentsDir, 'screenshot.png')).mode & 0o777).toBe(0o600)
    expect(ws.prompt).toContain('## Attachments')
    expect(ws.prompt).toContain('Look at these images before planning.')
    expect(ws.prompt).toContain('/api/v1/orgs/acme/attachments/a1/download')
    expect(ws.prompt).toContain('from comment c1')
    expect(events).toContain('Provisioned 2 attachments: screenshot.png, screenshot-2.png')
    expect(ws.attachmentsDir.startsWith(ws.checkout)).toBe(false)
  })

  it('provisions a chat run\'s files, extracts its ZIPs and names what it could not fetch', async () => {
    const ws = await provisionWorkspace(
      claimed({ itemId: null, itemKey: null, workOnDefaultBranch: true, branchName: 'main' }),
      options({
        attachments: {
          list: async () => [
            { id: 'z1', fileName: 'spec.zip', contentType: 'application/zip', sizeBytes: zip.byteLength, chatMessageId: 'm1' },
            { id: 'p1', fileName: 'brief.pdf', contentType: 'application/pdf', sizeBytes: 4, chatMessageId: 'm1' },
            { id: 'gone', fileName: 'gone.png', contentType: 'image/webp', sizeBytes: 4, chatMessageId: 'm1' },
          ],
          download: async (id) => {
            if (id === 'gone') throw new Error('blob missing')
            return id === 'z1' ? zip : new TextEncoder().encode('%PDF')
          },
        },
      }),
    )

    expect(readFileSync(join(ws.attachmentsDir, 'spec.zip.contents', 'docs', 'codename.txt'), 'utf8')).toBe('BLUEBIRD')
    expect(existsSync(join(ws.attachmentsDir, 'brief.pdf'))).toBe(true)
    expect(ws.prompt).toContain('from chat question m1')
    expect(ws.prompt).toContain(`extracted: ${join(ws.attachmentsDir, 'spec.zip.contents', 'docs', 'codename.txt')}`)
    expect(ws.prompt).toContain('not extracted: ../escape.txt (unsafe path)')
    expect(ws.prompt).toContain('- gone.png (download failed: blob missing)')
    expect(events).toContain('Extracted 1 file from "spec.zip", skipped 1')
    await ws.cleanup()
  })

  it('tells a chat run when its files could not be listed at all', async () => {
    const ws = await provisionWorkspace(
      claimed({ itemId: null, itemKey: null, workOnDefaultBranch: true, branchName: 'main' }),
      options({
        attachments: {
          list: async () => { throw new Error('404') },
          download: async () => new Uint8Array(),
        },
      }),
    )

    expect(ws.prompt).toContain('could not fetch the chat\'s files (404)')
    await ws.cleanup()
  })

  it('skips over-limit and failed attachments without failing provisioning', async () => {
    const ws = await provisionWorkspace(claimed(), options({
      attachments: {
        list: async () => [
          { id: 'large', fileName: 'large.png', contentType: 'image/png', sizeBytes: 10 },
          { id: 'broken', fileName: 'broken.txt', contentType: 'text/plain', sizeBytes: 2 },
          { id: 'okay', fileName: '../okay.txt', contentType: 'text/plain', sizeBytes: 2 },
        ],
        download: async (id) => {
          if (id === 'broken') throw new Error('blob missing')
          return new TextEncoder().encode('ok')
        },
      },
      attachmentMaxBytes: 5,
    }))

    expect(existsSync(join(ws.attachmentsDir, '.._okay.txt'))).toBe(true)
    expect(events).toContain('Skipped attachment "large.png" (would exceed 5 bytes total)')
    expect(events).toContain('Failed to download attachment "broken.txt": blob missing')
    await ws.cleanup()
  })

  it('adds a worktree on a new branch from the fetched default branch', async () => {
    const ws = await provisionWorkspace(claimed(), options())

    expect(ws.checkout).toBe(join(root, claimed().runId, 'repo'))
    expect(git(ws.checkout, 'rev-parse', '--abbrev-ref', 'HEAD').trim()).toBe(
      'aictiq/app-12-fix-the-bug',
    )
    expect(git(ws.checkout, 'rev-parse', 'HEAD').trim()).toBe(
      git(origin, 'rev-parse', 'main').trim(),
    )
    expect(existsSync(join(ws.checkout, 'CHANGELOG.md'))).toBe(true)
    // No upstream: a bare `git push` from the agent must not land on main.
    expect(() => git(ws.checkout, 'rev-parse', '--abbrev-ref', '@{upstream}')).toThrow()
    expect(ws.env).toEqual({})

    expect(statSync(ws.runDir).mode & 0o777).toBe(0o700)
    expect(statSync(ws.promptFile).mode & 0o777).toBe(0o600)
    expect(statSync(ws.mcpConfigFile).mode & 0o777).toBe(0o600)
    expect(readFileSync(ws.promptFile, 'utf8')).toBe('Fix the bug in APP-12.')
    expect(JSON.parse(readFileSync(ws.mcpConfigFile, 'utf8'))).toEqual({
      mcpServers: { aictiq: { command: 'aictiq', args: ['mcp'] } },
    })
    // Outside the checkout, so the agent cannot commit them.
    expect(ws.promptFile.startsWith(ws.checkout)).toBe(false)
    expect(ws.mcpConfigFile.startsWith(ws.checkout)).toBe(false)
    expect(git(ws.checkout, 'status', '--porcelain')).toBe('')

    expect(events).toContain(`Fetching origin in ${local}`)
    expect(events.some((e) => e.startsWith('Created worktree') && e.includes('origin/main'))).toBe(
      true,
    )

    await ws.cleanup()
    expect(existsSync(ws.runDir)).toBe(false)
    expect(git(local, 'worktree', 'list')).not.toContain(ws.checkout)
    // The branch survives: the agent may have pushed it or opened a pull request from it.
    expect(git(local, 'branch', '--list', ws.branch)).toContain(ws.branch)
  })

  it('leaves everything in place when keeping workspaces', async () => {
    const ws = await provisionWorkspace(claimed(), options({ keep: true }))
    await ws.cleanup()

    expect(existsSync(ws.checkout)).toBe(true)
    expect(git(local, 'worktree', 'list')).toContain(ws.checkout)
  })

  it('reuses the branch of a retried run', async () => {
    const first = await provisionWorkspace(claimed(), options())
    writeFileSync(join(first.checkout, 'fix.txt'), 'fixed\n')
    git(first.checkout, 'add', '.')
    git(first.checkout, 'commit', '-m', 'attempt one')
    const tip = git(first.checkout, 'rev-parse', 'HEAD').trim()
    await first.cleanup()

    const second = await provisionWorkspace(
      claimed({ runId: '0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5c' }),
      options(),
    )
    expect(git(second.checkout, 'rev-parse', 'HEAD').trim()).toBe(tip)
    expect(git(second.checkout, 'rev-parse', '--abbrev-ref', 'HEAD').trim()).toBe(
      'aictiq/app-12-fix-the-bug',
    )
    await second.cleanup()
  })

  it('branches from the local default branch when there is no origin', async () => {
    git(local, 'remote', 'remove', 'origin')
    const ws = await provisionWorkspace(claimed(), options())

    expect(git(ws.checkout, 'rev-parse', 'HEAD').trim()).toBe(
      git(local, 'rev-parse', 'main').trim(),
    )
    await ws.cleanup()
  })

  it('fails with no-local-repository when the project is not mapped', async () => {
    const error = await failure(
      provisionWorkspace(
        claimed({}, { localPathHint: '~/src/app' }),
        options({ repositories: {} }),
      ),
    )

    expect(error.reason).toBe('no-local-repository')
    expect(error.message).toContain('runner.json')
    expect(error.message).toContain('~/src/app')
    expect(existsSync(join(root, claimed().runId))).toBe(false)
  })

  it('fails with no-local-repository when the mapped path is not a git repository', async () => {
    const plain = join(scratch, 'plain')
    mkdirSync(plain)
    const error = await failure(
      provisionWorkspace(
        claimed(),
        options({
          repositories: { APP: plain },
          env: { ...gitEnv, GIT_CEILING_DIRECTORIES: scratch },
        }),
      ),
    )

    expect(error.reason).toBe('no-local-repository')
  })

  describe('path hint under repoRoots', () => {
    const hinted = (hint: string, repoRoots: string[]) =>
      provisionWorkspace(
        claimed({}, { localPathHint: hint }),
        options({ repositories: {}, repoRoots }),
      )

    it('uses the hint when it lies inside a repository root', async () => {
      const ws = await hinted(`${local}/`, [scratch])

      expect(git(ws.checkout, 'rev-parse', '--abbrev-ref', 'HEAD').trim()).toBe(
        claimed().branchName,
      )
      await ws.cleanup()
    })

    it('prefers an explicit mapping over the hint', async () => {
      const other = join(scratch, 'other')
      git(scratch, 'clone', origin, other)
      const ws = await provisionWorkspace(
        claimed({}, { localPathHint: other }),
        options({ repositories: { APP: local }, repoRoots: [scratch] }),
      )

      expect(git(local, 'branch', '--list', claimed().branchName)).toContain(claimed().branchName)
      expect(git(other, 'branch', '--list', claimed().branchName)).toBe('')
      await ws.cleanup()
    })

    it('ignores the hint when there are no repository roots', async () => {
      const error = await failure(hinted(local, []))

      expect(error.reason).toBe('no-local-repository')
      expect(error.message).toContain('repoRoots')
    })

    it('refuses a hint outside every root, including through ..', async () => {
      const elsewhere = join(scratch, 'elsewhere')
      mkdirSync(elsewhere)

      const outside = await failure(hinted(local, [elsewhere]))
      expect(outside.reason).toBe('no-local-repository')
      expect(outside.message).toContain(elsewhere)

      const dotted = await failure(hinted(join(elsewhere, '..', 'local'), [elsewhere]))
      expect(dotted.reason).toBe('no-local-repository')
    })

    it('refuses a symlink inside a root that points outside it', async () => {
      const roots = join(scratch, 'roots')
      mkdirSync(roots)
      symlinkSync(local, join(roots, 'link'))

      const error = await failure(hinted(join(roots, 'link'), [roots]))
      expect(error.reason).toBe('no-local-repository')
    })

    it('refuses a directory whose enclosing repository is outside the root', async () => {
      const sub = join(local, 'sub')
      mkdirSync(sub)

      const error = await failure(hinted(sub, [sub]))
      expect(error.reason).toBe('no-local-repository')
      expect(error.message).toContain('not a git repository under')
    })
  })

  it('fails with workspace-failed and git output when the default branch does not exist', async () => {
    const error = await failure(provisionWorkspace(claimed({ defaultBranch: 'trunk' }), options()))

    expect(error.reason).toBe('workspace-failed')
    expect(error.message).toMatch(/trunk/)
  })

  it('refuses a branch name git would read as an option', async () => {
    const error = await failure(provisionWorkspace(claimed({ branchName: '--force' }), options()))

    expect(error.reason).toBe('workspace-failed')
  })
})

describe('github workspaces', () => {
  const token = 'ghs_installationTokenSecret123'
  const github = (repo: Partial<ClaimedRun['repo']> = {}) =>
    claimed({}, { source: 'github', repoFullName: 'owner/app', cloneToken: token, ...repo })

  it('clones without writing the token anywhere in the checkout', async () => {
    const ws = await provisionWorkspace(
      github(),
      options({ repositories: {}, githubBaseUrl: `file://${scratch}` }),
    )

    expect(git(ws.checkout, 'rev-parse', '--abbrev-ref', 'HEAD').trim()).toBe(
      'aictiq/app-12-fix-the-bug',
    )
    expect(git(ws.checkout, 'rev-parse', 'HEAD').trim()).toBe(
      git(origin, 'rev-parse', 'main').trim(),
    )

    const config = readFileSync(join(ws.checkout, '.git', 'config'), 'utf8')
    expect(config).not.toContain(token)
    expect(git(ws.checkout, 'remote', 'get-url', 'origin')).not.toContain(token)
    // The push credential comes from the harness's environment, not from a stored secret.
    expect(config).toContain('AICTIQ_GIT_TOKEN')
    expect(ws.env).toEqual({ AICTIQ_GIT_TOKEN: token })

    const askpass = join(ws.runDir, 'askpass.sh')
    expect(existsSync(askpass)).toBe(true)
    expect(askpass.startsWith(ws.checkout)).toBe(false)
    expect(statSync(askpass).mode & 0o777).toBe(0o700)
    expect(readFileSync(askpass, 'utf8')).not.toContain(token)
    expect(
      execFileSync(askpass, ['Username for https://github.com: '], { encoding: 'utf8' }).trim(),
    ).toBe('x-access-token')
    expect(
      execFileSync(askpass, ['Password for https://x-access-token@github.com: '], {
        encoding: 'utf8',
        env: { AICTIQ_GIT_TOKEN: token },
      }).trim(),
    ).toBe(token)

    // The helper answers `get` with the token from the environment and nothing else.
    const filled = execFileSync('git', ['credential', 'fill'], {
      cwd: ws.checkout,
      env: { ...gitEnv, AICTIQ_GIT_TOKEN: token },
      input: 'protocol=https\nhost=github.com\n\n',
      encoding: 'utf8',
    })
    expect(filled).toContain(`password=${token}`)

    expect(events).toContain('Cloned owner/app (depth 50)')
    expect(events.join('\n')).not.toContain(token)

    await ws.cleanup()
    expect(existsSync(ws.runDir)).toBe(false)
  })

  it('keeps the default branch for GitHub direct runs and can push without creating an item branch', async () => {
    const ws = await provisionWorkspace(
      { ...github(), workOnDefaultBranch: true, branchName: 'main' },
      options({ githubBaseUrl: `file://${scratch}` }),
    )
    expect(git(ws.checkout, 'branch', '--show-current').trim()).toBe('main')
    expect(git(ws.checkout, 'branch', '--list')).not.toContain('app-12')
    writeFileSync(join(ws.checkout, 'fix.txt'), 'fixed\n')
    git(ws.checkout, 'add', '.')
    git(ws.checkout, 'commit', '-m', 'APP-12 fix')
    git(ws.checkout, 'push')
    expect(git(origin, 'log', '-1', '--format=%s').trim()).toBe('APP-12 fix')
    await ws.cleanup()
  })

  it('asks for a fresh token when the claim carried none', async () => {
    let asked = 0
    const ws = await provisionWorkspace(
      github({ cloneToken: null }),
      options({
        githubBaseUrl: `file://${scratch}`,
        refreshCloneToken: async () => {
          asked++
          return token
        },
      }),
    )

    expect(asked).toBe(1)
    expect(ws.env).toEqual({ AICTIQ_GIT_TOKEN: token })
    await ws.cleanup()
  })

  it('fails with repository-token-unavailable when no token can be had', async () => {
    const error = await failure(
      provisionWorkspace(
        github({ cloneToken: null }),
        options({ githubBaseUrl: `file://${scratch}`, refreshCloneToken: async () => null }),
      ),
    )

    expect(error.reason).toBe('repository-token-unavailable')
    expect(existsSync(join(root, claimed().runId))).toBe(false)
  })
})

describe('kept workspaces', () => {
  const continueRun = (previous: string) =>
    claimed({
      runId: '0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a99',
      resume: { continuesRunId: previous, sessionId: 'sess-1', failureReason: 'harness-rate-limited' },
    })

  it('reattaches a continue run to the failed run’s checkout, edits and all', async () => {
    const ws = await provisionWorkspace(claimed(), options())
    writeFileSync(join(ws.checkout, 'half-done.txt'), 'work in progress\n')
    ws.retain('sess-1')
    const kept = JSON.parse(readFileSync(join(ws.runDir, 'kept.json'), 'utf8'))
    expect(kept).toMatchObject({ runId: claimed().runId, itemKey: 'APP-12', sessionId: 'sess-1', source: local })

    const resumed = await reattachWorkspace(continueRun(claimed().runId), 'Continue.', options())
    expect(resumed.checkout).toBe(ws.checkout)
    expect(readFileSync(join(resumed.checkout, 'half-done.txt'), 'utf8')).toBe('work in progress\n')
    expect(readFileSync(resumed.promptFile, 'utf8')).toBe('Continue.')
    // In use: the retention sweep must not see it while the continue run works.
    expect(existsSync(join(ws.runDir, 'kept.json'))).toBe(false)

    await resumed.cleanup()
    expect(existsSync(ws.runDir)).toBe(false)
    expect(git(local, 'worktree', 'list').trim().split('\n')).toHaveLength(1)
  })

  it('finds the workspace again when a continue run is itself continued', async () => {
    const ws = await provisionWorkspace(claimed(), options())
    ws.retain('sess-1')
    const second = continueRun(claimed().runId)
    const resumed = await reattachWorkspace(second, 'Continue.', options())
    resumed.retain('sess-1')
    // The marker now names the second run, while the directory is still the first run's.
    expect(JSON.parse(readFileSync(join(ws.runDir, 'kept.json'), 'utf8')).runId).toBe(second.runId)

    const third = claimed({
      runId: '0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4aaa',
      resume: { continuesRunId: second.runId, sessionId: 'sess-1', failureReason: null },
    })
    const again = await reattachWorkspace(third, 'Continue again.', options())
    expect(again.checkout).toBe(ws.checkout)
    expect(readFileSync(again.promptFile, 'utf8')).toBe('Continue again.')
    await again.cleanup()
    expect(existsSync(ws.runDir)).toBe(false)
  })

  it('fails with session-unavailable when the workspace is gone', async () => {
    const error = await failure(reattachWorkspace(continueRun('0199-gone'), 'Continue.', options()))
    expect(error.reason).toBe('session-unavailable')
  })

  it('fails with session-unavailable for a workspace that was cleaned up rather than kept', async () => {
    const ws = await provisionWorkspace(claimed(), options({ keep: true }))
    const error = await failure(reattachWorkspace(continueRun(claimed().runId), 'Continue.', options()))
    expect(error.reason).toBe('session-unavailable')
    expect(existsSync(ws.checkout)).toBe(true)
  })

  it('removes kept workspaces after five days, beyond the limit, and worktrees holding the branch of an item’s next run', async () => {
    const now = Date.parse('2026-10-01T12:00:00Z')
    const keep = (name: string, hoursAgo: number, itemKey = 'APP-1', source: string | null = null) => {
      const dir = join(root, name)
      mkdirSync(join(dir, 'repo'), { recursive: true })
      writeFileSync(
        join(dir, 'kept.json'),
        JSON.stringify({
          runId: name, organizationSlug: 'acme', itemKey, sessionId: 's', source,
          keptAt: new Date(now - hoursAgo * 3_600_000).toISOString(),
        }),
      )
    }
    keep('expired', 5 * 24 + 1)
    keep('four-days', 4 * 24)
    for (let i = 1; i <= 6; i++) keep(`run-${i}`, i)
    keep('other-clone', 0.5, 'APP-2')
    keep('other-worktree', 0.25, 'APP-2', local)
    mkdirSync(join(root, 'in-flight', 'repo'), { recursive: true })

    expect(await pruneKeptWorkspaces(root, { now })).toEqual(['expired'])
    expect(existsSync(join(root, 'four-days'))).toBe(true)
    expect(existsSync(join(root, 'in-flight'))).toBe(true)

    expect((await pruneKeptWorkspaces(root, { now, max: 5 })).sort()).toEqual(['four-days', 'run-4', 'run-5', 'run-6'])

    // A clone holds no branch the item's next run needs; only the worktree has to go.
    expect(await pruneKeptWorkspaces(root, { now, item: { organizationSlug: 'acme', itemKey: 'APP-2' } }))
      .toEqual(['other-worktree'])
    expect(existsSync(join(root, 'other-clone'))).toBe(true)
    expect(existsSync(join(root, 'run-1'))).toBe(true)
  })
})

describe('multi-repository workspaces', () => {
  const token = 'ghs_backendTokenSecret456'
  const entry = (overrides: Partial<RunRepoEntry>): RunRepoEntry => ({
    alias: 'backend',
    source: 'github',
    repoFullName: 'owner/app',
    cloneToken: token,
    localPathHint: null,
    defaultBranch: 'main',
    workingDirectory: null,
    primary: false,
    ...overrides,
  })
  const multi = (repos: RunRepoEntry[], overrides: Partial<ClaimedRun> = {}) =>
    claimed({ repos, ...overrides })

  it('checks out a GitHub and a local repository under repo/<alias>/ on the run branch, each with its own token variable', async () => {
    const ws = await provisionWorkspace(
      multi([entry({ alias: 'backend' }), entry({ alias: 'frontend', source: 'local', repoFullName: null, cloneToken: null, primary: true })]),
      options({ repositories: { APP: local }, githubBaseUrl: `file://${scratch}` }),
    )

    const backend = join(ws.checkout, 'backend')
    const frontend = join(ws.checkout, 'frontend')
    expect(ws.checkout).toBe(join(ws.runDir, 'repo'))
    expect(ws.cwd).toBe(ws.checkout)
    expect(ws.checkouts?.map((c) => [c.alias, c.path, c.source])).toEqual([
      ['backend', backend, null],
      ['frontend', frontend, local],
    ])
    for (const dir of [backend, frontend]) {
      expect(git(dir, 'branch', '--show-current').trim()).toBe('aictiq/app-12-fix-the-bug')
    }
    expect(ws.env).toEqual({ AICTIQ_GIT_TOKEN_1: token })
    const config = readFileSync(join(backend, '.git', 'config'), 'utf8')
    expect(config).toContain('AICTIQ_GIT_TOKEN_1')
    expect(config).not.toContain(token)
    const filled = execFileSync('git', ['credential', 'fill'], {
      cwd: backend,
      env: { ...gitEnv, AICTIQ_GIT_TOKEN_1: token },
      input: 'protocol=https\nhost=github.com\n\n',
      encoding: 'utf8',
    })
    expect(filled).toContain(`password=${token}`)

    await ws.cleanup()
    expect(existsSync(ws.runDir)).toBe(false)
    // The local repository's worktree went with it.
    expect(git(local, 'worktree', 'list')).not.toContain(frontend)
  })

  it('starts a single-repository run in its working directory', async () => {
    const seed = join(scratch, 'seed')
    mkdirSync(join(seed, 'packages', 'api'), { recursive: true })
    writeFileSync(join(seed, 'packages', 'api', 'index.ts'), 'export {}\n')
    git(seed, 'add', '.')
    git(seed, 'commit', '-m', 'api')
    git(seed, 'push', 'origin', 'HEAD:main')

    const ws = await provisionWorkspace(
      multi([entry({ workingDirectory: 'packages/api' })]),
      options({ githubBaseUrl: `file://${scratch}` }),
    )
    expect(ws.cwd).toBe(join(ws.checkout, 'backend', 'packages', 'api'))
    await ws.cleanup()

    const legacy = await provisionWorkspace(
      claimed({ workingDirectory: 'packages/api' }, { source: 'github', repoFullName: 'owner/app', cloneToken: token }),
      options({ githubBaseUrl: `file://${scratch}` }),
    )
    expect(legacy.cwd).toBe(join(legacy.checkout, 'packages', 'api'))
    await legacy.cleanup()
  })

  it('refuses a working directory that is missing or leaves the checkout', async () => {
    const missing = await failure(
      provisionWorkspace(multi([entry({ workingDirectory: 'packages/nope' })]), options({ githubBaseUrl: `file://${scratch}` })),
    )
    expect(missing.reason).toBe('working-directory-missing')
    expect(missing.repository).toBe('backend')

    const escape = await failure(
      provisionWorkspace(multi([entry({ workingDirectory: '../..' })]), options({ githubBaseUrl: `file://${scratch}` })),
    )
    expect(escape.reason).toBe('workspace-failed')
    expect(existsSync(join(root, claimed().runId))).toBe(false)
  })

  it('refreshes each repository’s token by alias and fails naming the one it cannot get', async () => {
    const asked: (string | undefined)[] = []
    const error = await failure(
      provisionWorkspace(
        multi([entry({ cloneToken: null }), entry({ alias: 'sdk', cloneToken: null })]),
        options({
          githubBaseUrl: `file://${scratch}`,
          refreshCloneToken: async (alias) => {
            asked.push(alias)
            return alias === 'backend' ? token : null
          },
        }),
      ),
    )
    expect(asked).toEqual(['backend', 'sdk'])
    expect(error.reason).toBe('repo-token-missing')
    expect(error.repository).toBe('sdk')
    expect(error.message).toContain('sdk')
    expect(existsSync(join(root, claimed().runId))).toBe(false)
  })

  it('looks up another local repository as KEY/alias and names it when it is missing', async () => {
    const run = multi([
      entry({ alias: 'app', source: 'local', repoFullName: null, cloneToken: null, primary: true }),
      entry({ alias: 'sdk', source: 'local', repoFullName: null, cloneToken: null }),
    ])
    const error = await failure(locateLocalRepositories(run, options(), gitEnv))
    expect(error.reason).toBe('no-local-repository')
    expect(error.repository).toBe('sdk')
    expect(error.message).toContain('repository sdk of project APP')
    expect(error.message).toContain('"APP/sdk"')

    await locateLocalRepositories(run, options({ repositories: { APP: local, 'APP/sdk': local } }), gitEnv)
  })

  it('refuses an alias that is not a plain directory name', async () => {
    const error = await failure(provisionWorkspace(multi([entry({ alias: '../x' })]), options()))
    expect(error.reason).toBe('workspace-failed')
  })

  it('reattaches every checkout with fresh tokens and removes the worktrees of a kept workspace', async () => {
    const ws = await provisionWorkspace(
      multi([entry({}), entry({ alias: 'web', source: 'local', repoFullName: null, cloneToken: null, primary: true })]),
      options({ githubBaseUrl: `file://${scratch}` }),
    )
    ws.retain('sess-9')
    const kept = JSON.parse(readFileSync(join(ws.runDir, 'kept.json'), 'utf8'))
    expect(kept.worktrees).toEqual([{ path: join(ws.checkout, 'web'), source: local }])

    const resumed = await reattachWorkspace(
      multi([entry({ cloneToken: null }), entry({ alias: 'web', source: 'local', repoFullName: null, cloneToken: null, primary: true })], {
        runId: '0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a77',
        resume: { continuesRunId: claimed().runId, sessionId: 'sess-9', failureReason: null },
      }),
      'Continue.',
      options({ refreshCloneToken: async (alias) => (alias === 'backend' ? 'ghs_fresh' : null) }),
    )
    expect(resumed.env).toEqual({ AICTIQ_GIT_TOKEN_1: 'ghs_fresh' })
    expect(resumed.checkouts?.map((c) => c.source)).toEqual([null, local])
    await resumed.cleanup()
    expect(existsSync(ws.runDir)).toBe(false)
    expect(git(local, 'worktree', 'list')).not.toContain(join(ws.checkout, 'web'))
  })
})

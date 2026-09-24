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
  provisionWorkspace,
  type WorkspaceOptions,
} from '../../src/runner/workspace.js'
import { RunFailure, type ClaimedRun } from '../../src/runner/types.js'

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

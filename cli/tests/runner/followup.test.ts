import { execFileSync } from 'node:child_process'
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import {
  applyFollowUpTarget,
  followUpBranchNote,
  followUpPrompt,
  resolveFollowUpTarget,
  type FollowUpTarget,
  type PullRequestState,
} from '../../src/runner/followup.js'
import type { RunFollowUp } from '../../src/runner/types.js'

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

const commit = (cwd: string, file: string, message: string) => {
  writeFileSync(join(cwd, file), `${message}\n`)
  git(cwd, 'add', '.')
  git(cwd, 'commit', '-m', message)
}

const Branch = 'aictiq/app-12-fix-the-bug'
const PullRequest = 'https://github.com/acme/app/pull/7'

const followUp = (overrides: Partial<RunFollowUp> = {}): RunFollowUp => ({
  previousRunId: 'run-1',
  sessionId: 'sess-1',
  previousBranchName: Branch,
  pullRequestUrl: PullRequest,
  newBranchName: 'aictiq/app-12-follow-up',
  instruction: 'Rename the flag to `--dry`.\n\nAnd add a test.',
  requestedByName: 'Ana',
  commentId: 'comment-1',
  ...overrides,
})

const state = (value: PullRequestState) => ({ pullRequestState: async () => value })

let scratch: string
let origin: string
let seed: string
let checkout: string
let events: string[]

beforeEach(() => {
  scratch = mkdtempSync(join(tmpdir(), 'aictiq-followup-'))
  origin = join(scratch, 'origin.git')
  git(scratch, 'init', '--bare', '-b', 'main', origin)
  seed = join(scratch, 'seed')
  git(scratch, 'clone', origin, seed)
  commit(seed, 'README.md', 'initial')
  git(seed, 'push', 'origin', 'HEAD:main')
  git(seed, 'checkout', '-b', Branch)
  commit(seed, 'fix.txt', 'first attempt')
  git(seed, 'push', 'origin', Branch)
  git(seed, 'checkout', 'main')

  // The kept checkout: the earlier run's work, on its branch.
  checkout = join(scratch, 'checkout')
  git(scratch, 'clone', origin, checkout)
  git(checkout, 'checkout', Branch)
  events = []
})

afterEach(() => {
  rmSync(scratch, { recursive: true, force: true })
})

describe('resolveFollowUpTarget', () => {
  it('stays on the branch while its pull request is open', async () => {
    expect(await resolveFollowUpTarget(checkout, followUp(), gitEnv, state('open'))).toEqual({
      kind: 'same',
      branch: Branch,
      onOrigin: true,
      pullRequest: 'open',
    })
  })

  it('starts the new branch once the pull request was merged or closed', async () => {
    expect(await resolveFollowUpTarget(checkout, followUp(), gitEnv, state('merged'))).toEqual({
      kind: 'new',
      branch: 'aictiq/app-12-follow-up',
      because: 'merged',
    })
    expect(
      await resolveFollowUpTarget(checkout, followUp(), gitEnv, state('closed')),
    ).toMatchObject({
      kind: 'new',
      because: 'closed',
    })
  })

  it('reports a pull request that can no longer be found', async () => {
    const target = await resolveFollowUpTarget(checkout, followUp(), gitEnv, state('missing'))
    expect(target).toEqual({
      kind: 'missing',
      detail: `I didn't start: the pull request ${PullRequest} from run run-1 can no longer be found.`,
    })
  })

  it('reports an open pull request whose branch is gone everywhere', async () => {
    git(seed, 'push', 'origin', '--delete', Branch)
    git(checkout, 'checkout', 'main')
    git(checkout, 'branch', '-D', Branch)
    const target = await resolveFollowUpTarget(checkout, followUp(), gitEnv, state('open'))
    expect(target).toEqual({
      kind: 'missing',
      detail: `I didn't start: the branch ${Branch} from run run-1 is not on origin or in the kept checkout.`,
    })
  })

  it('falls back to the branch when GitHub cannot be asked, or no pull request was opened', async () => {
    // Only in the checkout: the agent committed but never pushed.
    git(seed, 'push', 'origin', '--delete', Branch)
    expect(await resolveFollowUpTarget(checkout, followUp(), gitEnv, state('unknown'))).toEqual({
      kind: 'same',
      branch: Branch,
      onOrigin: false,
      pullRequest: 'unknown',
    })
    let asked = false
    const target = await resolveFollowUpTarget(
      checkout,
      followUp({ pullRequestUrl: null }),
      gitEnv,
      {
        pullRequestState: async () => {
          asked = true
          return 'open'
        },
      },
    )
    expect(target).toMatchObject({ kind: 'same', pullRequest: null })
    expect(asked).toBe(false)
  })

  it('does not count a branch the fresh workspace just created', async () => {
    git(seed, 'push', 'origin', '--delete', Branch)
    const target = await resolveFollowUpTarget(
      checkout,
      followUp({ pullRequestUrl: null }),
      gitEnv,
      {
        createdBranch: true,
      },
    )
    expect(target.kind).toBe('missing')
  })
})

describe('applyFollowUpTarget', () => {
  const same: FollowUpTarget = { kind: 'same', branch: Branch, onOrigin: true, pullRequest: 'open' }
  const apply = (target: FollowUpTarget, options: { createdBranch?: boolean } = {}) =>
    applyFollowUpTarget(
      checkout,
      followUp(),
      target,
      'main',
      gitEnv,
      (line) => events.push(line),
      options,
    )

  it('fast-forwards a checkout that is only behind the pushed branch', async () => {
    git(seed, 'checkout', Branch)
    git(seed, 'pull', 'origin', Branch)
    commit(seed, 'review.txt', 'review fix')
    git(seed, 'push', 'origin', Branch)
    git(checkout, 'fetch', 'origin')

    await apply(same)
    expect(git(checkout, 'rev-parse', 'HEAD')).toBe(git(origin, 'rev-parse', Branch))
    expect(events).toContain(`Fast-forwarded ${Branch} to origin/${Branch}`)
    expect(events).toContain(`Pull request ${PullRequest} is open; continuing on branch ${Branch}`)
  })

  it('keeps commits the agent never pushed', async () => {
    commit(checkout, 'local.txt', 'unpushed')
    const head = git(checkout, 'rev-parse', 'HEAD')
    await apply(same)
    expect(git(checkout, 'rev-parse', 'HEAD')).toBe(head)
    expect(events).toContain(
      `Branch ${Branch} has commits that are not on origin/${Branch}; keeping them`,
    )
  })

  it('puts a fresh workspace’s empty branch on what was pushed', async () => {
    git(checkout, 'checkout', 'main')
    git(checkout, 'branch', '-D', Branch)
    git(checkout, 'checkout', '-b', Branch)
    await apply(same, { createdBranch: true })
    expect(git(checkout, 'branch', '--show-current').trim()).toBe(Branch)
    expect(git(checkout, 'rev-parse', 'HEAD')).toBe(git(origin, 'rev-parse', Branch))
  })

  it('fetches the branch into a single-branch shallow clone', async () => {
    rmSync(checkout, { recursive: true, force: true })
    git(scratch, 'clone', '--depth', '50', '--branch', 'main', `file://${origin}`, checkout)
    git(checkout, 'checkout', '-b', Branch)
    await apply(same, { createdBranch: true })
    expect(git(checkout, 'rev-parse', 'HEAD')).toBe(git(origin, 'rev-parse', Branch))
  })

  it('starts the new branch from origin’s default branch without tracking it', async () => {
    commit(seed, 'merged.txt', 'merged the pull request')
    git(seed, 'push', 'origin', 'main')
    // As resolveFollowUpTarget does first.
    git(checkout, 'fetch', 'origin')
    await apply({ kind: 'new', branch: 'aictiq/app-12-follow-up', because: 'merged' })

    expect(git(checkout, 'branch', '--show-current').trim()).toBe('aictiq/app-12-follow-up')
    expect(git(checkout, 'rev-parse', 'HEAD')).toBe(git(origin, 'rev-parse', 'main'))
    expect(() => git(checkout, 'rev-parse', '--abbrev-ref', '@{upstream}')).toThrow()
    expect(events).toContain(
      `Pull request ${PullRequest} was merged; starting branch aictiq/app-12-follow-up from origin/main`,
    )
  })

  it('drops the old branch a fresh workspace created only for this run', async () => {
    await apply(
      { kind: 'new', branch: 'aictiq/app-12-follow-up', because: 'closed' },
      { createdBranch: true },
    )
    expect(git(checkout, 'branch', '--list', Branch).trim()).toBe('')
  })
})

describe('follow-up prompts', () => {
  it('tells a resumed session who asked, what, and where to push', () => {
    const prompt = followUpPrompt(followUp(), {
      kind: 'same',
      branch: Branch,
      onOrigin: true,
      pullRequest: 'open',
    })
    expect(prompt).toContain('Ana asked for a follow-up')
    expect(prompt).toContain('> Rename the flag to `--dry`.\n>\n> And add a test.')
    expect(prompt).toContain(`Keep working on branch \`${Branch}\``)
    expect(prompt).toContain(PullRequest)
    expect(prompt).toContain('Do not transition the item')
    expect(prompt).toContain("posted as the reply to Ana's comment")
  })

  it('sends a session whose pull request was merged to the new branch', () => {
    const target: FollowUpTarget = {
      kind: 'new',
      branch: 'aictiq/app-12-follow-up',
      because: 'merged',
    }
    const prompt = followUpPrompt(followUp({ requestedByName: null }), target)
    expect(prompt).toContain('A teammate asked')
    expect(prompt).toContain(`${PullRequest} was merged`)
    expect(prompt).toContain('open a new pull request')
    expect(followUpBranchNote(target)).toContain('not the branch named above')
  })
})
